using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace PdfTranslator.Core;

public enum PageObjectKind { Image, Form, Annotation }

/// <summary>
/// A removable object on a page: a raster image or form XObject drawn by the page content (Index = which "Do"
/// operation), or an annotation such as a stamp (Index = position in /Annots). Bounds are top-left points relative
/// to the crop box. Hidden objects (links, popups) are not offered to the user but are removed with their owner.
/// </summary>
public sealed record PageImage(int Id, RectD Bounds, PageObjectKind Kind, int Index, string Label, bool Hidden = false);

/// <summary>An object the user deleted.</summary>
public sealed record DeletedImage(int Id, RectD Bounds, PageObjectKind Kind, int Index);

/// <summary>Lists and removes images, form XObjects (stamps, watermarks) and annotations in a page's content.</summary>
public static class ImageRemover
{
    readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        /// <summary>this × m (apply this first, then m).</summary>
        public Matrix Then(Matrix m) => new(
            A * m.A + B * m.C, A * m.B + B * m.D,
            C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);
        public (double x, double y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    static PdfRectangle CropOf(PdfPage page) => page.CropBox.Width > 0 ? page.CropBox : page.MediaBox;

    /// <summary>Every top-level image/form draw and every annotation on the page.</summary>
    public static List<PageImage> List(PdfPage page)
    {
        var result = new List<PageImage>();
        var crop = CropOf(page);
        // On the page as displayed (rotated pages included), like the text blocks.
        var frame = new PageFrame(crop.X1, crop.Y1, crop.X2, crop.Y2, page.Rotate);
        RectD ToTopLeft(double x0, double y0, double x1, double y1) => frame.ToDisplay(x0, y0, x1, y1);

        CSequence content;
        try { content = ContentReader.ReadContent(page); }
        catch { content = new CSequence(); }
        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
        var ctm = Matrix.Identity;
        var stack = new Stack<Matrix>();
        int doIndex = -1;
        foreach (var item in content)
        {
            if (item is not COperator op) continue;
            switch (op.OpCode.Name)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm" when op.Operands.Count == 6:
                    ctm = new Matrix(Num(op.Operands[0]), Num(op.Operands[1]), Num(op.Operands[2]),
                                     Num(op.Operands[3]), Num(op.Operands[4]), Num(op.Operands[5])).Then(ctm);
                    break;
                case "Do":
                    doIndex++;
                    if (op.Operands.Count != 1 || op.Operands[0] is not CName name || xobjects == null) break;
                    var xo = XObject(xobjects, name.Name);
                    if (xo == null || DrawBounds(xo, ctm) is not { } b) break;
                    var bounds = ToTopLeft(b.x0, b.y0, b.x1, b.y1);
                    if (bounds.W < 1 || bounds.H < 1) break;
                    bool image = xo.Elements.GetName("/Subtype") == "/Image";
                    result.Add(new PageImage(result.Count + 1, bounds, image ? PageObjectKind.Image : PageObjectKind.Form, doIndex,
                        image ? $"Image {bounds.W:0}×{bounds.H:0} pt" : $"Form object {bounds.W:0}×{bounds.H:0} pt (stamp / watermark / graphic)"));
                    break;
            }
        }

        var annots = page.Elements.GetArray("/Annots");
        if (annots != null)
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                if (Deref(annots.Elements[i]) is not { } a) continue;
                string subtype = a.Elements.GetName("/Subtype");
                var rect = a.Elements.GetArray("/Rect");
                if (rect == null || rect.Elements.Count < 4) continue;
                double x0 = Math.Min(rect.Elements.GetReal(0), rect.Elements.GetReal(2)), x1 = Math.Max(rect.Elements.GetReal(0), rect.Elements.GetReal(2));
                double y0 = Math.Min(rect.Elements.GetReal(1), rect.Elements.GetReal(3)), y1 = Math.Max(rect.Elements.GetReal(1), rect.Elements.GetReal(3));
                bool hidden = subtype is "/Link" or "/Popup" or "/Widget";
                result.Add(new PageImage(result.Count + 1, ToTopLeft(x0, y0, x1, y1), PageObjectKind.Annotation, i,
                    $"{subtype.TrimStart('/')} annotation", hidden));
            }
        return result;
    }

    /// <summary>Removes the given objects from the page (draw operations from the content, entries from /Annots).</summary>
    public static void Remove(PdfPage page, IReadOnlyCollection<DeletedImage> objects)
    {
        var draws = objects.Where(o => o.Kind != PageObjectKind.Annotation).Select(o => o.Index).ToHashSet();
        if (draws.Count > 0)
        {
            try
            {
                var content = ContentReader.ReadContent(page);
                int doIndex = -1;
                var drop = new List<CObject>();
                foreach (var item in content)
                    if (item is COperator { OpCode.Name: "Do" } op && draws.Contains(++doIndex))
                        drop.Add(op);
                if (drop.Count > 0)
                {
                    foreach (var d in drop) content.Remove(d);
                    page.Contents.ReplaceContent(content);
                }
            }
            catch { /* unreadable content: leave the page as it is */ }
        }

        var annots = page.Elements.GetArray("/Annots");
        var indexes = objects.Where(o => o.Kind == PageObjectKind.Annotation).Select(o => o.Index).ToHashSet();
        if (annots == null || indexes.Count == 0) return;
        // An annotation's popup goes with it.
        var removed = indexes.Where(i => i < annots.Elements.Count).Select(i => Deref(annots.Elements[i])).OfType<PdfDictionary>().ToList();
        for (int i = 0; i < annots.Elements.Count; i++)
            if (Deref(annots.Elements[i]) is { } a && a.Elements.GetName("/Subtype") == "/Popup" &&
                Deref(a.Elements["/Parent"]) is { } parent && removed.Contains(parent))
                indexes.Add(i);
        foreach (var i in indexes.Where(i => i < annots.Elements.Count).OrderByDescending(i => i))
            annots.Elements.RemoveAt(i);
    }

    static PdfDictionary? Deref(PdfItem? item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    static PdfDictionary? XObject(PdfDictionary xobjects, string name) =>
        xobjects.Elements.GetDictionary(name) ?? Deref(xobjects.Elements[name]);

    /// <summary>Page-space bounds of an XObject drawn under <paramref name="ctm"/> (PDF coordinates, y up).</summary>
    static (double x0, double y0, double x1, double y1)? DrawBounds(PdfDictionary xo, Matrix ctm)
    {
        string subtype = xo.Elements.GetName("/Subtype");
        double bx0 = 0, by0 = 0, bx1 = 1, by1 = 1;
        var m = ctm;
        if (subtype == "/Form")
        {
            var bbox = xo.Elements.GetArray("/BBox");
            if (bbox == null || bbox.Elements.Count < 4) return null;
            bx0 = bbox.Elements.GetReal(0); by0 = bbox.Elements.GetReal(1);
            bx1 = bbox.Elements.GetReal(2); by1 = bbox.Elements.GetReal(3);
            var fm = xo.Elements.GetArray("/Matrix");
            if (fm != null && fm.Elements.Count == 6)
                m = new Matrix(fm.Elements.GetReal(0), fm.Elements.GetReal(1), fm.Elements.GetReal(2),
                               fm.Elements.GetReal(3), fm.Elements.GetReal(4), fm.Elements.GetReal(5)).Then(ctm);
        }
        else if (subtype != "/Image") return null;

        var pts = new[] { m.Apply(bx0, by0), m.Apply(bx1, by0), m.Apply(bx0, by1), m.Apply(bx1, by1) };
        return (pts.Min(p => p.x), pts.Min(p => p.y), pts.Max(p => p.x), pts.Max(p => p.y));
    }

    static double Num(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };

    /// <summary>Diagnostic listing of a page's objects.</summary>
    public static IEnumerable<string> Describe(PdfPage page) =>
        List(page).Select(o => $"#{o.Id} {o.Kind} [{o.Index}] {o.Label}{(o.Hidden ? " (hidden)" : "")} at x={o.Bounds.X:0} y={o.Bounds.Y:0} w={o.Bounds.W:0} h={o.Bounds.H:0}");
}

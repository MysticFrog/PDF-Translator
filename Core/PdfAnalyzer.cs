using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PdfTranslator.Core;

/// <summary>Pulls letters (with size, weight, colour) and image placements out of the PDF text layer.</summary>
public sealed class PdfAnalyzer : IDisposable
{
    readonly PdfDocument _doc;

    public PdfAnalyzer(byte[] pdf) => _doc = PdfDocument.Open(pdf);

    public int PageCount => _doc.NumberOfPages;

    public sealed record PageLayout(PageResult Page, List<RectD> Images, int LetterCount);

    /// <summary>
    /// PdfPig already places content on the page as displayed: turned upright on a rotated page, and measured
    /// from the visible box's bottom-left corner (a page box starting at [-12 12] is not offset again), with
    /// Width/Height as displayed. Only the flip to a top-left origin is left.
    /// </summary>
    static PageFrame FrameOf(Page page) => new(0, 0, page.Width, page.Height, 0);

    static RectD ToDisplay(PageFrame frame, PdfRectangle r) => frame.ToDisplay(r.Left, r.Bottom, r.Right, r.Top);

    /// <summary>Page geometry only (size as displayed, crop offset, rotation).</summary>
    public PageResult Geometry(int pageIndex)
    {
        var page = _doc.GetPage(pageIndex + 1);
        var crop = page.CropBox.Bounds;
        var media = page.MediaBox.Bounds;
        var frame = FrameOf(page);
        return new PageResult
        {
            Index = pageIndex,
            Width = frame.Width,
            Height = frame.Height,
            CropOffsetX = crop.Left - media.Left,
            CropOffsetY = media.Top - crop.Top,
            Rotation = page.Rotation.Value,
            Frame = frame,
        };
    }

    public PageLayout Analyze(int pageIndex, Rules rules)
    {
        var page = _doc.GetPage(pageIndex + 1);
        var result = Geometry(pageIndex);
        var frame = result.Frame;

        var glyphs = new List<Glyph>();
        // Letters already placed, by text: the same text drawn again on top (a shadow, an outline, fake bold)
        // is one letter, not two.
        var placed = new Dictionary<string, List<(double x, double y)>>();
        foreach (var l in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(l.Value) || l.TextOrientation != TextOrientation.Horizontal) continue;
            double size = l.PointSize > 0.5 ? l.PointSize : Math.Max(4, l.GlyphRectangle.Height);
            var (x0, baseline) = frame.ToDisplay(l.StartBaseLine.X, l.StartBaseLine.Y);
            if (placed.TryGetValue(l.Value, out var same)
                && same.Any(p => Math.Abs(p.x - x0) < size * 0.15 && Math.Abs(p.y - baseline) < size * 0.15)) continue;
            if (same == null) placed[l.Value] = same = new();
            same.Add((x0, baseline));
            // A dingbat font's arrow or check mark that reads as a letter ("Æ") is a picture, not text: it is
            // carried as an icon inside the translated sentence.
            if (IsDingbatFont(l.FontName) && l.Value.Any(char.IsLetterOrDigit))
            {
                var g = l.GlyphRectangle;
                var icon = frame.ToDisplay(g.Left, g.Bottom, g.Right, g.Top);
                glyphs.Add(new Glyph
                {
                    Box = icon.W > 0.5 && icon.H > 0.5 ? icon : new RectD(x0, baseline - size * 0.82, size, size),
                    Text = Rules.InlineMarker, Size = size, Baseline = baseline, Color = ColorOf(l), Font = l.FontName ?? "",
                });
                continue;
            }
            // Advance-width em boxes: tight glyph outlines make narrow glyphs ("1", "）") look like word gaps.
            double adv = Math.Abs(l.EndBaseLine.X - l.StartBaseLine.X);
            if (adv < 0.05) adv = Math.Max(l.Width, l.GlyphRectangle.Width);
            var box = new RectD(x0, baseline - size * 0.82, Math.Max(0.1, adv), size);
            glyphs.Add(new Glyph
            {
                Box = box,
                Text = l.Value,
                Size = size,
                Baseline = baseline,
                Bold = IsBold(l),
                Italic = IsItalic(l),
                Color = ColorOf(l),
                Font = l.FontName ?? "",
            });
        }

        if (glyphs.Count > 0)
        {
            var lines = LayoutBuilder.LinesFromLetters(glyphs, rules);
            foreach (var region in LayoutBuilder.BuildBlocks(lines, rules, RegionSource.TextLayer, result.Width))
                if (region.Source.Any(char.IsLetterOrDigit)) result.Regions.Add(region);
        }

        var images = new List<RectD>();
        foreach (var img in page.GetImages())
        {
            var r = ToDisplay(frame, img.BoundingBox);
            // Small images matter too: captions and labels are often pasted in as 8-10 pt tall pictures.
            if (r.W > 12 && r.H > 4) images.Add(r);
        }
        return new PageLayout(result, images, glyphs.Count);
    }

    /// <summary>Every image drawn on the page, in drawing order (top-left points on the page as displayed).</summary>
    public List<RectD> Images(int pageIndex)
    {
        var page = _doc.GetPage(pageIndex + 1);
        var frame = FrameOf(page);
        return page.GetImages()
            .Select(i => ToDisplay(frame, i.BoundingBox))
            .Where(r => r.W > 2 && r.H > 2)
            .ToList();
    }

    /// <summary>Diagnostic listing of the letters on a page (value, font, size, rendering mode, detected style).</summary>
    public IEnumerable<string> DumpLetters(int pageIndex)
    {
        var page = _doc.GetPage(pageIndex + 1);
        yield return $"page {page.Width:0.#}×{page.Height:0.#} rotate={page.Rotation.Value} " +
                     $"media={page.MediaBox.Bounds} crop={page.CropBox.Bounds}";
        foreach (var l in page.Letters)
            yield return $"{l.Value} U+{(l.Value.Length > 0 ? ((int)l.Value[0]).ToString("X4") : "----")} " +
                         $"x={l.StartBaseLine.X:0.0} y={l.StartBaseLine.Y:0.0} size={l.PointSize:0.#} font={l.FontName} " +
                         $"mode={l.RenderingMode} bold={IsBold(l)} italic={IsItalic(l)}";
    }

    /// <summary>Picture fonts (arrows, check marks, pointing hands), whose letters are not text.</summary>
    static bool IsDingbatFont(string? name)
    {
        string f = (name ?? "").ToLowerInvariant();
        return f.Contains("wingding") || f.Contains("webding") || f.Contains("dingbat") || f.Contains("marlett");
    }

    static bool IsBold(Letter l)
    {
        // Word/WPS export synthetic bold for CJK fonts as fill+stroke text.
        int mode = (int)l.RenderingMode;
        if (mode is 2 or 6) return true;
        var d = l.Font;
        if (d != null && (d.IsBold || d.Weight >= 600)) return true;
        string f = (l.FontName ?? "").ToLowerInvariant();
        return f.Contains("bold") || f.Contains("black") || f.Contains("heavy") || f.Contains("semibold") || f.Contains("demi");
    }

    static bool IsItalic(Letter l)
    {
        if (l.Font?.IsItalic == true) return true;
        string f = (l.FontName ?? "").ToLowerInvariant();
        return f.Contains("italic") || f.Contains("oblique");
    }

    static Rgb ColorOf(Letter l)
    {
        try
        {
            var (r, g, b) = l.Color.ToRGBValues();
            return new Rgb((byte)Math.Clamp(r * 255, 0, 255), (byte)Math.Clamp(g * 255, 0, 255), (byte)Math.Clamp(b * 255, 0, 255));
        }
        catch { return Rgb.Black; }
    }

    /// <summary>Joins wrapped lines into one paragraph, undoing end-of-line hyphenation.</summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
                    sb.Length--;
                else if (!TextTools.IsCjk(sb[^1]) || !TextTools.IsCjk(line[0]))
                    sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    public void Dispose() => _doc.Dispose();
}

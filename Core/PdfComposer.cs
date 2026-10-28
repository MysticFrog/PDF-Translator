using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfTranslator.Core;

/// <summary>
/// Writes the translated PDF: every original page is copied as-is (vector graphics, images, fonts untouched),
/// then each translated region is covered with its sampled background colour and the translation is drawn
/// into its box. Sizes are fitted per block, then normalised so blocks that shared a style keep sharing a size.
/// </summary>
public static class PdfComposer
{
    static PdfComposer() => EnsureFonts();

    static bool _fontsReady;

    /// <summary>Installs the Windows font resolver once; also needed by anything else that draws with PDFsharp.</summary>
    public static void EnsureFonts()
    {
        if (_fontsReady) return;
        _fontsReady = true;
        GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

    public static void Compose(byte[] original, IReadOnlyList<PageResult?> pages,
        IReadOnlyDictionary<int, List<DeletedImage>> deletedImages, string targetLanguage, Stream output,
        IReadOnlyCollection<int>? onlyPages = null)
    {
        using var input = PdfReader.Open(Streams.Exposable(original), PdfDocumentOpenMode.Import);
        using var doc = new PdfDocument();
        doc.Info.Title = (input.Info.Title is { Length: > 0 } t ? t + " " : "") + $"({Languages.NameOf(targetLanguage)})";
        doc.Info.Creator = "PDF Translator";

        for (int i = 0; i < input.PageCount; i++)
        {
            if (onlyPages != null && !onlyPages.Contains(i)) continue;
            var page = doc.AddPage(input.Pages[i]);
            var result = i < pages.Count ? pages[i] : null;

            // Crop-box offset inside the media box (our coordinates are crop-relative, drawing is media-relative).
            var crop = page.CropBox.Width > 0 ? page.CropBox : page.MediaBox;
            double ox = crop.X1 - page.MediaBox.X1, oy = page.MediaBox.Y2 - crop.Y2;

            // Deleted images, stamps and annotations are removed from the page itself, not painted over.
            if (deletedImages.TryGetValue(i, out var deleted) && deleted.Count > 0)
                ImageRemover.Remove(page, deleted);

            bool overlay = result != null && result.Regions.Any(r => r.HasOverlay);
            if (!overlay) continue;

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            // PDFsharp draws as if the media box started at (0, 0); a page whose box starts elsewhere (Excel
            // exports: [-12 12 583 854]) would get everything shifted by that origin.
            gfx.TranslateTransform(ox + page.MediaBox.X1, oy - page.MediaBox.Y1);
            // Regions are on the page as displayed; a rotated page is drawn on unrotated.
            var frame = new PageFrame(crop.X1, crop.Y1, crop.X2, crop.Y2, page.Rotate);
            if (frame.Quarter != 0)
            {
                var (m11, m12, m21, m22, dx, dy) = frame.DisplayToPage();
                gfx.MultiplyTransform(new XMatrix(m11, m12, m21, m22, dx, dy));
            }
            DrawPage(gfx, result!, targetLanguage);
            if (Environment.GetEnvironmentVariable("PDFT_PROBE") == "1")
                gfx.DrawRectangle(XBrushes.Red, 40, 40, 6, 6);   // diagnostic: a mark at (40, 40) on the page as displayed
        }
        doc.Save(output, false);
    }

    public static byte[] ComposePage(byte[] original, IReadOnlyList<PageResult?> pages,
        IReadOnlyDictionary<int, List<DeletedImage>> deletedImages, string targetLanguage, int pageIndex)
    {
        using var ms = new MemoryStream();
        Compose(original, pages, deletedImages, targetLanguage, ms, new[] { pageIndex });
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ layout

    sealed record Run(string Text, bool Bold);

    sealed class Plan
    {
        public required TextRegion Region { get; init; }
        public required List<Run> Runs { get; init; }
        public required string Family { get; init; }
        public RectD Box;            // where text starts (x, y) and its nominal width
        public double Width;         // wrap width
        public double MaxHeight;     // height the text may use
        public double Top;           // highest point the text may start at
        public double Bottom;        // lowest point the text may reach
        public double Pitch;         // line pitch as a multiple of the font size
        public double Size;
        public List<List<(string text, bool bold)>> Lines = new();
        public double Widest;
        public double Hang;          // wrapped lines start this far right of Box.X (hanging indent)
        public bool LabelTab;        // the first token is a label ("Me:"): the text after it starts at Hang
    }

    static void DrawPage(XGraphics gfx, PageResult page, string target)
    {
        // Contents lines ("Title ........ 1-2"): the translation carries the page ref after a tab.
        var contents = page.Regions.Where(r => r.HasOverlay && r.Translated!.Contains('\t')).ToList();
        DrawContentsLines(gfx, contents, target);

        var plans = page.Regions.Where(r => r.HasOverlay && !contents.Contains(r)).Select(r => MakePlan(r, target)).ToList();

        // Pass 1: the largest size at which each block fits on its own.
        foreach (var p in plans)
            p.Size = p.Region.FontSizeOverride ?? Fit(gfx, p);

        // Pass 2: blocks that shared a source style share an output size (the group's median), unless the
        // block can't fit at that size. Stops headings/body text from drifting in size block to block.
        foreach (var group in plans.Where(p => p.Region.FontSizeOverride == null)
                     .GroupBy(p => (Math.Round(p.Region.FontSize * 2) / 2, p.Region.Bold, p.Region.Origin == RegionSource.TextLayer)))
        {
            var sizes = group.Select(p => p.Size).OrderBy(v => v).ToList();
            double median = sizes[sizes.Count / 2];
            foreach (var p in group) p.Size = Math.Min(p.Size, median);
        }

        foreach (var p in plans)
        {
            Layout(gfx, p, p.Size);
            Draw(gfx, p);
        }
    }

    static Plan MakePlan(TextRegion r, string target)
    {
        string text = r.Translated!.Trim();
        var plan = new Plan { Region = r, Runs = SplitRuns(r, text), Family = FamilyFor(r, target, text) };
        var box = r.DrawBounds;
        plan.Box = box;
        plan.Width = box.W;
        plan.MaxHeight = box.H;
        plan.Top = box.Y;
        plan.Bottom = box.Bottom;
        if (r.OutBounds == null)
        {
            if (r.LineCount == 1 && r.MaxRight > box.Right)
            {
                double extra = r.MaxRight - box.Right;
                if (r.Align == TextAlign.Center)
                {
                    extra = Math.Max(0, Math.Min(extra, box.X - 18));
                    plan.Box = box with { X = box.X - extra };
                    plan.Width = box.W + 2 * extra;
                }
                else plan.Width = box.W + extra;
            }
            plan.Bottom = Math.Max(box.Bottom, r.MaxBottom);
            plan.Top = r.MinTop > 0 && r.MinTop < box.Y ? r.MinTop : box.Y;
            plan.MaxHeight = plan.Bottom - plan.Top;
        }
        // Hanging indent from the source (left-aligned text only, and only while it leaves room to wrap).
        if (r.Align == TextAlign.Left && r.Hang > 0 && r.Hang < plan.Width * 0.5)
        {
            plan.Hang = r.Hang;
            var first = text.Split(' ', 2)[0];
            plan.LabelTab = first.Length is > 1 and <= 12 && (first.EndsWith(':') || first.EndsWith('：'));
        }
        double sourcePitch = r.LineCount > 1 ? r.Bounds.H / r.LineCount / r.FontSize : 1.2;
        plan.Pitch = Languages.IsCjk(target) ? Math.Clamp(sourcePitch, 1.1, 1.8) : Math.Clamp(sourcePitch, 1.12, 1.35);
        return plan;
    }

    /// <summary>Largest size (down from the source size) at which the text fits the plan's area.</summary>
    static double Fit(XGraphics gfx, Plan p)
    {
        double size = Math.Max(4, p.Region.FontSize);
        double min = Math.Max(3, Math.Min(5, p.Region.FontSize * 0.45));
        while (true)
        {
            Layout(gfx, p, size);
            double needed = size * (1 + (p.Lines.Count - 1) * p.Pitch);
            if ((needed <= p.MaxHeight + size * 0.25 && p.Widest <= p.Width * 1.02) || size <= min) return size;
            size = Math.Max(min, size * 0.94);
        }
    }

    static void Layout(XGraphics gfx, Plan p, double size)
    {
        p.Size = size;
        p.Lines = new();
        var cur = new List<(string text, bool bold)>();
        double curW = 0;
        p.Widest = 0;

        // Lines after the first start at the hanging indent; a label on the first line tabs to it.
        double Indent(int line) => line == 0 ? 0 : p.Hang;
        void Flush()
        {
            while (cur.Count > 0 && cur[^1].text.Trim().Length == 0) cur.RemoveAt(cur.Count - 1);
            if (cur.Count > 0) cur[^1] = (cur[^1].text.TrimEnd(), cur[^1].bold);
            double w = Indent(p.Lines.Count) + cur.Sum(t => Measure(gfx, p, t.text, t.bold));
            if (p.Lines.Count == 0 && p.LabelTab && cur.Count > 1)
                w = Math.Max(w, p.Hang + cur.Skip(1).Sum(t => Measure(gfx, p, t.text, t.bold)));
            p.Widest = Math.Max(p.Widest, w);
            p.Lines.Add(cur);
            cur = new();
            curW = Indent(p.Lines.Count);
        }

        foreach (var run in p.Runs)
            foreach (var para in SplitKeep(run.Text))
            {
                if (para == "\n") { Flush(); continue; }
                foreach (var token in Tokens(para))
                {
                    double w = Measure(gfx, p, token, run.Bold);
                    double wTrim = Measure(gfx, p, token.TrimEnd(), run.Bold);
                    if (cur.Count > 0 && curW + wTrim > p.Width)
                    {
                        Flush();
                        var t = token.TrimStart();
                        if (t.Length == 0) continue;
                        cur.Add((t, run.Bold));
                        curW += Measure(gfx, p, t, run.Bold);
                    }
                    else
                    {
                        cur.Add((token, run.Bold));
                        curW += w;
                        // The label fits before the text column: the text continues from there.
                        if (p.Lines.Count == 0 && cur.Count == 1 && p.LabelTab && curW < p.Hang) curW = p.Hang;
                    }
                }
            }
        if (cur.Count > 0) Flush();
    }

    static double Measure(XGraphics gfx, Plan p, string text, bool bold)
    {
        if (text.Length == 0) return 0;
        if (IconOf(p.Region, text) is { } icon)
        {
            double w = IconSize(p, icon).w;
            return text.EndsWith(' ') ? w + gfx.MeasureString(" ", MakeFont(p.Family, p.Size, Style(p.Region, bold))).Width : w;
        }
        return gfx.MeasureString(text, MakeFont(p.Family, p.Size, Style(p.Region, bold))).Width;
    }

    /// <summary>The inline icon a {{n}} token refers to, or null for ordinary text.</summary>
    static InlineGraphic? IconOf(TextRegion r, string token)
    {
        var t = token.Trim();
        if (t.Length < 5 || !t.StartsWith("{{") || !t.EndsWith("}}") || !int.TryParse(t[2..^2], out int n)) return null;
        return n >= 1 && n <= r.Inlines.Count && r.Inlines[n - 1].Png.Length > 0 ? r.Inlines[n - 1] : null;
    }

    /// <summary>Icons scale with the text: same ratio to the output size as they had to the source size.</summary>
    static (double w, double h) IconSize(Plan p, InlineGraphic icon)
    {
        double k = p.Size / Math.Max(1, p.Region.FontSize);
        return (icon.Bounds.W * k, icon.Bounds.H * k);
    }

    static void Draw(XGraphics gfx, Plan p)
    {
        var r = p.Region;
        // Blocks are em boxes (ascender to descender), so a small pad hides anti-aliasing; covers never reach past
        // the free space measured around the block, so table rules and neighbouring graphics survive.
        // Text in images carries anti-aliasing / JPEG halos well past the glyph edges; pad those covers more.
        double pad = r.Origin == RegionSource.TextLayer ? Math.Clamp(r.FontSize * 0.08, 0.6, 1.5) : Math.Clamp(r.FontSize * 0.2, 1.2, 2.5);
        double limTop = Math.Min(r.Bounds.Y, p.Top) - pad, limBottom = Math.Max(r.Bounds.Bottom, p.Bottom) + pad;
        double limRight = Math.Max(r.Bounds.Right, r.MaxRight) + pad;
        var bg = new XSolidBrush(XColor.FromArgb(r.Background.R, r.Background.G, r.Background.B));
        void Cover(double x0, double y0, double x1, double y1)
        {
            if (r.OutBounds == null) { y0 = Math.Max(y0, limTop); y1 = Math.Min(y1, limBottom); x1 = Math.Min(x1, limRight); }
            if (x1 > x0 && y1 > y0) gfx.DrawRectangle(bg, x0, y0, x1 - x0, y1 - y0);
        }
        Cover(r.Bounds.X - pad, r.Bounds.Y - pad, r.Bounds.Right + pad, r.Bounds.Bottom + pad);

        double size = p.Size;
        double textH = size * (1 + (p.Lines.Count - 1) * p.Pitch) + size * 0.25;
        // Start where the source started; move up into free space only when the extra lines need it.
        double top = p.Box.Y;
        if (top + textH > p.Bottom + size * 0.25) top = Math.Max(p.Top, p.Bottom + size * 0.25 - textH);
        if (r.OutBounds == null)
        {
            // Also clear the area the (possibly larger) translation occupies; it was verified as empty background.
            double x0 = p.Box.X, w = Math.Max(p.Widest, r.Bounds.W);
            if (r.Align == TextAlign.Center) { x0 = p.Box.X + (p.Width - p.Widest) / 2; w = p.Widest; }
            Cover(x0 - pad, top - pad, x0 + w + pad, top + textH);
        }

        var brush = new XSolidBrush(XColor.FromArgb(r.TextColor.R, r.TextColor.G, r.TextColor.B));
        double y = top + size * 0.85;
        for (int li = 0; li < p.Lines.Count; li++)
        {
            var line = p.Lines[li];
            double lineW = line.Sum(t => Measure(gfx, p, t.text, t.bold));
            double x = p.Box.X + r.Align switch
            {
                TextAlign.Center => (p.Width - lineW) / 2,
                TextAlign.Right => p.Width - lineW,
                _ => li == 0 ? 0 : p.Hang,
            };
            for (int ti = 0; ti < line.Count; ti++)
            {
                var (text, bold) = line[ti];
                if (li == 0 && ti == 1 && p.LabelTab) x = Math.Max(x, p.Box.X + p.Hang);
                if (text.Length == 0) continue;
                if (IconOf(r, text) is { } icon)
                {
                    // Centred on the text's x-height band, the way it sat in the source line.
                    var (iw, ih) = IconSize(p, icon);
                    using (var img = XImage.FromStream(Streams.Exposable(icon.Png)))
                        gfx.DrawImage(img, x, y - size * 0.35 - ih / 2, iw, ih);
                    x += Measure(gfx, p, text, bold);
                    continue;
                }
                var font = MakeFont(p.Family, size, Style(r, bold));
                gfx.DrawString(text, font, brush, x, y, XStringFormats.BaseLineLeft);
                x += gfx.MeasureString(text, font).Width;
            }
            y += size * p.Pitch;
        }
    }

    /// <summary>
    /// Draws contents lines: title on the left, page reference right-aligned at the line's end, dot leaders between.
    /// All lines of the same source size share one output size so the contents stay even.
    /// </summary>
    static void DrawContentsLines(XGraphics gfx, List<TextRegion> lines, string target)
    {
        foreach (var group in lines.GroupBy(r => Math.Round(r.FontSize * 2) / 2))
        {
            double size = group.Min(r => r.FontSizeOverride ?? FitContentsLine(gfx, r, target));
            foreach (var r in group) DrawContentsLine(gfx, r, target, r.FontSizeOverride ?? size);
        }
    }

    static double FitContentsLine(XGraphics gfx, TextRegion r, string target)
    {
        var (title, pageRef) = SplitTab(r.Translated!);
        string family = FamilyFor(r, target, title);
        var box = r.DrawBounds;
        double size = r.FontSize, min = Math.Max(4, r.FontSize * 0.6);
        while (size > min)
        {
            var font = MakeFont(family, size, Style(r, r.Bold));
            double need = gfx.MeasureString(title, font).Width + gfx.MeasureString(" .... " + pageRef, font).Width;
            if (need <= box.W) break;
            size *= 0.95;
        }
        return Math.Max(min, size);
    }

    static void DrawContentsLine(XGraphics gfx, TextRegion r, string target, double size)
    {
        var (title, pageRef) = SplitTab(r.Translated!);
        var box = r.DrawBounds;
        double pad = Math.Clamp(r.FontSize * 0.08, 0.6, 1.5);
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(r.Background.R, r.Background.G, r.Background.B)),
            r.Bounds.X - pad, r.Bounds.Y - pad, r.Bounds.W + 2 * pad, r.Bounds.H + 2 * pad);

        string family = FamilyFor(r, target, title);
        var font = MakeFont(family, size, Style(r, r.Bold));
        var plain = MakeFont(family, size, Style(r, false));
        var brush = new XSolidBrush(XColor.FromArgb(r.TextColor.R, r.TextColor.G, r.TextColor.B));
        double y = box.Y + box.H / 2 + size * 0.32;
        double titleW = gfx.MeasureString(title, font).Width;
        double refW = gfx.MeasureString(pageRef, font).Width;
        gfx.DrawString(title, font, brush, box.X, y, XStringFormats.BaseLineLeft);
        gfx.DrawString(pageRef, font, brush, box.Right - refW, y, XStringFormats.BaseLineLeft);

        // Dot leaders filling the gap, with a little air at both ends.
        double from = box.X + titleW + size * 0.4, to = box.Right - refW - size * 0.4;
        double dotW = gfx.MeasureString(".", plain).Width;
        int count = dotW > 0 ? (int)((to - from) / dotW) : 0;
        if (count > 2) gfx.DrawString(new string('.', count), plain, brush, to - count * dotW, y, XStringFormats.BaseLineLeft);
    }

    static (string title, string pageRef) SplitTab(string s)
    {
        int i = s.LastIndexOf('\t');
        return (s[..i].Trim(), s[(i + 1)..].Trim());
    }

    /// <summary>Bold label + regular body when the source block started with a bold "Label:".</summary>
    static List<Run> SplitRuns(TextRegion r, string text)
    {
        if (r.BoldLead)
        {
            int i = text.IndexOfAny(new[] { ':', '：' });
            if (i > 0 && i < Math.Min(60, text.Length - 1))
                return new() { new Run(text[..(i + 1)], true), new Run(text[(i + 1)..], false) };
        }
        return new() { new Run(text, r.Bold) };
    }

    static XFontStyleEx Style(TextRegion r, bool bold) =>
        bold && r.Italic ? XFontStyleEx.BoldItalic : bold ? XFontStyleEx.Bold : r.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular;

    static readonly Dictionary<(string, double, XFontStyleEx), XFont> FontCache = new();

    static XFont MakeFont(string family, double size, XFontStyleEx style)
    {
        size = Math.Round(size, 2);
        lock (FontCache)
        {
            if (FontCache.TryGetValue((family, size, style), out var f)) return f;
            try { f = new XFont(family, size, style); }
            catch { f = new XFont("Arial", size, style); }
            FontCache[(family, size, style)] = f;
            return f;
        }
    }

    static string FamilyFor(TextRegion r, string target, string text)
    {
        var special = Languages.FontFor(target);
        if (special != null) return special;
        // Untranslated CJK left in a Latin-target block still needs glyphs.
        var guess = TextTools.GuessLanguage(text);
        if (guess != null && Languages.FontFor(guess) is { } f) return f;

        string h = r.FontFamilyHint.ToLowerInvariant();
        if (h.Contains("courier") || h.Contains("mono") || h.Contains("consol")) return "Courier New";
        if (h.Contains("times") || h.Contains("serif") && !h.Contains("sans") || h.Contains("roman") || h.Contains("georgia") ||
            h.Contains("garamond") || h.Contains("cambria") || h.Contains("palatino") || h.Contains("minion") ||
            h.Contains("song") || h.Contains("simsun") || h.Contains("宋") || h.Contains("ming") || h.Contains("fangsong") || h.Contains("kai"))
            return "Times New Roman";
        if (h.Contains("calibri")) return "Calibri";
        if (h.Contains("verdana")) return "Verdana";
        if (h.Contains("tahoma")) return "Tahoma";
        if (h.Contains("segoe")) return "Segoe UI";
        return "Arial";
    }

    static IEnumerable<string> SplitKeep(string s)
    {
        var parts = s.Replace("\r", "").Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) yield return "\n";
            yield return parts[i];
        }
    }

    /// <summary>Word tokens with their trailing space; CJK characters and {{n}} icons are individual tokens.</summary>
    static IEnumerable<string> Tokens(string s)
    {
        foreach (var (part, icon) in TextTools.SplitInline(s))
        {
            if (icon > 0) { yield return part; continue; }
            foreach (var t in TextTokens(part)) yield return t;
        }
    }

    static IEnumerable<string> TextTokens(string s)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            string el = e.GetTextElement();
            char c = el[0];
            if (TextTools.IsCjk(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return el;
            }
            else if (c == ' ')
            {
                sb.Append(el);
                yield return sb.ToString();
                sb.Clear();
            }
            else sb.Append(el);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

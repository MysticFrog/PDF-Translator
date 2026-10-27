using System.Text;

namespace PdfTranslator.Core;

/// <summary>A positioned piece of text: a single letter (text layer) or a word (OCR).</summary>
public sealed class Glyph
{
    public required RectD Box { get; init; }
    public required string Text { get; init; }
    public double Size { get; init; }
    public double Baseline { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public Rgb Color { get; init; }
    public string Font { get; init; } = "";
}

/// <summary>A run of glyphs on one baseline that belongs together (not split by a wide gap or a table border).</summary>
public sealed class LineSeg
{
    public required List<Glyph> Glyphs { get; init; }
    public RectD Bounds { get; set; }
    public string Text { get; set; } = "";
    public double Size { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool BoldLead { get; set; }
    public Rgb Color { get; set; }
    public string Font { get; set; } = "";
}

/// <summary>
/// Table borders and rules found in the rendered page: thin, long, dark horizontal and vertical strokes.
/// Text is never grouped across them, so table cells stay separate blocks.
/// </summary>
public sealed class Rules
{
    public List<RectD> Horizontal { get; } = new();
    public List<RectD> Vertical { get; } = new();

    public IEnumerable<RectD> All => Horizontal.Concat(Vertical);

    /// <summary>Marks a graphic inside a line of text; replaced by {{n}} placeholders in the block's text.</summary>
    public const string InlineMarker = "\uFFFC";

    /// <summary>
    /// Ink found in a gap of a text line (gap rectangle in points, font size) - an icon drawn in the sentence.
    /// Null when the gap is empty. Set by <see cref="Detect"/> from the rendered page.
    /// </summary>
    public Func<RectD, double, RectD?>? InlineGraphicAt { get; private set; }

    public static Rules Detect(RasterPage page)
    {
        var rules = new Rules();
        rules.InlineGraphicAt = (gap, size) => InkBounds(page, gap);
        double s = page.Scale;
        int minLen = (int)(18 * s);          // at least 18pt long
        double maxThick = 3.2 * s;           // at most ~3pt thick (thicker = filled shape, not a rule)

        bool Dark(int x, int y) => page.Luma(x, y) < 165;

        // Horizontal runs per row, merged across adjacent rows into rectangles.
        var open = new List<(int x0, int x1, int y0, int y1)>();
        var done = new List<(int x0, int x1, int y0, int y1)>();
        for (int y = 0; y < page.Height; y++)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int x = 0; x <= page.Width; x++)
            {
                bool d = x < page.Width && Dark(x, y);
                if (d && start < 0) start = x;
                else if (!d && start >= 0) { if (x - start >= minLen) runs.Add((start, x)); start = -1; }
            }
            var next = new List<(int, int, int, int)>();
            foreach (var (a, b) in runs)
            {
                int idx = open.FindIndex(o => o.y1 == y - 1 && Math.Min(o.x1, b) - Math.Max(o.x0, a) > (b - a) / 2);
                if (idx >= 0) { var o = open[idx]; open.RemoveAt(idx); next.Add((Math.Min(o.x0, a), Math.Max(o.x1, b), o.y0, y)); }
                else next.Add((a, b, y, y));
            }
            done.AddRange(open);
            open = next;
        }
        done.AddRange(open);
        foreach (var (x0, x1, y0, y1) in done)
            if (y1 - y0 + 1 <= maxThick)
                rules.Horizontal.Add(new RectD(x0 / s, y0 / s, (x1 - x0) / s, (y1 - y0 + 1) / s));

        // Vertical runs per column.
        open.Clear();
        done.Clear();
        for (int x = 0; x < page.Width; x++)
        {
            var runs = new List<(int, int)>();
            int start = -1;
            for (int y = 0; y <= page.Height; y++)
            {
                bool d = y < page.Height && Dark(x, y);
                if (d && start < 0) start = y;
                else if (!d && start >= 0) { if (y - start >= minLen) runs.Add((start, y)); start = -1; }
            }
            var next = new List<(int, int, int, int)>();
            foreach (var (a, b) in runs)
            {
                int idx = open.FindIndex(o => o.x1 == x - 1 && Math.Min(o.y1, b) - Math.Max(o.y0, a) > (b - a) / 2);
                if (idx >= 0) { var o = open[idx]; open.RemoveAt(idx); next.Add((o.x0, x, Math.Min(o.y0, a), Math.Max(o.y1, b))); }
                else next.Add((x, x, a, b));
            }
            done.AddRange(open);
            open = next.Select(n => (n.Item1, n.Item2, n.Item3, n.Item4)).ToList();
        }
        done.AddRange(open);
        foreach (var (x0, x1, y0, y1) in done)
            if (x1 - x0 + 1 <= maxThick)
                rules.Vertical.Add(new RectD(x0 / s, y0 / s, (x1 - x0 + 1) / s, (y1 - y0) / s));
        return rules;
    }

    /// <summary>Bounding box of the ink inside <paramref name="area"/> (points), or null when it is blank.</summary>
    static RectD? InkBounds(RasterPage page, RectD area)
    {
        double s = page.Scale;
        int x0 = Math.Max(0, (int)Math.Ceiling(area.X * s)), x1 = Math.Min(page.Width - 1, (int)(area.Right * s));
        int y0 = Math.Max(0, (int)(area.Y * s)), y1 = Math.Min(page.Height - 1, (int)Math.Ceiling(area.Bottom * s));
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (page.Luma(x, y) < 170)
                {
                    if (x < bx0) bx0 = x;
                    if (x > bx1) bx1 = x;
                    if (y < by0) by0 = y;
                    if (y > by1) by1 = y;
                }
        if (bx1 < 0) return null;
        return new RectD(bx0 / s, by0 / s, (bx1 - bx0 + 1) / s, (by1 - by0 + 1) / s);
    }

    /// <summary>A vertical rule runs between x-positions <paramref name="x0"/> and <paramref name="x1"/> at height y.</summary>
    public bool VerticalBetween(double x0, double x1, double y) =>
        Vertical.Any(v => v.X + v.W / 2 > x0 - 0.5 && v.X + v.W / 2 < x1 + 0.5 && v.Y <= y && v.Bottom >= y);

    /// <summary>A horizontal rule lies between two lines (one above the other) where they overlap horizontally.</summary>
    public bool HorizontalBetween(RectD upper, RectD lower)
    {
        double x0 = Math.Min(upper.X, lower.X), x1 = Math.Max(upper.Right, lower.Right);
        double minW = Math.Min(upper.W, lower.W);
        return Horizontal.Any(h =>
            h.CenterY > upper.CenterY && h.CenterY < lower.CenterY &&
            Math.Min(h.Right, x1) - Math.Max(h.X, x0) > minW * 0.5 &&
            !IsUnderline(h, upper, lower));
    }

    /// <summary>
    /// A line drawn under text (underlined words) rather than between rows: it hugs the upper line's bottom, stays
    /// within that line's width, and is much nearer to it than to the next line. Table borders span the cell.
    /// </summary>
    static bool IsUnderline(RectD h, RectD upper, RectD lower)
    {
        double em = upper.H;
        double toUpper = h.CenterY - upper.Bottom, toLower = lower.Y - h.CenterY;
        bool withinText = h.X >= upper.X - 0.3 * em && h.Right <= upper.Right + 0.3 * em;
        return withinText && toUpper < 0.3 * em && toLower > Math.Max(toUpper, 0) * 2 + 0.15 * em;
    }
}

/// <summary>
/// Groups glyphs into lines and lines into blocks. Unlike generic page segmenters it is conservative: blocks
/// never cross table rules, change font size or weight, or swallow list items and headings.
/// </summary>
public static class LayoutBuilder
{
    /// <summary>Clusters letters by baseline, then splits each cluster at wide gaps and vertical rules.</summary>
    public static List<LineSeg> LinesFromLetters(List<Glyph> letters, Rules rules)
    {
        var result = new List<LineSeg>();
        var sorted = letters.OrderBy(g => g.Baseline).ThenBy(g => g.Box.X).ToList();
        var cluster = new List<Glyph>();
        double clusterBase = 0, clusterSize = 0;
        foreach (var g in sorted)
        {
            if (cluster.Count > 0 && g.Baseline - clusterBase > Math.Max(1, 0.35 * Math.Min(clusterSize, g.Size)))
            {
                result.AddRange(SplitRow(cluster, rules));
                cluster = new();
            }
            if (cluster.Count == 0) { clusterBase = g.Baseline; clusterSize = g.Size; }
            cluster.Add(g);
        }
        if (cluster.Count > 0) result.AddRange(SplitRow(cluster, rules));
        return result;
    }

    /// <summary>OCR: each recognised line becomes one or more segments (split at wide gaps and vertical rules).</summary>
    public static List<LineSeg> LinesFromWords(List<List<Glyph>> ocrLines, Rules rules) =>
        ocrLines.SelectMany(l => SplitRow(l, rules)).ToList();

    static IEnumerable<LineSeg> SplitRow(List<Glyph> row, Rules rules)
    {
        var glyphs = row.OrderBy(g => g.Box.X).ToList();
        var seg = new List<Glyph>();
        foreach (var g in glyphs)
        {
            if (seg.Count > 0)
            {
                var prev = seg[^1];
                double size = Math.Max(prev.Size, g.Size);
                double gap = g.Box.X - prev.Box.Right;
                double cy = (prev.Box.CenterY + g.Box.CenterY) / 2;
                bool sizeJump = Math.Max(prev.Size, g.Size) / Math.Max(0.1, Math.Min(prev.Size, g.Size)) > 1.6;
                bool rule = rules.VerticalBetween(prev.Box.Right, g.Box.X, cy);
                if (gap > 1.6 * size && !rule && gap < 10 * size && rules.InlineGraphicAt != null)
                {
                    // An icon drawn inside the sentence ("press the ▲ key"): keep the line whole and mark the icon.
                    double top = Math.Min(prev.Box.Y, g.Box.Y) - 0.45 * size, bottom = Math.Max(prev.Box.Bottom, g.Box.Bottom) + 0.45 * size;
                    var ink = rules.InlineGraphicAt(new RectD(prev.Box.Right + 0.5, top, gap - 1, bottom - top), size);
                    if (ink is { } icon && icon.W >= 0.5 * size && icon.H >= 0.4 * size && icon.H <= 2.4 * size)
                    {
                        seg.Add(new Glyph { Box = icon, Text = Rules.InlineMarker, Size = size, Baseline = prev.Baseline, Color = prev.Color, Font = prev.Font });
                        seg.Add(g);
                        continue;
                    }
                }
                if (gap > 1.6 * size || rule || sizeJump && gap > 0.5 * size)
                {
                    yield return Finish(seg);
                    seg = new();
                }
            }
            seg.Add(g);
        }
        if (seg.Count > 0) yield return Finish(seg);
    }

    static LineSeg Finish(List<Glyph> glyphs)
    {
        // Line geometry comes from the text: a taller inline icon must not shift the line's top or bottom,
        // or line spacing looks uneven and the paragraph splits. The icons still widen it sideways.
        var all = RectD.Union(glyphs.Select(g => g.Box));
        var text = glyphs.Where(g => g.Text != Rules.InlineMarker).ToList();
        var bounds = text.Count == 0 ? all : RectD.Union(text.Select(g => g.Box));
        var line = new LineSeg { Glyphs = glyphs, Bounds = new RectD(all.X, bounds.Y, all.W, bounds.H) };

        // Text with spaces restored from gaps (many PDFs position words without space glyphs).
        var sb = new StringBuilder();
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (i > 0)
            {
                var p = glyphs[i - 1];
                double gap = g.Box.X - p.Box.Right;
                bool cjk = TextTools.IsCjk(p.Text[^1]) && TextTools.IsCjk(g.Text[0]);
                bool mixed = !cjk && (TextTools.IsCjk(p.Text[^1]) || TextTools.IsCjk(g.Text[0]));
                if (sb.Length > 0 && sb[^1] != ' ' && g.Text[0] != ' ' && gap > Math.Max(p.Size, g.Size) * (cjk ? 1.0 : mixed ? 0.3 : 0.18))
                    sb.Append(' ');
            }
            sb.Append(g.Text);
        }
        // Icons read as their own word: " {{n}} " (numbered when the block is assembled).
        line.Text = sb.ToString().Replace(Rules.InlineMarker, " " + Rules.InlineMarker + " ").Replace("  ", " ").Trim();

        // Dominant style by character count.
        int total = glyphs.Sum(g => g.Text.Length);
        line.Size = glyphs.GroupBy(g => Math.Round(g.Size * 2) / 2).MaxBy(gr => gr.Sum(g => g.Text.Length))!.Key;
        int boldChars = glyphs.Where(g => g.Bold).Sum(g => g.Text.Length);
        line.Italic = glyphs.Where(g => g.Italic).Sum(g => g.Text.Length) * 2 > total;
        line.Color = glyphs.GroupBy(g => g.Color).MaxBy(gr => gr.Count())!.Key;
        line.Font = glyphs.GroupBy(g => g.Font).MaxBy(gr => gr.Count())!.Key;

        // Bold label at the start ("原因分析：", "Note:") followed by regular text.
        int lead = 0;
        while (lead < glyphs.Count && glyphs[lead].Bold) lead++;
        string leadText = string.Concat(glyphs.Take(lead).Select(g => g.Text)).Trim();
        bool restRegular = glyphs.Skip(lead).Count(g => g.Bold) * 4 < Math.Max(1, glyphs.Count - lead);
        if (lead > 0 && lead < glyphs.Count && restRegular && leadText.Length <= 24 && (leadText.EndsWith(':') || leadText.EndsWith('：')))
        {
            line.BoldLead = true;
            line.Bold = false;
        }
        else line.Bold = boldChars * 2 > total;
        return line;
    }

    /// <summary>Merges wrapped lines into paragraphs; anything that looks like a new item starts a new block.</summary>
    public static List<TextRegion> BuildBlocks(List<LineSeg> lines, Rules rules, RegionSource origin, double pageWidth)
    {
        var blocks = new List<List<LineSeg>>();
        var pitch = TypicalPitch(lines);
        foreach (var line in lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            // A line with a label beside it on the same row ("Me:", "민호:", "Q1") starts a new entry, so
            // dialogue and definition lists don't run speakers together.
            bool labelled = lines.Any(o => !ReferenceEquals(o, line) && o.Bounds.Right <= line.Bounds.X + 0.5 &&
                line.Bounds.X - o.Bounds.Right < 8 * line.Size && o.Bounds.W < 8 * line.Size &&
                o.Text.TrimEnd() is { Length: > 0 and <= 16 } label && (label.EndsWith(':') || label.EndsWith('：')) &&
                VerticalOverlap(o.Bounds, line.Bounds) > 0.5 * Math.Min(o.Bounds.H, line.Bounds.H));
            // ...and the label itself ("나:" with its text to the right) never continues the entry above.
            bool isLabel = line.Text.TrimEnd() is { Length: > 0 and <= 16 } own && (own.EndsWith(':') || own.EndsWith('：')) &&
                lines.Any(o => !ReferenceEquals(o, line) && o.Bounds.X >= line.Bounds.Right - 0.5 &&
                    o.Bounds.X - line.Bounds.Right < 8 * line.Size &&
                    VerticalOverlap(o.Bounds, line.Bounds) > 0.5 * Math.Min(o.Bounds.H, line.Bounds.H));
            List<LineSeg>? best = null;
            double bestGap = double.MaxValue;
            if (!labelled && !isLabel)
                foreach (var block in blocks)
                    if (CanAppend(block, line, rules, origin != RegionSource.TextLayer, pitch, out double gap) && gap < bestGap) { best = block; bestGap = gap; }
            if (best != null) best.Add(line);
            else blocks.Add(new List<LineSeg> { line });
        }
        return blocks.Select(b => ToRegion(b, origin, pageWidth)).ToList();
    }

    /// <param name="ocr">OCR lines: only tightly stacked lines (a wrapped label) belong together; stacked
    /// labels in diagrams sit further apart.</param>
    /// <summary>
    /// The usual distance from one line to the next for each text size on the page (median), so a blank line
    /// between paragraphs is recognised even where it is smaller than the generic spacing limit. Only body
    /// lines (at least half the widest line) count: table cells are packed more tightly than paragraphs.
    /// </summary>
    static Dictionary<double, double> TypicalPitch(List<LineSeg> lines)
    {
        var pitches = new Dictionary<double, List<double>>();
        if (lines.Count == 0) return new();
        double wide = 0.5 * lines.Max(l => l.Bounds.W);
        var body = lines.Where(l => l.Bounds.W >= wide).ToList();
        foreach (var l in body)
        {
            var next = body.Where(o => o.Bounds.Y > l.Bounds.Y + 0.5 * l.Size && Math.Abs(o.Size - l.Size) < 0.12 * l.Size &&
                                        o.Bounds.HorizontalOverlap(l.Bounds) > 0)
                            .MinBy(o => o.Bounds.Y);
            if (next == null || next.Bounds.Y - l.Bounds.Y > 3 * l.Size) continue;
            double key = Math.Round(l.Size);
            (pitches.TryGetValue(key, out var list) ? list : pitches[key] = new()).Add(next.Bounds.Y - l.Bounds.Y);
        }
        return pitches.Where(kv => kv.Value.Count >= 4)
                      .ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(v => v).ElementAt(kv.Value.Count / 2));
    }

    static bool CanAppend(List<LineSeg> block, LineSeg line, Rules rules, bool ocr, Dictionary<double, double> typicalPitch, out double gap)
    {
        var last = block[^1];
        var first = block[0];
        double size = last.Size;
        gap = line.Bounds.Y - last.Bounds.Bottom;

        if (line.Bounds.Y <= last.Bounds.Y + size * 0.5) return false;                  // must be below
        if (gap < -0.3 * size || gap > (ocr ? 0.5 : 1.6) * size) return false;          // line spacing
        if (Math.Abs(line.Size - last.Size) > 0.12 * Math.Max(line.Size, last.Size)) return false;
        if (line.Bold != last.Bold || line.Italic != last.Italic) return false;
        if (line.BoldLead) return false;                                               // "Label: ..." starts an item
        // Clearly more space than this page puts between the lines of a paragraph: a new paragraph.
        if (typicalPitch.TryGetValue(Math.Round(last.Size), out double usual) && line.Bounds.Y - last.Bounds.Y > 1.4 * usual) return false;
        if (TextTools.StartsWithListMarker(line.Text)) return false;
        if (TextTools.HasLeader(last.Text) || TextTools.HasLeader(line.Text)) return false;   // contents lines
        if (rules.HorizontalBetween(last.Bounds, line.Bounds)) return false;
        // A line in another script is a different text, e.g. the translation printed under the original.
        var blockScript = TextTools.ClearScript(string.Concat(block.Select(l => l.Text)));
        var lineScript = TextTools.ClearScript(line.Text);
        if (blockScript != Script.None && lineScript != Script.None && blockScript != lineScript) return false;

        // Left edges line up (the first line may be indented, or hang left of the rest).
        double left = block.Count > 1 ? block[1].Bounds.X : first.Bounds.X;
        if (block.Count == 1)
        {
            // A numbered item or "Note: 1、" may wrap under its text rather than under the marker.
            double indent = first.Bounds.X - line.Bounds.X;
            double hang = TextTools.StartsWithListMarker(first.Text) || first.BoldLead || StartsWithLabel(first.Text) ? 4.5 : 1.2;
            if (indent < -hang * size || indent > 4.5 * size) return false;
        }
        else if (Math.Abs(line.Bounds.X - left) > 1.2 * size) return false;

        // The previous line must run (nearly) to the block's right edge; a short line ends its paragraph.
        double right = Math.Max(block.Max(l => l.Bounds.Right), line.Bounds.Right);
        if (last.Bounds.Right < right - Math.Max(2.5 * size, (right - left) * 0.12)) return false;
        if (last.Text.EndsWith(':') || last.Text.EndsWith('：')) return false;

        // Keep line pitch consistent within a paragraph.
        if (block.Count >= 2)
        {
            double pitch = (last.Bounds.Y - first.Bounds.Y) / (block.Count - 1);
            if (Math.Abs(line.Bounds.Y - last.Bounds.Y - pitch) > 0.45 * size) return false;
        }
        return line.Bounds.HorizontalOverlap(RectD.Union(block.Select(l => l.Bounds))) > 0;
    }

    /// <summary>"Me: …", "민호: …": a short label and colon, with the text wrapping under the text.</summary>
    static bool StartsWithLabel(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"^\S{1,10}[:：]\s");

    static double VerticalOverlap(RectD a, RectD b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);

    static TextRegion ToRegion(List<LineSeg> lines, RegionSource origin, double pageWidth)
    {
        // The block covers its inline icons too, so the originals are painted over.
        var bounds = RectD.Union(lines.Select(l => l.Bounds).Concat(lines.SelectMany(l => l.Glyphs).Select(g => g.Box)));
        var region = new TextRegion
        {
            Bounds = bounds,
            Source = PdfAnalyzer.JoinLines(lines.Select(l => l.Text)),
            Lines = lines.Select(l => new SourceLine(l.Bounds, l.Text)).ToList(),
            LineCount = lines.Count,
            FontSize = lines.GroupBy(l => l.Size).MaxBy(g => g.Sum(l => l.Text.Length))!.Key,
            Bold = lines.Count(l => l.Bold) * 2 > lines.Count,
            Italic = lines.Count(l => l.Italic) * 2 > lines.Count,
            BoldLead = lines[0].BoldLead,
            TextColor = lines[0].Color,
            FontFamilyHint = lines[0].Font,
            Origin = origin,
        };
        // Number the inline icons in reading order: {{1}}, {{2}}…
        var icons = lines.SelectMany(l => l.Glyphs).Where(g => g.Text == Rules.InlineMarker).ToList();
        if (icons.Count > 0)
        {
            int n = 0;
            region.Source = System.Text.RegularExpressions.Regex.Replace(region.Source, Rules.InlineMarker, _ => "{{" + ++n + "}}");
            region.Lines = region.Lines.Select(l => l with { Text = l.Text.Replace(Rules.InlineMarker, "{{?}}") }).ToList();
            region.Inlines = icons.Select(g => new InlineGraphic(g.Box, Array.Empty<byte>())).ToList();
        }
        if (lines.Count == 1 && !TextTools.StartsWithListMarker(lines[0].Text) && Math.Abs(bounds.X + bounds.W / 2 - pageWidth / 2) < pageWidth * 0.025 && bounds.X > pageWidth * 0.15)
            region.Align = TextAlign.Center;
        return region;
    }
}

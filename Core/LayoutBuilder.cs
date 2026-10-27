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

    public static Rules Detect(RasterPage page)
    {
        var rules = new Rules();
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
            Math.Min(h.Right, x1) - Math.Max(h.X, x0) > minW * 0.5);
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
                if (gap > 1.6 * size || rules.VerticalBetween(prev.Box.Right, g.Box.X, cy) || sizeJump && gap > 0.5 * size)
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
        var line = new LineSeg { Glyphs = glyphs, Bounds = RectD.Union(glyphs.Select(g => g.Box)) };

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
        line.Text = sb.ToString().Trim();

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
        foreach (var line in lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            List<LineSeg>? best = null;
            double bestGap = double.MaxValue;
            foreach (var block in blocks)
                if (CanAppend(block, line, rules, origin != RegionSource.TextLayer, out double gap) && gap < bestGap) { best = block; bestGap = gap; }
            if (best != null) best.Add(line);
            else blocks.Add(new List<LineSeg> { line });
        }
        return blocks.Select(b => ToRegion(b, origin, pageWidth)).ToList();
    }

    /// <param name="ocr">OCR lines: only tightly stacked lines (a wrapped label) belong together; stacked
    /// labels in diagrams sit further apart.</param>
    static bool CanAppend(List<LineSeg> block, LineSeg line, Rules rules, bool ocr, out double gap)
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
        if (TextTools.StartsWithListMarker(line.Text)) return false;
        if (TextTools.HasLeader(last.Text) || TextTools.HasLeader(line.Text)) return false;   // contents lines
        if (rules.HorizontalBetween(last.Bounds, line.Bounds)) return false;

        // Left edges line up (the first line may be indented, or hang left of the rest).
        double left = block.Count > 1 ? block[1].Bounds.X : first.Bounds.X;
        if (block.Count == 1)
        {
            double indent = first.Bounds.X - line.Bounds.X;
            if (indent < -1.2 * size || indent > 4.5 * size) return false;
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

    static TextRegion ToRegion(List<LineSeg> lines, RegionSource origin, double pageWidth)
    {
        var bounds = RectD.Union(lines.Select(l => l.Bounds));
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
        if (lines.Count == 1 && Math.Abs(bounds.X + bounds.W / 2 - pageWidth / 2) < pageWidth * 0.025 && bounds.X > pageWidth * 0.15)
            region.Align = TextAlign.Center;
        return region;
    }
}

using System.Text.Json.Serialization;

namespace PdfTranslator.Core;

/// <summary>Axis-aligned rectangle in PDF points with a top-left origin (y grows downward).</summary>
public readonly record struct RectD(double X, double Y, double W, double H)
{
    [JsonIgnore] public double Right => X + W;
    [JsonIgnore] public double Bottom => Y + H;
    [JsonIgnore] public double Area => Math.Max(0, W) * Math.Max(0, H);
    [JsonIgnore] public double CenterY => Y + H / 2;

    public RectD Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public double IntersectionArea(RectD o)
    {
        double w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        double h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public double HorizontalOverlap(RectD o) => Math.Min(Right, o.Right) - Math.Max(X, o.X);
    public double VerticalOverlap(RectD o) => Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);

    public static RectD Union(IEnumerable<RectD> rects)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var r in rects)
        {
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.Right); y1 = Math.Max(y1, r.Bottom);
        }
        return x0 == double.MaxValue ? default : new RectD(x0, y0, x1 - x0, y1 - y0);
    }
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);
    [JsonIgnore] public double Luma => 0.299 * R + 0.587 * G + 0.114 * B;
    public int DistanceTo(Rgb o) => Math.Abs(R - o.R) + Math.Abs(G - o.G) + Math.Abs(B - o.B);
}

public enum RegionSource { TextLayer, ImageOcr, PageOcr, Added }

public enum TextAlign { Left, Center, Right }

/// <summary>A small graphic inside a line of text (a key or button icon), redrawn inline with the translation.</summary>
public sealed record InlineGraphic(RectD Bounds, byte[] Png);

/// <summary>One physical line of source text inside a block.</summary>
public sealed record SourceLine(RectD Bounds, string Text);

/// <summary>One translatable block of text on a page (a paragraph, heading, table cell, or OCR'd paragraph).</summary>
public sealed class TextRegion
{
    /// <summary>Stable id within its page (used by the editor and the MCP tools).</summary>
    public int Id { get; set; }
    /// <summary>Area of the original text; it is painted over with <see cref="Background"/>.</summary>
    public RectD Bounds { get; set; }
    /// <summary>Where the translation is drawn when the user moved/resized the block; null = automatic.</summary>
    public RectD? OutBounds { get; set; }
    public string Source { get; set; } = "";
    public string? Translated { get; set; }
    public List<SourceLine> Lines { get; set; } = new();
    /// <summary>Icons inside the text, referenced from Source/Translated as {{1}}, {{2}}… in order.</summary>
    public List<InlineGraphic> Inlines { get; set; } = new();
    public double FontSize { get; set; } = 10;
    /// <summary>Fixed output size set by the user; null = fit automatically.</summary>
    public double? FontSizeOverride { get; set; }
    public int LineCount { get; set; } = 1;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>The block starts with a bold label ("Cause:", "原因分析：") followed by regular text.</summary>
    public bool BoldLead { get; set; }
    public TextAlign Align { get; set; }
    /// <summary>
    /// Hanging indent (points from the block's left edge): where wrapped lines start, under the text after a
    /// label or list number ("Me:  “…", "1、…"). 0 when lines start at the left edge.
    /// </summary>
    public double Hang { get; set; }
    public string FontFamilyHint { get; set; } = "";
    public Rgb TextColor { get; set; } = Rgb.Black;
    public Rgb Background { get; set; } = Rgb.White;
    public RegionSource Origin { get; set; }
    /// <summary>The translation was typed or supplied by hand (editor or MCP); automatic translation leaves it alone.</summary>
    public bool ManualText { get; set; }
    /// <summary>Leave the original text visible; nothing is drawn for this block.</summary>
    public bool KeepOriginal { get; set; }
    /// <summary>How far right / down (points) the translation may run on over empty background.</summary>
    public double MaxRight { get; set; }
    public double MaxBottom { get; set; }
    /// <summary>How far up (points) the text may start when it needs more lines than the source (e.g. table-cell padding).</summary>
    public double MinTop { get; set; }

    [JsonIgnore] public RectD DrawBounds => OutBounds ?? Bounds;

    /// <summary>True when the region should be painted over in the output.</summary>
    [JsonIgnore] public bool HasOverlay => !KeepOriginal && !string.IsNullOrWhiteSpace(Translated) && Translated != Source;

    [JsonIgnore]
    public string OriginLabel => (Origin switch
    {
        RegionSource.TextLayer => "Text",
        RegionSource.ImageOcr => "Image OCR",
        RegionSource.Added => "Added",
        _ => "Page OCR",
    }) + (KeepOriginal ? " (kept)" : "");

    [JsonIgnore] public string StyleLabel =>
        $"{(FontSizeOverride ?? FontSize):0.#}{(Bold ? " B" : BoldLead ? " b:" : "")}{(Italic ? " I" : "")}";
}

/// <summary>
/// The page as it is displayed: crop-box relative, top-left origin, with the page's /Rotate applied, so a
/// landscape page stored as a rotated portrait one is read, laid out and drawn left to right like any other.
/// </summary>
/// <param name="Left">Crop box in PDF user space (bottom-left origin).</param>
public readonly record struct PageFrame(double Left, double Bottom, double Right, double Top, int Rotation)
{
    /// <summary>Clockwise quarter turns, 0..3.</summary>
    public int Quarter => ((Rotation / 90 % 4) + 4) % 4;
    double W => Right - Left;
    double H => Top - Bottom;
    public double Width => Quarter % 2 == 0 ? W : H;
    public double Height => Quarter % 2 == 0 ? H : W;

    /// <summary>A point in PDF user space, on the displayed page.</summary>
    public (double X, double Y) ToDisplay(double x, double y)
    {
        double u = x - Left, v = y - Bottom;
        return Quarter switch { 1 => (v, u), 2 => (W - u, v), 3 => (H - v, W - u), _ => (u, H - v) };
    }

    /// <summary>A rectangle in PDF user space (any two opposite corners), on the displayed page.</summary>
    public RectD ToDisplay(double x0, double y0, double x1, double y1)
    {
        var a = ToDisplay(x0, y0);
        var b = ToDisplay(x1, y1);
        return new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// Matrix (m11, m12, m21, m22, dx, dy) from displayed-page points to the unrotated crop box with a top-left
    /// origin, for drawing onto the page: x' = x·m11 + y·m21 + dx, y' = x·m12 + y·m22 + dy.
    /// </summary>
    public (double, double, double, double, double, double) DisplayToPage() => Quarter switch
    {
        1 => (0, -1, 1, 0, 0, H),
        2 => (-1, 0, 0, -1, W, H),
        3 => (0, 1, -1, 0, W, 0),
        _ => (1, 0, 0, 1, 0, 0),
    };
}

public sealed class PageResult
{
    public int Index { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>Offset of the crop box inside the media box (points, top-left origin); output drawing is in media-box space.</summary>
    public double CropOffsetX { get; init; }
    public double CropOffsetY { get; init; }
    public int Rotation { get; init; }
    /// <summary>How the page is displayed; regions are in its coordinates.</summary>
    [JsonIgnore] internal PageFrame Frame { get; init; }
    public List<TextRegion> Regions { get; } = new();
    /// <summary>Table borders / rules found on the page (used to keep cells apart).</summary>
    public List<RectD> Rules { get; } = new();
    public bool Translated { get; set; }
    public string? Note { get; set; }

    public int NextId() => Regions.Count == 0 ? 1 : Regions.Max(r => r.Id) + 1;
}

public enum OcrMode
{
    Off,
    /// <summary>OCR embedded images, plus whole pages that have no text layer (scans).</summary>
    Auto,
    /// <summary>OCR the whole rendered page; keeps lines not already covered by the text layer.</summary>
    FullPage,
}

/// <summary>A rasterised page: BGRA pixels plus the pixels-per-point scale used to render it.</summary>
public sealed class RasterPage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }
    public required double Scale { get; init; }

    public Rgb Pixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int i = (y * Width + x) * 4;
        return new Rgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public double Luma(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return 0.114 * Bgra[i] + 0.587 * Bgra[i + 1] + 0.299 * Bgra[i + 2];
    }
}

public static class Streams
{
    /// <summary>A MemoryStream whose buffer PDFsharp may access (new MemoryStream(byte[]) forbids GetBuffer).</summary>
    public static MemoryStream Exposable(byte[] data)
    {
        var ms = new MemoryStream(data.Length);
        ms.Write(data, 0, data.Length);
        ms.Position = 0;
        return ms;
    }
}

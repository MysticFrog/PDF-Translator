using PDFtoImage;
using SkiaSharp;

namespace PdfTranslator.Core;

/// <summary>Renders PDF pages to BGRA rasters through PDFium.</summary>
public static class PageRenderer
{
    public static RasterPage Render(byte[] pdf, int pageIndex, int dpi, double pageWidthPt)
    {
        using var bmp = Conversion.ToImage(pdf, (Index)pageIndex, null,
            new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
        return ToRaster(bmp, pageWidthPt);
    }

    public static int PageCount(byte[] pdf) => Conversion.GetPageCount(pdf);

    static RasterPage ToRaster(SKBitmap bmp, double pageWidthPt)
    {
        SKBitmap src = bmp;
        SKBitmap? converted = null;
        if (bmp.ColorType != SKColorType.Bgra8888)
        {
            converted = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bmp.CopyTo(converted, SKColorType.Bgra8888);
            src = converted;
        }
        try
        {
            return new RasterPage
            {
                Width = src.Width,
                Height = src.Height,
                Bgra = src.Bytes,
                Scale = src.Width / Math.Max(1, pageWidthPt),
            };
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Most common colour on the outline just outside <paramref name="r"/>: the region's background.</summary>
    public static Rgb SampleBackground(RasterPage page, RectD r)
    {
        // Quantised buckets find the dominant colour despite noise; the bucket's exact average is returned.
        var buckets = new Dictionary<int, (int n, long r, long g, long b)>();
        double s = page.Scale;
        int x0 = (int)Math.Floor((r.X - 1.5) * s), x1 = (int)Math.Ceiling((r.Right + 1.5) * s);
        int y0 = (int)Math.Floor((r.Y - 1.5) * s), y1 = (int)Math.Ceiling((r.Bottom + 1.5) * s);
        int step = Math.Max(1, (x1 - x0 + y1 - y0) / 400);

        void Add(int x, int y)
        {
            var c = page.Pixel(x, y);
            int key = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
            var t = buckets.GetValueOrDefault(key);
            buckets[key] = (t.n + 1, t.r + c.R, t.g + c.G, t.b + c.B);
        }
        for (int x = x0; x <= x1; x += step) { Add(x, y0); Add(x, y1); }
        for (int y = y0; y <= y1; y += step) { Add(x0, y); Add(x1, y); }
        // Inside the block too: text covers only a fraction of it, so the paper wins there even where the ring
        // around a short block runs along table borders.
        int ix0 = (int)Math.Ceiling(r.X * s), ix1 = (int)Math.Floor(r.Right * s);
        int iy0 = (int)Math.Ceiling(r.Y * s), iy1 = (int)Math.Floor(r.Bottom * s);
        int istep = Math.Max(1, (int)Math.Sqrt(Math.Max(1, (ix1 - ix0) * (iy1 - iy0)) / 600.0));
        for (int y = iy0; y <= iy1; y += istep)
            for (int x = ix0; x <= ix1; x += istep) Add(x, y);

        var best = buckets.Values.MaxBy(v => v.n);
        return new Rgb((byte)(best.r / best.n), (byte)(best.g / best.n), (byte)(best.b / best.n));
    }

    /// <summary>Right edge (points) up to which the band of <paramref name="r"/> stays plain background, capped at <paramref name="limit"/>.</summary>
    public static double FreeSpaceRight(RasterPage page, RectD r, Rgb background, double limit)
    {
        double s = page.Scale;
        int y0 = (int)((r.Y - 1) * s), y1 = (int)Math.Ceiling((r.Bottom + 1) * s);
        int xEnd = Math.Min(page.Width - 1, (int)(limit * s));
        int ystep = Math.Max(1, (y1 - y0) / 30);
        for (int x = (int)Math.Ceiling((r.Right + 1) * s); x <= xEnd; x++)
            for (int y = y0; y <= y1; y += ystep)
                if (page.Pixel(x, y).DistanceTo(background) > 45)
                    return Math.Max(r.Right, x / s - 2);
        return Math.Max(r.Right, limit);
    }

    /// <summary>Bottom edge (points) down to which the columns of <paramref name="r"/> stay plain background.</summary>
    public static double FreeSpaceDown(RasterPage page, RectD r, double startY, Rgb background, double limit)
    {
        double s = page.Scale;
        int x0 = (int)((r.X - 1) * s), x1 = (int)Math.Ceiling((r.Right + 1) * s);
        int yEnd = Math.Min(page.Height - 1, (int)(limit * s));
        int xstep = Math.Max(1, (x1 - x0) / 60);
        for (int y = (int)Math.Ceiling(startY * s); y <= yEnd; y++)
            for (int x = x0; x <= x1; x += xstep)
                if (page.Pixel(x, y).DistanceTo(background) > 45)
                    return Math.Max(r.Bottom, y / s - 2);
        return Math.Max(r.Bottom, limit);
    }

    /// <summary>Top edge (points) up to which the columns of <paramref name="r"/> stay plain background.</summary>
    public static double FreeSpaceUp(RasterPage page, RectD r, Rgb background, double limit)
    {
        double s = page.Scale;
        int x0 = (int)((r.X - 1) * s), x1 = (int)Math.Ceiling((r.Right + 1) * s);
        int yEnd = Math.Max(0, (int)(limit * s));
        int xstep = Math.Max(1, (x1 - x0) / 60);
        for (int y = (int)Math.Floor((r.Y - 1) * s); y >= yEnd; y--)
            for (int x = x0; x <= x1; x += xstep)
                if (page.Pixel(x, y).DistanceTo(background) > 45)
                    return Math.Min(r.Y, y / s + 2);
        return Math.Min(r.Y, limit);
    }

    /// <summary>Number of text rows inside <paramref name="r"/> and the font size their ink height suggests.</summary>
    public static (int lines, double size) EstimateTextLines(RasterPage page, RectD r, Rgb background)
    {
        double s = page.Scale;
        int x0 = Math.Max(0, (int)(r.X * s)), x1 = Math.Min(page.Width, (int)(r.Right * s));
        int y0 = Math.Max(0, (int)(r.Y * s)), y1 = Math.Min(page.Height, (int)(r.Bottom * s));
        var runs = new List<int>();
        int run = 0;
        for (int y = y0; y < y1; y++)
        {
            bool ink = false;
            for (int x = x0; x < x1 && !ink; x++) ink = page.Pixel(x, y).DistanceTo(background) > 150;
            if (ink) run++;
            else if (run > 0) { runs.Add(run); run = 0; }
        }
        if (run > 0) runs.Add(run);
        runs = runs.Where(h => h / s >= 3).ToList(); // ignore specks and rules
        if (runs.Count == 0) return (1, 0);
        double h = runs.OrderBy(v => v).ElementAt(runs.Count / 2) / s;
        return (runs.Count, Math.Clamp(h / 0.85, 4, 48));
    }

    /// <summary>Fraction of pixels in <paramref name="r"/> that are ink (used to tell bold OCR words from regular ones).</summary>
    public static double InkDensity(RasterPage page, RectD r)
    {
        double s = page.Scale;
        int x0 = (int)(r.X * s), x1 = (int)(r.Right * s), y0 = (int)(r.Y * s), y1 = (int)(r.Bottom * s);
        var bg = SampleBackground(page, r);
        long ink = 0, n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(page.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(page.Width, x1); x++)
            {
                n++;
                if (page.Pixel(x, y).DistanceTo(bg) > 200) ink++;
            }
        return n == 0 ? 0 : (double)ink / n;
    }

    /// <summary>PNG of one area of a rendered page (points).</summary>
    public static byte[] CropPng(RasterPage page, RectD area)
    {
        double s = page.Scale;
        int x0 = Math.Clamp((int)(area.X * s), 0, page.Width - 1), y0 = Math.Clamp((int)(area.Y * s), 0, page.Height - 1);
        int w = Math.Clamp((int)Math.Ceiling(area.W * s), 1, page.Width - x0), h = Math.Clamp((int)Math.Ceiling(area.H * s), 1, page.Height - y0);
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Array.Copy(page.Bgra, ((y0 + y) * page.Width + x0) * 4, bgra, y * w * 4, w * 4);
        return ToPng(new RasterPage { Width = w, Height = h, Bgra = bgra, Scale = s });
    }

    public static byte[] ToPng(RasterPage r)
    {
        var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(r.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        finally { handle.Free(); }
    }

    /// <summary>Average of the pixels inside <paramref name="r"/> that clearly differ from the background (the ink).</summary>
    public static Rgb SampleInk(RasterPage page, RectD r, Rgb background)
    {
        double s = page.Scale;
        int x0 = (int)(r.X * s), x1 = (int)(r.Right * s), y0 = (int)(r.Y * s), y1 = (int)(r.Bottom * s);
        int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 20000));
        long sr = 0, sg = 0, sb = 0, n = 0;
        int bestDist = 0;
        var samples = new List<(Rgb c, int d)>();
        for (int y = y0; y < y1; y += step)
            for (int x = x0; x < x1; x += step)
            {
                var c = page.Pixel(x, y);
                int d = c.DistanceTo(background);
                if (d > 120) { samples.Add((c, d)); bestDist = Math.Max(bestDist, d); }
            }
        // Keep only the most contrasting pixels; anti-aliased edges would wash the colour out.
        foreach (var (c, d) in samples.Where(t => t.d >= bestDist * 0.8))
        {
            sr += c.R; sg += c.G; sb += c.B; n++;
        }
        if (n == 0) return background.Luma > 128 ? Rgb.Black : Rgb.White;
        return new Rgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }
}

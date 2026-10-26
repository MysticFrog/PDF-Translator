using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PdfTranslator.Core;

/// <summary>OCR through the Windows.Media.Ocr engine built into Windows 10/11 (no extra downloads).</summary>
public sealed class OcrService : IOcrEngine
{
    public string Name => "Windows OCR " + LanguageTag;
    public int PreferredDpi => 200;

    readonly OcrEngine _engine;

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    OcrService(OcrEngine engine) => _engine = engine;

    public static IReadOnlyList<string> InstalledLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>Engine for the source language if its OCR pack is installed, else the user's profile languages.</summary>
    public static OcrService? Create(string sourceLanguage)
    {
        OcrEngine? engine = null;
        if (sourceLanguage != Languages.Auto)
        {
            var tag = sourceLanguage switch { "zh-CN" => "zh-Hans-CN", "zh-TW" => "zh-Hant-TW", _ => sourceLanguage };
            var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l =>
                l.LanguageTag.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.StartsWith(tag.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.Equals(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (lang != null) engine = OcrEngine.TryCreateFromLanguage(lang);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        engine ??= OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null);
        return engine == null ? null : new OcrService(engine);
    }

    /// <summary>Recognises text inside <paramref name="area"/> (page points) of a rendered page.</summary>
    public async Task<List<List<Glyph>>> RecognizeAsync(RasterPage page, RectD area, bool sparse)
    {
        double s = page.Scale;
        int x0 = Math.Clamp((int)(area.X * s), 0, page.Width - 1);
        int y0 = Math.Clamp((int)(area.Y * s), 0, page.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(area.Right * s), x0 + 1, page.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(area.Bottom * s), y0 + 1, page.Height);
        int w = x1 - x0, h = y1 - y0;
        if (w < 24 || h < 12) return new();

        // The engine rejects images larger than MaxImageDimension; downscale by integer stride if needed.
        int stride = 1;
        while (w / stride > OcrEngine.MaxImageDimension || h / stride > OcrEngine.MaxImageDimension) stride++;
        int cw = w / stride, ch = h / stride;
        var buf = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int si = ((y0 + y * stride) * page.Width + x0 + x * stride) * 4;
                int di = (y * cw + x) * 4;
                buf[di] = page.Bgra[si]; buf[di + 1] = page.Bgra[si + 1]; buf[di + 2] = page.Bgra[si + 2]; buf[di + 3] = 255;
            }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buf.AsBuffer(), BitmapPixelFormat.Bgra8, cw, ch, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap);

        var lines = new List<List<Glyph>>();
        double k = stride / s; // crop pixels -> points
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var rects = line.Words.Select(wd => new RectD(
                x0 / s + wd.BoundingRect.X * k, y0 / s + wd.BoundingRect.Y * k,
                wd.BoundingRect.Width * k, wd.BoundingRect.Height * k)).ToList();
            double wordH = rects.Select(r => r.H).OrderBy(v => v).ElementAt(rects.Count / 2);
            double baseline = rects.Max(r => r.Bottom);
            lines.Add(line.Words.Select((wd, i) => new Glyph
            {
                Box = rects[i],
                Text = wd.Text,
                Size = Math.Max(4, wordH * 1.05),
                Baseline = baseline,
            }).ToList());
        }
        return lines;
    }

    static bool IsUnspacedScript(string tag) => tag.StartsWith("zh") || tag.StartsWith("ja");
}

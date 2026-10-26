using SkiaSharp;
using Tesseract;

namespace PdfTranslator.Core;

/// <summary>An OCR engine that returns recognised lines as word glyphs in page points.</summary>
public interface IOcrEngine
{
    string Name { get; }
    /// <summary>Render resolution this engine reads best at.</summary>
    int PreferredDpi { get; }
    /// <param name="sparse">Scattered labels (diagrams) rather than running text.</param>
    Task<List<List<Glyph>>> RecognizeAsync(RasterPage page, RectD area, bool sparse);
}

/// <summary>Chooses an OCR engine that can read the document's script.</summary>
public static class OcrFactory
{
    /// <summary>
    /// Windows OCR when one of its installed languages covers <paramref name="script"/>; otherwise PaddleOCR for
    /// Chinese/Japanese, or Tesseract for other scripts, downloading model data on first use. Returns null (with a reason) when neither is possible.
    /// </summary>
    public static async Task<(IOcrEngine? engine, string? problem)> CreateAsync(string source, Script script,
        IProgress<string>? log, CancellationToken ct)
    {
        var windows = OcrService.Create(source);
        if (windows != null)
        {
            var have = TextTools.ScriptOfOcrTag(windows.LanguageTag);
            if (script is Script.None or Script.Latin || have == script || script == Script.Kana && have == Script.Han)
                return (windows, null);
        }

        // PaddleOCR reads Chinese and Japanese (incl. Latin mixed in) far better than Tesseract.
        if (script is Script.Han or Script.Kana || source is "zh-CN" or "zh-TW" or "ja")
        {
            try { return (await PaddleOcr.CreateAsync(log, ct), null); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.Report("PaddleOCR unavailable, using Tesseract: " + ex.Message); }
        }

        var lang = TessData.LanguageFor(source, script);
        if (lang == null)
            return (null, $"no OCR language available for {script} script");
        try
        {
            var dir = await TessData.EnsureAsync(lang, log, ct);
            return (new TesseractOcr(dir, lang), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"could not get Tesseract data for '{lang}': {ex.Message}");
        }
    }
}

/// <summary>Tesseract language data (LSTM "best" models) kept in %LOCALAPPDATA%\PdfTranslator\tessdata.</summary>
public static class TessData
{
    const string BaseUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfTranslator", "tessdata");

    /// <summary>Tesseract language string for the source language or script; English is always added for mixed labels.</summary>
    public static string? LanguageFor(string source, Script script)
    {
        string? main = source switch
        {
            "zh-CN" => "chi_sim",
            "zh-TW" => "chi_tra",
            "ja" => "jpn",
            "ko" => "kor",
            "ru" => "rus",
            "uk" => "ukr",
            "bg" => "bul",
            "el" => "ell",
            "ar" => "ara",
            "he" => "heb",
            "th" => "tha",
            "hi" => "hin",
            _ => null,
        };
        main ??= script switch
        {
            Script.Han => "chi_sim",
            Script.Kana => "jpn",
            Script.Hangul => "kor",
            Script.Cyrillic => "rus",
            Script.Greek => "ell",
            Script.Arabic => "ara",
            Script.Hebrew => "heb",
            Script.Thai => "tha",
            Script.Devanagari => "hin",
            Script.Latin or Script.None => "eng",
            _ => null,
        };
        return main == null ? null : main == "eng" ? "eng" : main + "+eng";
    }

    static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Makes sure every component of <paramref name="langs"/> ("chi_sim+eng") is on disk; returns the folder.</summary>
    public static async Task<string> EnsureAsync(string langs, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        await Gate.WaitAsync(ct);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PdfTranslator/1.0");
            foreach (var lang in langs.Split('+'))
            {
                string file = Path.Combine(Folder, lang + ".traineddata");
                if (File.Exists(file) && new FileInfo(file).Length > 100_000) continue;
                log?.Report($"Downloading Tesseract OCR data for '{lang}' (one-off)…");
                string part = file + ".part";
                using (var resp = await http.GetAsync(BaseUrl + lang + ".traineddata", HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(part);
                    await resp.Content.CopyToAsync(fs, ct);
                }
                File.Move(part, file, true);
            }
            return Folder;
        }
        finally { Gate.Release(); }
    }
}

/// <summary>Tesseract 5 (LSTM) OCR; used for scripts Windows OCR has no installed language for.</summary>
public sealed class TesseractOcr : IOcrEngine
{
    readonly string _dataDir;
    readonly string _lang;

    public TesseractOcr(string dataDir, string lang)
    {
        _dataDir = dataDir;
        _lang = lang;
    }

    public string Name => "Tesseract " + _lang;
    public int PreferredDpi => 300;

    public Task<List<List<Glyph>>> RecognizeAsync(RasterPage page, RectD area, bool sparse) =>
        Task.Run(() => Recognize(page, area, sparse));

    List<List<Glyph>> Recognize(RasterPage page, RectD area, bool sparse)
    {
        double s = page.Scale;
        int x0 = Math.Clamp((int)(area.X * s), 0, page.Width - 1);
        int y0 = Math.Clamp((int)(area.Y * s), 0, page.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(area.Right * s), x0 + 1, page.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(area.Bottom * s), y0 + 1, page.Height);
        if (x1 - x0 < 24 || y1 - y0 < 12) return new List<List<Glyph>>();

        using var engine = new TesseractEngine(_dataDir, _lang, EngineMode.LstmOnly);
        using var pix = Pix.LoadFromMemory(CropPng(page, x0, y0, x1 - x0, y1 - y0, enhance: false));
        using var enhanced = Pix.LoadFromMemory(CropPng(page, x0, y0, x1 - x0, y1 - y0, enhance: true));

        // Two segmentations find different words (sparse = scattered labels, auto = text blocks); keep the
        // most confident reading wherever they overlap.
        var words = new List<(RectD box, string text, float conf)>();
        // A small box drawn around one label: read it as a single line / block, not as a page layout.
        bool label = !sparse && area.H < 40;
        var passes = sparse
            ? new[] { (enhanced, PageSegMode.SparseText), (enhanced, PageSegMode.Auto), (pix, PageSegMode.SparseText) }
            : label
                ? new[] { (enhanced, PageSegMode.SingleLine), (enhanced, PageSegMode.SingleBlock), (pix, PageSegMode.SingleLine) }
                : new[] { (pix, PageSegMode.Auto), (enhanced, PageSegMode.Auto) };
        foreach (var (image, mode) in passes)
        {
            using var result = engine.Process(image, mode);
            using var it = result.GetIterator();
            it.Begin();
            do
            {
                if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out var r)) continue;
                string text = (it.GetText(PageIteratorLevel.Word) ?? "").Trim();
                float conf = it.GetConfidence(PageIteratorLevel.Word);
                if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) continue;
                // Chinese/Japanese models report lower confidences; Latin junk from drawings needs a stricter bar.
                bool cjk = text.Any(TextTools.IsCjk);
                if (conf < (cjk ? 35 : 60)) continue;
                words.Add((new RectD((x0 + r.X1) / s, (y0 + r.Y1) / s, r.Width / s, r.Height / s), text, conf));
            }
            while (it.Next(PageIteratorLevel.Word));
        }
        var kept = new List<(RectD box, string text, float conf)>();
        foreach (var w in words.OrderByDescending(w => w.conf))
            if (!kept.Any(k => k.box.IntersectionArea(w.box) > Math.Min(k.box.Area, w.box.Area) * 0.4)) kept.Add(w);

        // Rebuild lines: words whose vertical centres line up.
        var lines = new List<List<(RectD box, string text, float conf)>>();
        foreach (var w in kept.OrderBy(w => w.box.CenterY))
        {
            var line = lines.FirstOrDefault(l =>
            {
                double h = l.Average(x => x.box.H);
                return Math.Abs(l.Average(x => x.box.CenterY) - w.box.CenterY) < Math.Min(h, w.box.H) * 0.5;
            });
            if (line != null) line.Add(w); else lines.Add(new() { w });
        }

        // Word boxes are tight around the ink; CJK glyphs fill ~90% of the em, Latin words less.
        var result2 = new List<List<Glyph>>();
        foreach (var line in lines)
        {
            var hs = line.Select(g => g.box.H).OrderBy(v => v).ToList();
            double h = hs[hs.Count / 2];
            bool cjk = line.Any(g => g.text.Any(TextTools.IsCjk));
            double size = Math.Max(4, h / (cjk ? 0.9 : 0.8));
            double baseline = line.Max(g => g.box.Bottom);
            result2.Add(line.OrderBy(g => g.box.X).Select(g => new Glyph
            {
                Box = new RectD(g.box.X, baseline - size * 0.85, g.box.W, size),
                Text = g.text,
                Size = size,
                Baseline = baseline,
            }).ToList());
        }
        return result2;
    }

    /// <summary>Diagnostic: every word each segmentation mode reads in the area, with confidence.</summary>
    public IEnumerable<string> Debug(RasterPage page, RectD area)
    {
        double s = page.Scale;
        int x0 = (int)(area.X * s), y0 = (int)(area.Y * s), w = (int)(area.W * s), h = (int)(area.H * s);
        using var engine = new TesseractEngine(_dataDir, _lang, EngineMode.LstmOnly);
        foreach (var enhance in new[] { false, true })
        {
            using var pix = Pix.LoadFromMemory(CropPng(page, x0, y0, w, h, enhance));
            foreach (var mode in new[] { PageSegMode.SingleLine, PageSegMode.SingleBlock, PageSegMode.SparseText, PageSegMode.Auto })
            {
                using var result = engine.Process(pix, mode);
                var words = new List<string>();
                using (var it = result.GetIterator())
                {
                    it.Begin();
                    do
                    {
                        var t = it.GetText(PageIteratorLevel.Word);
                        if (!string.IsNullOrWhiteSpace(t)) words.Add($"{t.Trim()}({it.GetConfidence(PageIteratorLevel.Word):0})");
                    }
                    while (it.Next(PageIteratorLevel.Word));
                }
                yield return $"{(enhance ? "enh" : "raw")} {mode}: {string.Join(" ", words)}";
            }
        }
    }

    /// <param name="enhance">Grey-scale with a steep contrast curve: grey, anti-aliased labels in screenshots
    /// (luma ~110-140) become near-black while the paper stays white.</param>
    static byte[] CropPng(RasterPage page, int x0, int y0, int w, int h, bool enhance)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var row = new byte[w * 4];
        var dst = bmp.GetPixels();
        for (int y = 0; y < h; y++)
        {
            Array.Copy(page.Bgra, ((y0 + y) * page.Width + x0) * 4, row, 0, w * 4);
            if (enhance)
                for (int i = 0; i < row.Length; i += 4)
                {
                    double l = 0.114 * row[i] + 0.587 * row[i + 1] + 0.299 * row[i + 2];
                    byte v = (byte)Math.Clamp((l - 90) * 255 / 115, 0, 255);
                    row[i] = row[i + 1] = row[i + 2] = v;
                }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, dst + y * bmp.RowBytes, w * 4);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

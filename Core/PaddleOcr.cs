using System.Security.Cryptography;
using RapidOcrNet;
using SkiaSharp;

namespace PdfTranslator.Core;

/// <summary>
/// PaddleOCR PP-OCRv5 through RapidOcrNet (ONNX Runtime). Much stronger than Tesseract on Chinese/Japanese,
/// including small, blurry labels in screenshots. The detector and angle classifier ship with the NuGet package;
/// the Chinese recogniser and its dictionary are downloaded once (hash-checked) from RapidOCR's model host.
/// </summary>
public sealed class PaddleOcr : IOcrEngine
{
    const string Base = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/";
    const string RecFile = "ch_PP-OCRv5_rec_server.onnx";
    const string RecUrl = Base + "onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_server.onnx";
    const string RecSha256 = "e09385400eaaaef34ceff54aeb7c4f0f1fe014c27fa8b9905d4709b65746562a";
    const string DictFile = "ppocrv5_dict.txt";
    const string DictUrl = Base + "paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_server/ppocrv5_dict.txt";

    static readonly SemaphoreSlim InitGate = new(1);
    static RapidOcr? _ocr;

    public string Name => "PaddleOCR v5";
    public int PreferredDpi => 300;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfTranslator", "models");

    static string Bundled(string file) => Path.Combine(AppContext.BaseDirectory, "models", "v5", file);

    /// <summary>Loads the models once, downloading the Chinese recogniser on first use.</summary>
    public static async Task<PaddleOcr> CreateAsync(IProgress<string>? log, CancellationToken ct)
    {
        await InitGate.WaitAsync(ct);
        try
        {
            if (_ocr == null)
            {
                Directory.CreateDirectory(Folder);
                string rec = Path.Combine(Folder, RecFile), dict = Path.Combine(Folder, DictFile);
                await DownloadAsync(RecUrl, rec, RecSha256, log, ct);
                await DownloadAsync(DictUrl, dict, null, log, ct);
                var ocr = new RapidOcr();
                ocr.InitModels(
                    detPath: Bundled("ch_PP-OCRv5_mobile_det.onnx"),
                    clsPath: Bundled("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    recPath: rec,
                    keysPath: dict);
                _ocr = ocr;
            }
            return new PaddleOcr();
        }
        finally { InitGate.Release(); }
    }

    static async Task DownloadAsync(string url, string file, string? sha256, IProgress<string>? log, CancellationToken ct)
    {
        if (File.Exists(file) && new FileInfo(file).Length > 1000) return;
        log?.Report($"Downloading OCR model {Path.GetFileName(file)} (one-off)…");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PdfTranslator/1.0");
        string part = file + ".part";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(part);
            await resp.Content.CopyToAsync(fs, ct);
        }
        if (sha256 != null)
        {
            await using var fs = File.OpenRead(part);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (hash != sha256)
            {
                File.Delete(part);
                throw new InvalidDataException($"Checksum mismatch for {Path.GetFileName(file)}.");
            }
        }
        File.Move(part, file, true);
    }

    public Task<List<List<Glyph>>> RecognizeAsync(RasterPage page, RectD area, bool sparse) =>
        Task.Run(() => Recognize(page, area));

    List<List<Glyph>> Recognize(RasterPage page, RectD area)
    {
        double s = page.Scale;
        int x0 = Math.Clamp((int)(area.X * s), 0, page.Width - 1);
        int y0 = Math.Clamp((int)(area.Y * s), 0, page.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(area.Right * s), x0 + 1, page.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(area.Bottom * s), y0 + 1, page.Height);
        int w = x1 - x0, h = y1 - y0;
        var lines = new List<List<Glyph>>();
        if (w < 16 || h < 8) return lines;

        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dst = bmp.GetPixels();
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(page.Bgra, ((y0 + y) * page.Width + x0) * 4, dst + y * bmp.RowBytes, w * 4);

        // Keep full resolution for the detector (the default caps the long side at 1024 px, losing small labels).
        var options = RapidOcrOptions.Default with { ImgResize = 0, LimitSideLen = 960, MaxSideLen = 6000, DoAngle = false };
        OcrResult result;
        lock (InitGate) result = _ocr!.Detect(bmp, options);

        foreach (var block in result.TextBlocks)
        {
            // Leader lines next to diagram labels come back as dashes.
            string text = (block.Text ?? "").Trim().Trim('—', '–', '-', '_', '|', '一', ' ');
            if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) continue;
            var scores = block.CharScores;
            if (scores != null && scores.Length > 0 && scores.Average() < 0.6) continue;

            var pts = block.BoxPoints;
            double bx0 = pts.Min(p => (double)p.X), bx1 = pts.Max(p => (double)p.X);
            double by0 = pts.Min(p => (double)p.Y), by1 = pts.Max(p => (double)p.Y);
            // Detector boxes are dilated around the ink; the font size is about 62% of the box height.
            double hPt = (by1 - by0) / s;
            double size = Math.Max(4, hPt * 0.62);
            double cy = y0 / s + (by0 + by1) / 2 / s;
            // The block box spans the ink (~84% of the detector box), so covering it hides the original glyphs.
            var box = new RectD(x0 / s + bx0 / s, cy - hPt * 0.42, (bx1 - bx0) / s, hPt * 0.84);
            lines.Add(new List<Glyph> { new() { Box = box, Text = text, Size = size, Baseline = box.Bottom } });
        }
        return lines;
    }
}

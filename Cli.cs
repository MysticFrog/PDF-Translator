using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfTranslator.Core;
using PdfTranslator.Translators;
using SkiaSharp;

namespace PdfTranslator;

/// <summary>Headless commands for scripting and testing.</summary>
public static class Cli
{
    public const string Usage = """
        PdfTranslator.exe [file.pdf]                       open the GUI (optionally with a file)
        PdfTranslator.exe --translate in.pdf out.pdf --to de [--from auto] [--provider mymemory] [--ocr auto|fullpage|off] [--pages 1,3-5]
        PdfTranslator.exe --render in.pdf outDir [--dpi 110]   write every page as PNG
        PdfTranslator.exe --snapshot in.pdf out.png [--to de] [--provider mymemory] [--page 1]
                                                           translate one page in the GUI and save a window image
        PdfTranslator.exe --make-sample out.pdf             write a test PDF (text, colours, image text, scanned page)
        PdfTranslator.exe --providers                       list translation services
        PdfTranslator.exe --version                         version and the commit it was built from
        PdfTranslator.exe --mcp-stdio [--port 8765]         MCP over stdio for Claude Desktop (starts the app if needed)
        """;

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    public static async Task<int> RunAsync(string[] args)
    {
        AttachConsole(-1);
        try
        {
            string Opt(string name, string def)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
            }

            switch (args[0])
            {
                case "--translate" when args.Length >= 3:
                    return await TranslateAsync(args[1], args[2],
                        new TranslateOptions(Opt("--from", Languages.Auto), Opt("--to", "en"), ParseOcr(Opt("--ocr", "auto"))),
                        Opt("--provider", "mymemory"), ParsePages(Opt("--pages", "")));
                case "--render" when args.Length >= 3:
                    RenderAll(args[1], args[2], int.Parse(Opt("--dpi", "110")));
                    return 0;
                case "--snapshot" when args.Length >= 3:
                    return await SnapshotAsync(args[1], args[2], Opt("--to", "de"), Opt("--provider", "mymemory"), int.Parse(Opt("--page", "1")) - 1,
                        args.Contains("--images"));
                case "--make-sample" when args.Length >= 2:
                    MakeSample(args[1]);
                    Console.WriteLine($"Wrote {args[1]}");
                    return 0;
                case "--mcp-stdio":
                    return await Mcp.McpStdioBridge.RunAsync(int.Parse(Opt("--port", AppSettings.Load().McpPort.ToString())));
                case "--dump" when args.Length >= 3:
                {
                    using var an = new PdfAnalyzer(File.ReadAllBytes(args[1]));
                    foreach (var line in an.DumpLetters(int.Parse(args[2]) - 1)) Console.WriteLine(line);
                    return 0;
                }
                case "--ocr-box" when args.Length >= 7:
                {
                    // --ocr-box in.pdf page x y w h [lang]
                    var bytes = File.ReadAllBytes(args[1]);
                    int pg = int.Parse(args[2]) - 1;
                    using var an = new PdfAnalyzer(bytes);
                    var raster = PageRenderer.Render(bytes, pg, 300, an.Geometry(pg).Width);
                    string lang = args.Length > 7 ? args[7] : "chi_sim+eng";
                    if (lang == "paddle")
                    {
                        var paddle = await PaddleOcr.CreateAsync(new SyncProgress(Console.WriteLine), CancellationToken.None);
                        var box = new RectD(double.Parse(args[3]), double.Parse(args[4]), double.Parse(args[5]), double.Parse(args[6]));
                        foreach (var l in await paddle.RecognizeAsync(raster, box, false))
                            foreach (var g in l) Console.WriteLine($"{g.Text}  x={g.Box.X:0.0} y={g.Box.Y:0.0} w={g.Box.W:0.0} size={g.Size:0.0}");
                        return 0;
                    }
                    var tess = new TesseractOcr(await TessData.EnsureAsync(lang, null, CancellationToken.None), lang);
                    var area = new RectD(double.Parse(args[3]), double.Parse(args[4]), double.Parse(args[5]), double.Parse(args[6]));
                    foreach (var line in tess.Debug(raster, area)) Console.WriteLine(line);
                    return 0;
                }
                case "--structure" when args.Length >= 3:
                {
                    using var doc = PdfSharp.Pdf.IO.PdfReader.Open(args[1], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                    foreach (var line in ImageRemover.Describe(doc.Pages[int.Parse(args[2]) - 1])) Console.WriteLine(line);
                    return 0;
                }
                case "--queue" when args.Length >= 3:
                {
                    // --queue out.png in1.pdf in2.pdf … [--provider id] [--to xx]: runs the window's queue, then a screenshot.
                    var w = new MainWindow { Left = -20000, Top = 0, ShowActivated = false };
                    w.Show();
                    var files = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).ToList();
                    await w.RunQueueForTestAsync(files, Opt("--provider", "mymemory"), Opt("--to", "en"));
                    w.Hide();
                    SaveWindowImage(w, args[1]);
                    foreach (var line in w.QueueSummary()) Console.WriteLine(line);
                    w.Close();
                    return 0;
                }
                case "--snapshot-dialog" when args.Length >= 3:
                {
                    // --snapshot-dialog signin|mcp|settings out.png
                    var w = new MainWindow { Left = -20000, Top = 0, ShowActivated = false };
                    w.Show();
                    await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ActionDialog.SnapshotTarget = args[2];
                    switch (args[1])
                    {
                        case "signin":
                            w.ShowClaudeCodeSetup(new Translators.ClaudeCodeSetupException("Claude Code is not signed in for PDF Translator.",
                                Translators.ClaudeCodeTranslator.Locate(null) ?? "claude.exe", false));
                            break;
                        case "mcp":
                            w.McpText_Click(w, null);
                            break;
                        case "key":
                            w.ShowMissingKey(KeyHelp.For("deepl"));
                            break;
                        case "offline":
                            w.ShowOfflineInstall();
                            break;
                        case "ollama":
                            w.ShowOllamaInstall(null);
                            break;
                        case "about":
                            w.ShowAbout();
                            break;
                        default:
                            SaveWindowImage(new SettingsWindow(AppSettings.Load()) { MaxHeight = 4000 }, args[2]);   // every section, unscrolled
                            break;
                    }
                    w.Close();
                    Console.WriteLine($"Wrote {args[2]}");
                    return 0;
                }
                case "--install-offline":
                {
                    // Installs offline LibreTranslate in this console (same script as Settings › Offline translation › Install).
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -File \"{LocalLibre.WriteInstallScript()}\"") { UseShellExecute = false };
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    await proc.WaitForExitAsync();
                    Console.WriteLine(LocalLibre.IsInstalled ? "Offline LibreTranslate installed." : "Install did not complete.");
                    return LocalLibre.IsInstalled ? 0 : 1;
                }
                case "--install-ollama":
                {
                    // Installs offline AI (Ollama + a model) in this console: same script as Settings › Offline AI › Install.
                    string model = args.Length >= 2 ? args[1] : LocalOllama.SuggestedModel;
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -File \"{LocalOllama.WriteScript(model)}\"") { UseShellExecute = false };
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    await proc.WaitForExitAsync();
                    Console.WriteLine(proc.ExitCode == 0 ? $"Offline AI installed with {model}." : "Install did not complete.");
                    return proc.ExitCode;
                }
                case "--version":
                    Console.WriteLine(AppInfo.Title);
                    return 0;
                case "--providers":
                    foreach (var p in TranslatorFactory.Providers) Console.WriteLine($"{p.Id,-14} {p.Name} — {p.Requirement}");
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex);
            return 1;
        }
    }

    static OcrMode ParseOcr(string s) => s.ToLowerInvariant() switch
    {
        "off" => OcrMode.Off,
        "fullpage" or "full" => OcrMode.FullPage,
        _ => OcrMode.Auto,
    };

    /// <summary>"1,3-5" → {0, 2, 3, 4}; empty means every page.</summary>
    static HashSet<int>? ParsePages(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var pages = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', 2);
            int a = int.Parse(ends[0]), b = ends.Length == 2 ? int.Parse(ends[1]) : a;
            for (int p = a; p <= b; p++) pages.Add(p - 1);
        }
        return pages;
    }

    static async Task<int> TranslateAsync(string input, string output, TranslateOptions opt, string provider, HashSet<int>? pages = null)
    {
        var translator = TranslatorFactory.Create(provider, AppSettings.Load());
        if (translator is LocalLibreTranslator)
            await LocalLibre.EnsureRunningAsync(opt.Source, opt.Target, new SyncProgress(Console.WriteLine), CancellationToken.None);
        using var session = new TranslationSession(input);
        var log = new SyncProgress(Console.WriteLine);
        for (int i = 0; i < session.PageCount; i++)
        {
            if (pages != null && !pages.Contains(i)) continue;
            var t0 = DateTime.UtcNow;
            var page = await session.TranslatePageAsync(i, opt, translator, log, CancellationToken.None);
            Console.WriteLine($"Page {i + 1}: {page.Regions.Count} blocks " +
                $"({page.Regions.Count(r => r.Origin != RegionSource.TextLayer)} from OCR){(page.Note != null ? " — " + page.Note : "")} in {(DateTime.UtcNow - t0).TotalSeconds:0}s");
            foreach (var r in page.Regions)
                Console.WriteLine($"   #{r.Id} [{r.OriginLabel} {r.StyleLabel}{(r.Align != TextAlign.Left ? " " + r.Align : "")}] {Short(r.Source)}  =>  {Short(r.Translated ?? "")}" +
                    (Environment.GetEnvironmentVariable("PDFT_BOUNDS") == "1" ? $"   @{r.Bounds.X:0},{r.Bounds.Y:0} {r.Bounds.W:0}×{r.Bounds.H:0} bg={r.Background.R},{r.Background.G},{r.Background.B}" : ""));
        }
        session.Save(output);
        LocalLibre.Stop();
        LocalOllama.Stop();
        Console.WriteLine($"Wrote {output}");
        return 0;
    }

    static string Short(string s) => s.Length > 70 ? s[..70] + "…" : s;

    static void RenderAll(string input, string outDir, int dpi)
    {
        Directory.CreateDirectory(outDir);
        var bytes = File.ReadAllBytes(input);
        using var analyzer = new PdfAnalyzer(bytes);
        for (int i = 0; i < analyzer.PageCount; i++)
        {
            var width = analyzer.Geometry(i).Width;
            var r = PageRenderer.Render(bytes, i, dpi, width);
            var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            var handle = GCHandle.Alloc(r.Bgra, GCHandleType.Pinned);
            try
            {
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                using var fs = File.Create(Path.Combine(outDir, $"page{i + 1}.png"));
                bmp.Encode(fs, SKEncodedImageFormat.Png, 90);
            }
            finally { handle.Free(); }
            Console.WriteLine($"page{i + 1}.png");
        }
    }

    static async Task<int> SnapshotAsync(string input, string png, string to, string provider, int page, bool imageMode = false)
    {
        var w = new MainWindow { Left = -20000, Top = 0, Width = 1500, Height = 950, ShowActivated = false };
        w.Show();
        w.OpenFile(input);
        await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (imageMode) await w.ImageModeForSnapshotAsync(page);
        else await w.TranslateForSnapshotAsync(page, to, provider);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(150);
            await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render((Visual)w.Content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(png)) enc.Save(fs);
        w.Close();
        Console.WriteLine($"Wrote {png}");
        return 0;
    }

    /// <summary>Shows a window off-screen, renders it to a PNG and closes it.</summary>
    internal static void SaveWindowImage(Window window, string png)
    {
        window.Left = -20000;
        window.Top = 0;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(bg);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(png)) enc.Save(fs);
        window.Close();
    }

    sealed class SyncProgress(Action<string> act) : IProgress<string>
    {
        public void Report(string value) => act(value);
    }

    // ------------------------------------------------------------------ sample document

    static void MakeSample(string path)
    {
        PdfComposer.EnsureFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = "PDF Translator sample";

        // Page 1: real text layer, mixed styles, coloured box, image containing text.
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("Hydraulic Pump Maintenance Guide", new XFont("Arial", 22, XFontStyleEx.Bold), XBrushes.DarkBlue, 50, 70);
            g.DrawLine(new XPen(XColors.DarkBlue, 1.5), 50, 80, 545, 80);

            var body = new XFont("Times New Roman", 11);
            var tf = new PdfSharp.Drawing.Layout.XTextFormatter(g);
            tf.DrawString(
                "This guide describes the routine maintenance of the pump unit. Always switch off the main power supply " +
                "and release the system pressure before opening any part of the hydraulic circuit. Wear protective gloves " +
                "and safety glasses, because hydraulic oil under pressure can cause serious injuries.",
                body, XBrushes.Black, new XRect(50, 95, 495, 60));

            g.DrawString("1. Daily checks", new XFont("Arial", 14, XFontStyleEx.Bold), XBrushes.Black, 50, 180);
            tf.DrawString(
                "Check the oil level in the sight glass. The level must be between the minimum and maximum marks. " +
                "Listen for unusual noises and look for leaks around the hoses and fittings.",
                body, XBrushes.Black, new XRect(50, 190, 495, 45));

            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(200, 40, 40)), 50, 245, 495, 42);
            tf.DrawString("WARNING: Never adjust the relief valve while the pump is running.",
                new XFont("Arial", 12, XFontStyleEx.Bold), XBrushes.White, new XRect(60, 258, 480, 20));

            g.DrawString("2. Replacing the filter", new XFont("Arial", 14, XFontStyleEx.Bold), XBrushes.Black, 50, 320);
            tf.DrawString(
                "Replace the return filter every 500 operating hours. Use only original spare parts (part number HF-2040). " +
                "After replacement, run the pump for five minutes and check the filter housing for leaks.",
                new XFont("Times New Roman", 11, XFontStyleEx.Italic), XBrushes.DarkGreen, new XRect(50, 330, 495, 45));

            using var label = Streams.Exposable(MakeLabelPng(new[] { "DANGER", "High pressure", "Read the manual before use" }, 900, 420));
            var img = XImage.FromStream(label);
            g.DrawImage(img, 150, 400, 300, 140);
            g.DrawString("Figure 1: Warning label on the pump housing", new XFont("Arial", 9, XFontStyleEx.Italic), XBrushes.Gray, 160, 556);
        }

        // Page 2: a "scanned" page — only an image, no text layer.
        var scan = doc.AddPage();
        scan.Size = PdfSharp.PageSize.A4;
        using (var g = XGraphics.FromPdfPage(scan))
        {
            using var ms = Streams.Exposable(MakeScanPng());
            g.DrawImage(XImage.FromStream(ms), 0, 0, scan.Width.Point, scan.Height.Point);
        }
        MakeCjkTablePage(doc);
        doc.Save(path);
    }

    /// <summary>Chinese service-note page: bordered table, bold headings, bold "label：" leads, circled list items.</summary>
    static void MakeCjkTablePage(PdfDocument doc)
    {
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using var g = XGraphics.FromPdfPage(page);
        var body = new XFont("SimSun", 10.5);
        var bold = new XFont("SimSun", 10.5, XFontStyleEx.Bold);     // simulated bold = fill+stroke text
        var head = new XFont("Microsoft YaHei", 12, XFontStyleEx.Bold);
        var black = XBrushes.Black;

        g.DrawString("一．常见 SINAMICS V70 报警列表", head, black, 40, 60);

        double[] cols = { 40, 150, 235, 340, 555 };
        string[][] rows =
        {
            new[] { "SIN808DA 系统报警号", "V70 驱动报警号", "报警内容", "可能原因" },
            new[] { "25000/25201", "F7901", "电机超速", "编码器损坏，编码器电缆不良，干扰" },
            new[] { "25000/25201", "F31110", "串行通讯故障，", "编码器电缆不良，干扰" },
            new[] { "25201", "F31112", "串行记录中的故障|位已置位", "编码器电缆不良，干扰" },
            new[] { "25201", "F52983/4", "没有检测到编码器", "未配置增量编码器电机，编码器电缆不良" },
            new[] { "26101/26102", "", "伺服通讯失败/轴伺|服的运行信号故障", "通讯总线电缆干扰" },
        };
        double y = 75;
        var pen = new XPen(XColors.Black, 0.6);
        foreach (var row in rows)
        {
            int lines = row.Max(c => c.Split('|').Length);
            double h = 8 + lines * 15;
            g.DrawLine(pen, cols[0], y, cols[^1], y);
            for (int c = 0; c < row.Length; c++)
            {
                var parts = row[c].Split('|');
                for (int l = 0; l < parts.Length; l++)
                    g.DrawString(parts[l], row == rows[0] ? bold : body, black, cols[c] + 4, y + 15 + l * 15);
            }
            y += h;
        }
        g.DrawLine(pen, cols[0], y, cols[^1], y);
        foreach (var x in cols) g.DrawLine(pen, x, 75, x, y);

        y += 30;
        g.DrawString("二．故障分析及措施：", head, black, 40, y);
        y += 24;
        g.DrawString("1）F52980 /F52983 故障", bold, black, 40, y);
        y += 21;
        string label = "原因分析：";
        g.DrawString(label, bold, black, 40, y);
        double lw = g.MeasureString(label, bold).Width;
        g.DrawString("V70 与 1FL6 电机在应用上，通过软件固化是一一匹配的，如果编码器损坏或", body, black, 40 + lw, y);
        y += 21;
        g.DrawString("者与 V70 不匹配就引起编码器不能被识别，从而产生 F52980/F52983 报警。", body, black, 40, y);
        y += 21;
        g.DrawString("建议措施：", bold, black, 40, y);
        foreach (var item in new[]
                 {
                     "①检查 V70 固件版本是否太低，尤其是对于 D-code 电机，需要更高的固件版本。",
                     "②确认 V70 与电机的订货号，是否可以匹配使用。",
                     "③更换硬件，如 V70 、编码器或者编码器电缆来排查故障。",
                 })
        {
            y += 21;
            g.DrawString(item, body, black, 40, y);
        }
        y += 30;
        g.DrawString("2）差异比对法", body, black, 40, y);
        foreach (var line in new[]
                 {
                     "此方法适用于现场有同样设备场合可以进行对比，可以快速发现差异，便于快速查找故障",
                     "原因。如安装接线是否差异，测量现场各部份电压电阻是否有差异，加工应用是否有差",
                     "异，从差异入手在逐步排除故障。",
                 })
        {
            y += 21;
            g.DrawString(line, body, black, 40, y);
        }
    }

    static byte[] MakeLabelPng(string[] lines, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(255, 214, 0));
        using var border = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 14 };
        c.DrawRect(10, 10, w - 20, h - 20, border);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var bold = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 110);
        using var reg = new SKFont(SKTypeface.FromFamilyName("Arial"), 56);
        c.DrawText(lines[0], 60, 150, bold, paint);
        c.DrawText(lines[1], 60, 260, reg, paint);
        c.DrawText(lines[2], 60, 350, reg, paint);
        return Encode(bmp);
    }

    static byte[] MakeScanPng()
    {
        const int w = 1654, h = 2339; // A4 at 200 dpi
        using var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(250, 248, 240));
        using var paint = new SKPaint { Color = new SKColor(30, 30, 30), IsAntialias = true };
        using var title = new SKFont(SKTypeface.FromFamilyName("Times New Roman", SKFontStyle.Bold), 64);
        using var text = new SKFont(SKTypeface.FromFamilyName("Times New Roman"), 40);
        c.DrawText("Service Record", 140, 260, title, paint);
        string[] para =
        {
            "The pump was inspected on the date shown below.",
            "All seals were replaced and the pressure test",
            "was completed without any problems.",
        };
        for (int i = 0; i < para.Length; i++) c.DrawText(para[i], 140, 400 + i * 58, text, paint);
        string[] para2 =
        {
            "Next inspection: after twelve months or",
            "two thousand operating hours, whichever comes first.",
        };
        for (int i = 0; i < para2.Length; i++) c.DrawText(para2[i], 140, 680 + i * 58, text, paint);
        return Encode(bmp);
    }

    static byte[] Encode(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

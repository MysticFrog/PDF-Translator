using PdfTranslator.Translators;

namespace PdfTranslator.Core;

public sealed record TranslateOptions(string Source, string Target, OcrMode Ocr);

/// <summary>An open PDF plus per-page analysis and translation results.</summary>
public sealed class TranslationSession : IDisposable
{
    public const int OcrDpi = 200;

    readonly PdfAnalyzer _analyzer;
    readonly Dictionary<string, string> _cache = new();
    readonly Dictionary<int, OcrMode> _analyzedWith = new();

    public string FilePath { get; }
    public byte[] Bytes { get; }
    public int PageCount { get; }
    public PageResult?[] Pages { get; }
    public string TargetLanguage { get; set; } = "en";

    public TranslationSession(string path)
    {
        FilePath = path;
        Bytes = File.ReadAllBytes(path);
        _analyzer = new PdfAnalyzer(Bytes);
        PageCount = _analyzer.PageCount;
        Pages = new PageResult?[PageCount];
    }

    /// <summary>Drops a page's analysis so the next translation detects its blocks again.</summary>
    public void Forget(int index)
    {
        Pages[index] = null;
        _analyzedWith.Remove(index);
    }

    public double PageWidth(int index) => Pages[index]?.Width ?? _analyzer.Geometry(index).Width;

    /// <summary>Finds text blocks (text layer + OCR) on a page without translating. Reuses an existing analysis.</summary>
    public async Task<PageResult> AnalyzePageAsync(int index, OcrMode ocrMode, string source, IProgress<string>? log,
        CancellationToken ct, bool force = false)
    {
        if (!force && Pages[index] is { } existing && _analyzedWith.GetValueOrDefault(index) == ocrMode) return existing;

        log?.Report($"Page {index + 1}: analysing layout");
        var geometry = _analyzer.Geometry(index);
        var raster = await Task.Run(() => PageRenderer.Render(Bytes, index, OcrDpi, geometry.Width), ct);
        var rules = await Task.Run(() => Rules.Detect(raster), ct);
        var layout = await Task.Run(() => _analyzer.Analyze(index, rules), ct);
        var page = layout.Page;
        page.Rules.AddRange(rules.All);

        if (ocrMode != OcrMode.Off)
            await AddOcrRegionsAsync(page, layout, raster, rules, ocrMode, source, log, ct);

        FinishRegions(page, raster);
        Pages[index] = page;
        _analyzedWith[index] = ocrMode;
        return page;
    }

    /// <summary>Background/ink colours, ids, and how much free space each block may grow into.</summary>
    static void FinishRegions(PageResult page, RasterPage raster)
    {
        // Pixels of inline icons, so the composer can draw them inside the translated sentence.
        foreach (var r in page.Regions.Where(r => r.Inlines.Count > 0))
            r.Inlines = r.Inlines.Select(i => i with { Png = PageRenderer.CropPng(raster, i.Bounds.Inflate(0.5)) }).ToList();

        int id = 1;
        var verticalRules = page.Rules.Where(x => x.H > x.W).ToList();
        foreach (var r in page.Regions)
        {
            r.Id = id++;
            // A line near the page centre inside a table is a cell, not centred text.
            if (r.Align == TextAlign.Center && verticalRules.Any(v => v.Y <= r.Bounds.Bottom && v.Bottom >= r.Bounds.Y))
                r.Align = TextAlign.Left;
            r.Background = PageRenderer.SampleBackground(raster, r.Bounds);
            if (r.Origin != RegionSource.TextLayer)
                r.TextColor = PageRenderer.SampleInk(raster, r.Bounds, r.Background);
        }
        foreach (var r in page.Regions)
        {
            double rightLimit = page.Width - Math.Min(r.Bounds.X, 36);
            double bottomLimit = page.Height - 24;
            double topLimit = 24;
            foreach (var o in page.Regions)
            {
                if (o == r) continue;
                if (o.Bounds.X >= r.Bounds.Right - 1 && o.Bounds.VerticalOverlap(r.Bounds) > 0)
                    rightLimit = Math.Min(rightLimit, o.Bounds.X - 3);
                if (o.Bounds.Y >= r.Bounds.Bottom - 1 && o.Bounds.HorizontalOverlap(r.Bounds) > 0)
                    bottomLimit = Math.Min(bottomLimit, o.Bounds.Y - 2);
                if (o.Bounds.Bottom <= r.Bounds.Y + 1 && o.Bounds.HorizontalOverlap(r.Bounds) > 0)
                    topLimit = Math.Max(topLimit, o.Bounds.Bottom + o.FontSize * 0.3 + 2);
            }
            r.MaxRight = PageRenderer.FreeSpaceRight(raster, r.Bounds, r.Background, rightLimit);
            r.MinTop = topLimit >= r.Bounds.Y ? r.Bounds.Y : PageRenderer.FreeSpaceUp(raster, r.Bounds, r.Background, topLimit);
            double start = r.Bounds.Bottom + 0.5;
            r.MaxBottom = start >= bottomLimit ? r.Bounds.Bottom
                : PageRenderer.FreeSpaceDown(raster, r.Bounds, start, r.Background, bottomLimit);
        }
    }

    async Task AddOcrRegionsAsync(PageResult page, PdfAnalyzer.PageLayout layout, RasterPage raster, Rules rules,
        OcrMode mode, string source, IProgress<string>? log, CancellationToken ct)
    {
        bool scanned = layout.LetterCount < 10;
        // Plain text pages (no pictures, no line graphics) have nothing for OCR to find.
        bool graphics = layout.Images.Count > 0 || page.Rules.Count > 0;
        if (mode == OcrMode.Auto && !scanned && !graphics) return;

        // Windows OCR if it has a language for the document's script, else PaddleOCR / Tesseract (data fetched on first use).
        var script = source != Languages.Auto ? TextTools.ScriptOfLanguage(source) : TextTools.Dominant(page.Regions.Select(r => r.Source));
        var (ocr, problem) = await OcrFactory.CreateAsync(source, script, log, ct);
        if (ocr == null)
        {
            page.Note = $"Image text skipped: {problem}.";
            return;
        }

        // Read the whole page: labels in diagrams are often drawn as outlines (vector), not text or pictures.
        // Words the text layer already has are dropped below. Tesseract is too noisy on line drawings, so with it
        // the default mode reads only the embedded images.
        var areas = new List<(RectD area, RegionSource origin)>();
        if (mode == OcrMode.FullPage || scanned || ocr is not TesseractOcr)
            areas.Add((new RectD(0, 0, page.Width, page.Height), RegionSource.PageOcr));
        else
            areas.AddRange(layout.Images.Select(i => (i, RegionSource.ImageOcr)));
        if (areas.Count == 0) return;
        if (ocr.PreferredDpi > OcrDpi)
            raster = await Task.Run(() => PageRenderer.Render(Bytes, page.Index, ocr.PreferredDpi, page.Width), ct);

        var textBlocks = page.Regions.Select(r => r.Bounds).ToList();
        foreach (var (area, origin) in areas)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Page {page.Index + 1}: OCR ({ocr.Name}) {(origin == RegionSource.PageOcr ? "full page" : "image")}");
            var lines = await ocr.RecognizeAsync(raster, area, sparse: origin == RegionSource.ImageOcr);

            // Drop words the text layer already covers (e.g. scans that carry an invisible OCR text layer).
            lines = lines
                .Select(l => l.Where(w => !textBlocks.Any(t => t.IntersectionArea(w.Box) > w.Box.Area * 0.4)).ToList())
                .Where(l => l.Count > 0).ToList();
            if (lines.Count == 0) continue;

            // Bold words carry noticeably more ink than the page's typical word.
            var density = lines.SelectMany(l => l).ToDictionary(w => w, w => PageRenderer.InkDensity(raster, w.Box));
            double median = density.Values.OrderBy(v => v).ElementAt(density.Count / 2);
            var styled = lines.Select(l => l.Select(w => new Glyph
            {
                Box = w.Box, Text = w.Text, Size = w.Size, Baseline = w.Baseline,
                Bold = density.Count >= 6 && density[w] > median * 1.45,
            }).ToList()).ToList();

            var segs = LayoutBuilder.LinesFromWords(styled, rules);
            foreach (var region in LayoutBuilder.BuildBlocks(segs, rules, origin, page.Width))
            {
                if (!LooksLikeText(region.Source)) continue;
                // Text read from inside a picture is image text (it goes when the picture is deleted).
                var c = (x: region.Bounds.X + region.Bounds.W / 2, y: region.Bounds.Y + region.Bounds.H / 2);
                if (layout.Images.Any(i => i.Inflate(1).Contains(c.x, c.y))) region.Origin = RegionSource.ImageOcr;
                page.Regions.Add(region);
            }
        }
    }

    /// <summary>Rejects OCR noise from drawings: dash/rule fragments ("-一一") and single repeated strokes.</summary>
    static bool LooksLikeText(string s)
    {
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count < 2) return false;
        var meaningful = letters.Where(c => c is not ('一' or '丨' or '丿' or '丶' or 'l' or 'I' or '|')).ToList();
        return meaningful.Count >= 2 || meaningful.Distinct().Count() >= 2;
    }

    /// <summary>
    /// Adds a block the detector missed (drawn by the user or placed by Claude). Without text, the box is OCR'd
    /// on its own, which reads small labels far better than OCR over a whole figure.
    /// </summary>
    public async Task<TextRegion> AddRegionAsync(int index, RectD bounds, string? source, string? translation, double? fontSize,
        string sourceLanguage, IProgress<string>? log, CancellationToken ct)
    {
        var page = Pages[index] ?? throw new InvalidOperationException("Analyse the page first.");
        var raster = await Task.Run(() => PageRenderer.Render(Bytes, index, OcrDpi, page.Width), ct);
        var bg = PageRenderer.SampleBackground(raster, bounds);
        var (lineCount, inkSize) = PageRenderer.EstimateTextLines(raster, bounds, bg);

        if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(translation))
        {
            var script = sourceLanguage != Languages.Auto ? TextTools.ScriptOfLanguage(sourceLanguage) : TextTools.Dominant(page.Regions.Select(r => r.Source));
            var (ocr, problem) = await OcrFactory.CreateAsync(sourceLanguage, script, log, ct);
            if (ocr != null)
            {
                var hi = ocr.PreferredDpi > OcrDpi ? await Task.Run(() => PageRenderer.Render(Bytes, index, ocr.PreferredDpi, page.Width), ct) : raster;
                var lines = await ocr.RecognizeAsync(hi, bounds.Inflate(4), sparse: false);
                source = PdfAnalyzer.JoinLines(lines.Select(l => string.Concat(l.Select((g, i) =>
                    i > 0 && !(TextTools.IsCjk(l[i - 1].Text[^1]) && TextTools.IsCjk(g.Text[0])) ? " " + g.Text : g.Text))));
            }
            else log?.Report("OCR unavailable: " + problem);
        }

        double size = fontSize ?? (inkSize > 0 ? inkSize : Math.Clamp(bounds.H * 0.75, 5, 24));
        var region = new TextRegion
        {
            Id = page.NextId(),
            Bounds = bounds,
            Source = source?.Trim() ?? "",
            Translated = string.IsNullOrWhiteSpace(translation) ? null : translation.Trim(),
            ManualText = !string.IsNullOrWhiteSpace(translation),
            Lines = new() { new SourceLine(bounds, source?.Trim() ?? "") },
            LineCount = Math.Max(1, lineCount),
            FontSize = Math.Round(size * 2) / 2,
            Background = bg,
            TextColor = PageRenderer.SampleInk(raster, bounds, bg),
            Origin = RegionSource.Added,
            MaxRight = bounds.Right,
            MaxBottom = bounds.Bottom,
            MinTop = bounds.Y,
        };
        page.Regions.Add(region);
        page.Translated = true;
        return region;
    }

    /// <summary>Analyses (if needed) and translates one page. Hand-edited blocks keep their text.</summary>
    public async Task<PageResult> TranslatePageAsync(int index, TranslateOptions opt, ITranslator? translator,
        IProgress<string>? log, CancellationToken ct)
    {
        TargetLanguage = opt.Target;
        var page = await AnalyzePageAsync(index, opt.Ocr, opt.Source, log, ct);
        if (translator != null)
            await TranslateRegionsAsync(page.Regions.Where(r => !r.ManualText).ToList(), page, opt, translator, log, $"Page {index + 1}", ct);
        page.Translated = true;
        return page;
    }

    /// <summary>Translates the given regions again, ignoring cached results.</summary>
    public async Task RetranslateAsync(PageResult page, IReadOnlyList<TextRegion> regions, TranslateOptions opt, ITranslator translator,
        IProgress<string>? log, CancellationToken ct)
    {
        TargetLanguage = opt.Target;
        regions = regions.Where(r => !string.IsNullOrWhiteSpace(r.Source)).ToList();
        foreach (var r in regions)
        {
            r.ManualText = false;
            _cache.Remove(Key(translator, opt, TextTools.SplitNumbering(r.Source).body));
        }
        await TranslateRegionsAsync(regions.ToList(), page, opt, translator, log, "Selection", ct);
        page.Translated = true;
    }

    static string Key(ITranslator t, TranslateOptions o, string text) => $"{t.Id}|{o.Source}|{o.Target}|{text}";

    async Task TranslateRegionsAsync(List<TextRegion> regions, PageResult page, TranslateOptions opt, ITranslator translator,
        IProgress<string>? log, string label, CancellationToken ct)
    {
        var script = opt.Source == Languages.Auto
            ? TextTools.Dominant(page.Regions.Select(r => r.Source))
            : TextTools.ScriptOfLanguage(opt.Source);

        var work = new List<(TextRegion region, string prefix, string body, string? pageRef)>();
        foreach (var r in regions)
        {
            if (!TextTools.ShouldTranslate(r.Source, script)) { r.Translated = r.Source; continue; }
            // Contents line: translate the title only; the dot leaders are redrawn and the page ref kept.
            var (text, pageRef) = r.LineCount == 1 ? TextTools.SplitLeader(r.Source) : (r.Source, null);
            var (prefix, body) = TextTools.SplitNumbering(text);
            if (!TextTools.ShouldTranslate(body, script)) { r.Translated = r.Source; continue; }
            work.Add((r, prefix, body, pageRef));
        }

        var pending = work.Select(w => w.body).Where(b => !_cache.ContainsKey(Key(translator, opt, b))).Distinct().ToList();
        if (pending.Count > 0)
        {
            log?.Report($"{label}: translating {pending.Count} block(s) with {translator.DisplayName}");
            var output = await translator.TranslateAsync(pending, opt.Source, opt.Target, ct);
            for (int i = 0; i < pending.Count; i++) _cache[Key(translator, opt, pending[i])] = output[i];
        }
        foreach (var (r, prefix, body, pageRef) in work)
        {
            string translated = _cache[Key(translator, opt, body)];
            string number = TextTools.LocalizeNumbering(prefix, opt.Target);
            r.Translated = TextTools.RestoreInlineTokens(
                               TextTools.NormalizeForTarget((number.Length > 0 ? number + " " : "") + translated, opt.Target), r.Inlines.Count) +
                           (pageRef != null ? "\t" + TextTools.NormalizeForTarget(pageRef, opt.Target) : "");
        }
    }

    // ------------------------------------------------------------------ images

    readonly Dictionary<int, List<PageImage>> _objects = new();
    PdfSharp.Pdf.PdfDocument? _structure;

    /// <summary>Objects the user removed, per page; applied when the output is written.</summary>
    public Dictionary<int, List<DeletedImage>> DeletedImages { get; } = new();

    List<PageImage> AllObjects(int index)
    {
        if (_objects.TryGetValue(index, out var list)) return list;
        _structure ??= PdfSharp.Pdf.IO.PdfReader.Open(Streams.Exposable(Bytes), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        try { list = ImageRemover.List(_structure.Pages[index]); }
        catch { list = new(); }
        return _objects[index] = list;
    }

    /// <summary>Images, form objects (stamps, watermarks) and annotations the user can delete.</summary>
    public IReadOnlyList<PageImage> ImagesOn(int index) => AllObjects(index).Where(o => !o.Hidden).ToList();

    public bool IsDeleted(int index, int imageId) => DeletedImages.TryGetValue(index, out var d) && d.Any(x => x.Id == imageId);

    /// <summary>Deletes objects; links lying on a deleted object and OCR blocks read from inside it go too.</summary>
    public Task DeleteImagesAsync(int index, IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        var all = AllObjects(index);
        var objects = all.Where(o => idSet.Contains(o.Id) && !IsDeleted(index, o.Id)).ToList();
        if (objects.Count == 0) return Task.CompletedTask;
        if (!DeletedImages.TryGetValue(index, out var list)) DeletedImages[index] = list = new();
        foreach (var o in objects)
        {
            list.Add(new DeletedImage(o.Id, o.Bounds, o.Kind, o.Index));
            // e.g. the "click to buy" link sitting on a watermark stamp
            foreach (var link in all.Where(h => h.Hidden && h.Label.StartsWith("Link") && !IsDeleted(index, h.Id) &&
                                                h.Bounds.IntersectionArea(o.Bounds) > Math.Min(h.Bounds.Area, o.Bounds.Area) * 0.8))
                list.Add(new DeletedImage(link.Id, link.Bounds, link.Kind, link.Index));
            Pages[index]?.Regions.RemoveAll(r => r.Origin == RegionSource.ImageOcr &&
                o.Bounds.Inflate(2).Contains(r.Bounds.X + r.Bounds.W / 2, r.Bounds.Y + r.Bounds.H / 2));
        }
        return Task.CompletedTask;
    }

    public void RestoreImages(int index, IEnumerable<int> ids)
    {
        if (!DeletedImages.TryGetValue(index, out var list)) return;
        var restore = list.Where(d => ids.Contains(d.Id)).ToList();
        // Links removed together with an object come back with it.
        list.RemoveAll(d => restore.Contains(d) ||
            AllObjects(index).Any(o => o.Id == d.Id && o.Hidden && restore.Any(r => r.Bounds.IntersectionArea(d.Bounds) > 0)));
    }

    /// <summary>
    /// Deletes images with the same size and position as <paramref name="image"/> on every page (repeated
    /// stamps, logos, watermarks). Returns the page indexes that changed.
    /// </summary>
    public async Task<List<int>> DeleteMatchingOnAllPagesAsync(PageImage image, CancellationToken ct = default)
    {
        var changed = new List<int>();
        for (int p = 0; p < PageCount; p++)
        {
            var same = ImagesOn(p).Where(i => i.Kind == image.Kind && Math.Abs(i.Bounds.X - image.Bounds.X) < 2 && Math.Abs(i.Bounds.Y - image.Bounds.Y) < 2 &&
                                              Math.Abs(i.Bounds.W - image.Bounds.W) < 2 && Math.Abs(i.Bounds.H - image.Bounds.H) < 2)
                .Where(i => !IsDeleted(p, i.Id)).Select(i => i.Id).ToList();
            if (same.Count == 0) continue;
            await DeleteImagesAsync(p, same, ct);
            changed.Add(p);
        }
        return changed;
    }

    public byte[] ComposePage(int index) => PdfComposer.ComposePage(Bytes, Pages, DeletedImages, TargetLanguage, index);

    public void Save(string path)
    {
        using var fs = File.Create(path);
        PdfComposer.Compose(Bytes, Pages, DeletedImages, TargetLanguage, fs);
    }

    public void Dispose()
    {
        _analyzer.Dispose();
        _structure?.Dispose();
    }
}

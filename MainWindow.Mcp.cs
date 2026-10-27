using System.Text.Json.Nodes;
using System.Windows;
using PdfTranslator.Core;
using PdfTranslator.Mcp;
using PdfTranslator.Translators;

namespace PdfTranslator;

/// <summary>MCP tools that let Claude (Claude Code / Claude Desktop) read and edit the open document.</summary>
public partial class MainWindow
{
    const string McpInstructions =
        "Controls the PDF Translator desktop app that is open on the user's screen (side-by-side original/translated view). " +
        "Typical workflow: get_status; open_pdf if no file is open; analyze_page to detect text blocks (text layer + OCR); " +
        "get_page_image to see the layout; translate the blocks yourself and send them with set_translations (keep list " +
        "numbering, units, part numbers and codes; keep each translation about as long as the source so it fits its box); " +
        "get_page_image with view=translated to check the result; fix layout with update_block, merge_blocks (a sentence " +
        "split across blocks) or split_block (separate items wrongly joined). Text inside images (diagram labels, screenshots) " +
        "may be missed by OCR: compare get_page_image view=original with the blocks, and for each missed label call add_block " +
        "with its box, the source text you read and your translation. Then save_pdf. Page numbers are 1-based. " +
        "Block coordinates are PDF points from the page's top-left corner. Every edit can be undone by the user (Ctrl+Z).";

    void StartMcp()
    {
        _mcp?.Dispose();
        _mcp = null;
        if (!_settings.McpEnabled) { McpText.Text = "MCP off"; return; }
        try
        {
            var server = new McpServer(_settings.McpPort, BuildTools(), McpInstructions);
            server.Start();
            _mcp = server;
            McpText.Text = $"MCP ● localhost:{_settings.McpPort}";
        }
        catch (Exception ex)
        {
            McpText.Text = $"MCP: port {_settings.McpPort} unavailable";
            McpText.ToolTip = ex.Message;
        }
    }

    internal void McpText_Click(object sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        if (_mcp == null) { Settings_Click(sender, e); return; }
        string url = _mcp.Url;
        string args = $"mcp add --scope user --transport http pdf-translator {url}";
        string? cli = Translators.ClaudeCodeTranslator.Locate(_settings.ClaudeCodePath);
        var actions = new List<ActionDialog.Action>();
        if (cli != null)
            actions.Add(new("Add to Claude Code", () => Launch.Terminal(cli, args),
                ToolTip: "Opens a terminal running: claude " + args));
        actions.Add(new("Add to Claude Desktop", () => AddToClaudeDesktop(),
            ToolTip: "Adds a pdf-translator entry to claude_desktop_config.json (a backup is kept)"));
        actions.Add(new("Copy endpoint", () => Launch.Copy(url)));
        actions.Add(new("Copy Claude Code command", () => Launch.Copy("claude " + args)));

        ActionDialog.Show(this, "Connect Claude",
            $"PDF Translator is serving MCP at {url} while it runs.\n\n" +
            "Connect Claude once, then ask it things like “translate the PDF open in PDF Translator into English”: it reads the " +
            "blocks and page images, writes the translations and fixes the layout while you watch here.\n\n" +
            "• Claude Code: “Add to Claude Code” registers the server for all your projects.\n" +
            "• Claude Desktop: “Add to Claude Desktop” adds it to the config; restart Claude Desktop afterwards.",
            actions.ToArray());
    }

    /// <summary>Registers the stdio bridge in Claude Desktop's config (claude_desktop_config.json), keeping a backup.</summary>
    void AddToClaudeDesktop()
    {
        try
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            var servers = root["mcpServers"] as JsonObject;
            if (servers == null) root["mcpServers"] = servers = new JsonObject();
            servers["pdf-translator"] = new JsonObject
            {
                ["command"] = Environment.ProcessPath,
                ["args"] = new JsonArray("--mcp-stdio", "--port", _settings.McpPort.ToString()),
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            SetStatus("Added to Claude Desktop. Restart Claude Desktop to load it.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update Claude Desktop's config:\n" + ex.Message, Title);
        }
    }

    // ---------------------------------------------------------------- tool table

    IReadOnlyList<McpTool> BuildTools()
    {
        static JsonObject Schema(JsonObject props, params string[] required) => new()
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            ["additionalProperties"] = false,
        };
        static JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };
        static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
        static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
        JsonObject Page() => Int("1-based page number; defaults to the page shown in the app");

        return new List<McpTool>
        {
            new("get_status", "Open file, page count, languages, selected service and which pages are analysed/translated.",
                Schema(new JsonObject()), (a, ct) => Ui(() => McpResult.Json(StatusJson()))),

            new("open_pdf", "Open a PDF file in the app (absolute path).",
                Schema(new JsonObject { ["path"] = Str("Absolute path of the PDF") }, "path"),
                (a, ct) => Ui(() =>
                {
                    string path = a["path"]!.GetValue<string>();
                    if (!File.Exists(path)) return McpResult.Text($"File not found: {path}", true);
                    return OpenFile(path) ? McpResult.Json(StatusJson()) : McpResult.Text("Could not open the file (is a translation running?)", true);
                })),

            new("set_options", "Change source/target language, translation service or OCR mode in the app.",
                Schema(new JsonObject
                {
                    ["source"] = Str("Source language code or 'auto' (" + string.Join(", ", Languages.All.Select(l => l.Code)) + ")"),
                    ["target"] = Str("Target language code"),
                    ["provider"] = Str("Service id: " + string.Join(", ", TranslatorFactory.Providers.Select(p => p.Id))),
                    ["ocr"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Auto", "FullPage", "Off") },
                }), (a, ct) => Ui(() =>
                {
                    if (a["source"]?.GetValue<string>() is { } s) SourceBox.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == s) ?? throw new ArgumentException($"Unknown language '{s}'");
                    if (a["target"]?.GetValue<string>() is { } t) TargetBox.SelectedItem = Languages.Targets.FirstOrDefault(l => l.Code == t) ?? throw new ArgumentException($"Unknown language '{t}'");
                    if (a["provider"]?.GetValue<string>() is { } p) ProviderBox.SelectedItem = TranslatorFactory.Providers.FirstOrDefault(x => x.Id == p) ?? throw new ArgumentException($"Unknown provider '{p}'");
                    if (a["ocr"]?.GetValue<string>() is { } o) OcrBox.SelectedValue = Enum.Parse<OcrMode>(o, true);
                    if (_session != null) _session.TargetLanguage = CurrentOptions().Target;
                    SaveChoices();
                    return McpResult.Json(StatusJson());
                })),

            new("analyze_page", "Detect the text blocks on a page (text layer, table cells, OCR of images/scans) without translating. Returns the blocks.",
                Schema(new JsonObject { ["page"] = Page(), ["force"] = Bool("Re-detect even if already analysed (discards edits)") }),
                (a, ct) => UiAsync(async () =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    if (a["force"]?.GetValue<bool>() == true) { if (_session!.Pages[index] is { } old) PushUndo(old); _session.Forget(index); }
                    var opt = CurrentOptions();
                    _session!.TargetLanguage = opt.Target;
                    var page = await _session.AnalyzePageAsync(index, opt.Ocr, opt.Source, new Progress<string>(SetStatus), ct);
                    page.Translated = true;
                    ShowPage(index);
                    return McpResult.Json(PageJson(page));
                })),

            new("get_blocks", "List the blocks of an analysed page: id, position, style, source text and current translation.",
                Schema(new JsonObject { ["page"] = Page() }),
                (a, ct) => Ui(() =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    return _session!.Pages[index] is { } page ? McpResult.Json(PageJson(page)) : McpResult.Text("Page not analysed yet; call analyze_page.", true);
                })),

            new("get_page_image", "Render a page as PNG: the original, the translated result, or both.",
                Schema(new JsonObject
                {
                    ["page"] = Page(),
                    ["view"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("original", "translated", "both") },
                    ["dpi"] = Int("Resolution, 50-200 (default 100)"),
                }),
                (a, ct) => UiAsync(async () =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    string view = a["view"]?.GetValue<string>() ?? "original";
                    int dpi = Math.Clamp(a["dpi"]?.GetValue<int>() ?? 100, 50, 200);
                    var session = _session!;
                    double width = session.PageWidth(index);
                    var result = McpResult.Text($"Page {index + 1} ({view}, {dpi} dpi). 1 pt = {dpi / 72.0:0.###} px.");
                    if (view is "original" or "both")
                        result.AddImage(await Task.Run(() => PageRenderer.ToPng(PageRenderer.Render(session.Bytes, index, dpi, width)), ct));
                    if (view is "translated" or "both")
                    {
                        if (session.Pages[index]?.Translated != true) return McpResult.Text("Page has no translation yet.", true);
                        var composed = session.ComposePage(index);
                        result.AddImage(await Task.Run(() => PageRenderer.ToPng(PageRenderer.Render(composed, 0, dpi, width)), ct));
                    }
                    return result;
                })),

            new("set_translations", "Set the translated text of blocks (by id). Marks them as hand-made so automatic translation won't overwrite them.",
                Schema(new JsonObject
                {
                    ["page"] = Page(),
                    ["translations"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Schema(new JsonObject { ["id"] = Int("Block id"), ["text"] = Str("Translated text; use \\n for a forced line break") }, "id", "text"),
                    },
                }, "translations"),
                (a, ct) => Ui(() =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    PushUndo(page!);
                    var missing = new List<int>();
                    int n = 0;
                    foreach (var item in a["translations"]!.AsArray().OfType<JsonObject>())
                    {
                        int id = item["id"]!.GetValue<int>();
                        var r = page!.Regions.FirstOrDefault(x => x.Id == id);
                        if (r == null) { missing.Add(id); continue; }
                        r.Translated = item["text"]!.GetValue<string>();
                        r.ManualText = true;
                        r.KeepOriginal = false;
                        n++;
                    }
                    page!.Translated = true;
                    _session!.TargetLanguage = CurrentOptions().Target;
                    ShowOrRefresh(page.Index);
                    return McpResult.Text($"Updated {n} block(s)." + (missing.Count > 0 ? $" Unknown ids: {string.Join(", ", missing)}." : ""), missing.Count > 0 && n == 0);
                })),

            new("translate_page", "Translate a page with one of the app's built-in services (not Claude). Hand-edited blocks are kept.",
                Schema(new JsonObject { ["page"] = Page(), ["provider"] = Str("Service id; defaults to the one selected in the app") }),
                (a, ct) => UiAsync(async () =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    if (_cts != null) return McpResult.Text("The app is busy translating; try again shortly.", true);
                    var translator = CreateTranslator(a["provider"]?.GetValue<string>());
                    if (translator == null) return McpResult.Text("Provider 'none' does not translate; use set_translations.", true);
                    var page = await _session!.TranslatePageAsync(index, CurrentOptions(), translator, new Progress<string>(SetStatus), ct);
                    _translatedCache.Remove(index);
                    ShowPage(index);
                    return McpResult.Json(PageJson(page));
                })),

            new("update_block", "Change a block's layout or style. Omitted fields stay as they are.",
                Schema(new JsonObject
                {
                    ["page"] = Page(),
                    ["id"] = Int("Block id"),
                    ["x"] = Num("Left edge of the translation box (pt)"),
                    ["y"] = Num("Top edge (pt)"),
                    ["width"] = Num("Width (pt)"),
                    ["height"] = Num("Height (pt)"),
                    ["font_size"] = Num("Fixed font size in pt; 0 = automatic fit"),
                    ["bold"] = Bool("Bold"),
                    ["italic"] = Bool("Italic"),
                    ["align"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Left", "Center", "Right") },
                    ["keep_original"] = Bool("Leave the original text instead of the translation"),
                    ["reset_layout"] = Bool("Return to automatic position and size"),
                }, "id"),
                (a, ct) => Ui(() =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    var r = page!.Regions.FirstOrDefault(x => x.Id == a["id"]!.GetValue<int>());
                    if (r == null) return McpResult.Text("No block with that id.", true);
                    PushUndo(page);
                    if (a["reset_layout"]?.GetValue<bool>() == true) { r.OutBounds = null; r.FontSizeOverride = null; }
                    if (a["x"] != null || a["y"] != null || a["width"] != null || a["height"] != null)
                    {
                        var b = r.DrawBounds;
                        r.OutBounds = new RectD(
                            a["x"]?.GetValue<double>() ?? b.X, a["y"]?.GetValue<double>() ?? b.Y,
                            Math.Max(4, a["width"]?.GetValue<double>() ?? b.W), Math.Max(4, a["height"]?.GetValue<double>() ?? b.H));
                    }
                    if (a["font_size"]?.GetValue<double>() is { } fs) r.FontSizeOverride = fs <= 0 ? null : fs;
                    if (a["bold"]?.GetValue<bool>() is { } bold) { r.Bold = bold; r.BoldLead = false; }
                    if (a["italic"]?.GetValue<bool>() is { } it) r.Italic = it;
                    if (a["align"]?.GetValue<string>() is { } al) r.Align = Enum.Parse<TextAlign>(al, true);
                    if (a["keep_original"]?.GetValue<bool>() is { } keep) r.KeepOriginal = keep;
                    ShowOrRefresh(page.Index);
                    return McpResult.Json(BlockJson(r));
                })),

            new("merge_blocks", "Merge several blocks into one (reading order), e.g. a sentence that was split across blocks.",
                Schema(new JsonObject { ["page"] = Page(), ["ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["minItems"] = 2 } }, "ids"),
                (a, ct) => Ui(() =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    var ids = a["ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
                    var regions = page!.Regions.Where(r => ids.Contains(r.Id)).ToList();
                    if (regions.Count < 2) return McpResult.Text("Need at least two existing block ids.", true);
                    PushUndo(page);
                    var merged = MergeRegions(page, regions, CurrentOptions().Target);
                    ShowOrRefresh(page.Index);
                    return McpResult.Json(BlockJson(merged));
                })),

            new("split_block", "Split a block into one block per source line (their translations are cleared; set them with set_translations).",
                Schema(new JsonObject { ["page"] = Page(), ["id"] = Int("Block id") }, "id"),
                (a, ct) => Ui(() =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    var r = page!.Regions.FirstOrDefault(x => x.Id == a["id"]!.GetValue<int>());
                    if (r == null || r.Lines.Count < 2) return McpResult.Text("No such block, or it has only one line.", true);
                    PushUndo(page);
                    var parts = SplitRegion(page, r);
                    ShowOrRefresh(page.Index);
                    return McpResult.Json(new JsonArray(parts.Select(p => (JsonNode)BlockJson(p)).ToArray()));
                })),

            new("add_block", "Add a block the detector missed (e.g. a label inside an image). Give the box around the original text " +
                "in PDF points (top-left origin); with neither source nor translation the app OCRs the box itself.",
                Schema(new JsonObject
                {
                    ["page"] = Page(),
                    ["x"] = Num("Left (pt)"), ["y"] = Num("Top (pt)"), ["width"] = Num("Width (pt)"), ["height"] = Num("Height (pt)"),
                    ["source"] = Str("Original text in the box, if known"),
                    ["translation"] = Str("Translated text to draw"),
                    ["font_size"] = Num("Font size in pt; estimated from the ink if omitted"),
                    ["bold"] = Bool("Bold"),
                }, "x", "y", "width", "height"),
                (a, ct) => UiAsync(async () =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    PushUndo(page!);
                    var box = new RectD(a["x"]!.GetValue<double>(), a["y"]!.GetValue<double>(),
                        Math.Max(2, a["width"]!.GetValue<double>()), Math.Max(2, a["height"]!.GetValue<double>()));
                    var r = await _session!.AddRegionAsync(page!.Index, box, a["source"]?.GetValue<string>(), a["translation"]?.GetValue<string>(),
                        a["font_size"]?.GetValue<double>(), CurrentOptions().Source, new Progress<string>(SetStatus), ct);
                    if (a["bold"]?.GetValue<bool>() is { } bold) r.Bold = bold;
                    ShowOrRefresh(page.Index);
                    return McpResult.Json(BlockJson(r));
                })),

            new("delete_block", "Remove blocks (the original text stays visible), e.g. OCR noise.",
                Schema(new JsonObject { ["page"] = Page(), ["ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } } }, "ids"),
                (a, ct) => Ui(() =>
                {
                    var (page, err) = AnalysedPage(a);
                    if (err != null) return err;
                    var ids = a["ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
                    PushUndo(page!);
                    int n = page!.Regions.RemoveAll(r => ids.Contains(r.Id));
                    ShowOrRefresh(page.Index);
                    return McpResult.Text($"Removed {n} block(s).");
                })),

            new("list_images", "List the removable objects on a page: raster images, form objects (stamps, watermarks, logos) and annotations, with ids and positions.",
                Schema(new JsonObject { ["page"] = Page() }),
                (a, ct) => Ui(() =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    return McpResult.Json(new JsonArray(_session!.ImagesOn(index).Select(i => (JsonNode)new JsonObject
                    {
                        ["id"] = i.Id,
                        ["kind"] = i.Kind.ToString(),
                        ["label"] = i.Label,
                        ["x"] = Math.Round(i.Bounds.X, 1), ["y"] = Math.Round(i.Bounds.Y, 1),
                        ["width"] = Math.Round(i.Bounds.W, 1), ["height"] = Math.Round(i.Bounds.H, 1),
                        ["deleted"] = _session.IsDeleted(index, i.Id),
                    }).ToArray()));
                })),

            new("delete_images", "Remove images from the output PDF (their draw operations are deleted from the page content). " +
                "all_pages also removes images with the same size and position on every page (repeated stamps/logos).",
                Schema(new JsonObject
                {
                    ["page"] = Page(),
                    ["ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
                    ["all_pages"] = Bool("Also delete matching images on every page"),
                }, "ids"),
                (a, ct) => UiAsync(async () =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    var ids = a["ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
                    var images = _session!.ImagesOn(index).Where(i => ids.Contains(i.Id)).ToList();
                    if (images.Count == 0) return McpResult.Text("No images with those ids; call list_images.", true);
                    var pages = new HashSet<int> { index };
                    PushUndo(index);
                    await _session.DeleteImagesAsync(index, ids, ct);
                    if (a["all_pages"]?.GetValue<bool>() == true)
                    {
                        for (int p = 0; p < _session.PageCount; p++) if (p != index) PushUndo(p);
                        foreach (var img in images)
                            pages.UnionWith(await _session.DeleteMatchingOnAllPagesAsync(img, ct));
                    }
                    foreach (var p in pages) _translatedCache.Remove(p);
                    ShowOrRefresh(index);
                    return McpResult.Text($"Deleted on page(s) {string.Join(", ", pages.OrderBy(p => p).Select(p => p + 1))}.");
                })),

            new("restore_images", "Bring deleted images back.",
                Schema(new JsonObject { ["page"] = Page(), ["ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } } }, "ids"),
                (a, ct) => Ui(() =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    PushUndo(index);
                    _session!.RestoreImages(index, a["ids"]!.AsArray().Select(n => n!.GetValue<int>()));
                    ShowOrRefresh(index);
                    return McpResult.Text("Restored.");
                })),

            new("show_page", "Show a page in the app.",
                Schema(new JsonObject { ["page"] = Page() }, "page"),
                (a, ct) => Ui(() =>
                {
                    var (index, err) = PageArg(a);
                    if (err != null) return err;
                    ShowPage(index);
                    return McpResult.Text($"Showing page {index + 1}.");
                })),

            new("save_pdf", "Write the translated PDF. Untranslated pages are copied unchanged.",
                Schema(new JsonObject { ["path"] = Str("Output path; defaults to <name>.<target>.pdf next to the source") }),
                (a, ct) => Ui(() =>
                {
                    if (_session == null) return McpResult.Text("No PDF is open.", true);
                    string path = a["path"]?.GetValue<string>()
                        ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_session.FilePath)!, DefaultOutputName());
                    _session.TargetLanguage = CurrentOptions().Target;
                    _session.Save(path);
                    SetStatus($"Saved {path} (via MCP)");
                    return McpResult.Text($"Saved {path}");
                })),
        };
    }

    // ---------------------------------------------------------------- helpers

    Task<McpResult> Ui(Func<McpResult> f) => Dispatcher.InvokeAsync(f).Task;

    Task<McpResult> UiAsync(Func<Task<McpResult>> f) => Dispatcher.InvokeAsync(f).Task.Unwrap();

    (int index, McpResult? error) PageArg(JsonObject a)
    {
        if (_session == null) return (0, McpResult.Text("No PDF is open; call open_pdf first.", true));
        int index = (a["page"]?.GetValue<int>() ?? _page + 1) - 1;
        if (index < 0 || index >= _session.PageCount) return (0, McpResult.Text($"Page must be 1..{_session.PageCount}.", true));
        return (index, null);
    }

    (PageResult? page, McpResult? error) AnalysedPage(JsonObject a)
    {
        var (index, err) = PageArg(a);
        if (err != null) return (null, err);
        if (_cts != null) return (null, McpResult.Text("The app is busy translating; try again shortly.", true));
        var page = _session!.Pages[index];
        return page == null ? (null, McpResult.Text("Page not analysed yet; call analyze_page.", true)) : (page, null);
    }

    void ShowOrRefresh(int index)
    {
        _translatedCache.Remove(index);
        if (index != _page) ShowPage(index);
        else { RefreshBlockList(); AfterEdit(); }
    }

    JsonObject StatusJson()
    {
        var opt = CurrentOptions();
        return new JsonObject
        {
            ["file"] = _session?.FilePath,
            ["page_count"] = _session?.PageCount ?? 0,
            ["current_page"] = _session == null ? null : _page + 1,
            ["source"] = opt.Source,
            ["target"] = opt.Target,
            ["provider"] = (ProviderBox.SelectedItem as ProviderInfo)?.Id,
            ["ocr"] = opt.Ocr.ToString(),
            ["analysed_pages"] = _session == null ? new JsonArray() :
                new JsonArray(Enumerable.Range(0, _session.PageCount).Where(i => _session.Pages[i] != null).Select(i => (JsonNode)(i + 1)).ToArray()),
            ["pages_with_translations"] = _session == null ? new JsonArray() :
                new JsonArray(Enumerable.Range(0, _session.PageCount).Where(i => _session.Pages[i]?.Regions.Any(r => r.HasOverlay) == true).Select(i => (JsonNode)(i + 1)).ToArray()),
            ["busy"] = _cts != null,
        };
    }

    static JsonObject PageJson(PageResult p) => new()
    {
        ["page"] = p.Index + 1,
        ["width_pt"] = Math.Round(p.Width, 1),
        ["height_pt"] = Math.Round(p.Height, 1),
        ["note"] = p.Note,
        ["blocks"] = new JsonArray(p.Regions.Select(r => (JsonNode)BlockJson(r)).ToArray()),
    };

    static JsonObject BlockJson(TextRegion r)
    {
        var b = r.DrawBounds;
        return new JsonObject
        {
            ["id"] = r.Id,
            ["origin"] = r.Origin.ToString(),
            ["x"] = Math.Round(b.X, 1), ["y"] = Math.Round(b.Y, 1), ["width"] = Math.Round(b.W, 1), ["height"] = Math.Round(b.H, 1),
            ["lines"] = r.LineCount,
            ["font_size"] = Math.Round(r.FontSize, 1),
            ["font_size_override"] = r.FontSizeOverride,
            ["bold"] = r.Bold,
            ["bold_label"] = r.BoldLead,
            ["italic"] = r.Italic,
            ["align"] = r.Align.ToString(),
            ["source"] = r.Source,
            ["translation"] = r.Translated,
            ["manual"] = r.ManualText,
            ["keep_original"] = r.KeepOriginal,
        };
    }
}

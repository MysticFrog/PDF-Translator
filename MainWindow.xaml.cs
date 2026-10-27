using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfTranslator.Core;
using PdfTranslator.Mcp;
using PdfTranslator.Translators;

namespace PdfTranslator;

public partial class MainWindow : Window
{
    const int PreviewDpi = 144; // bitmaps are tagged with this DPI so 100% zoom = real paper size

    readonly AppSettings _settings = AppSettings.Load();
    readonly Dictionary<int, BitmapSource> _originalCache = new();
    readonly Dictionary<int, BitmapSource> _translatedCache = new();
    TranslationSession? _session;
    CancellationTokenSource? _cts;
    McpServer? _mcp;
    int _page;
    bool _syncing;
    int _renderVersion;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.Title;

        SourceBox.ItemsSource = Languages.All;
        TargetBox.ItemsSource = Languages.Targets.ToList();
        ProviderBox.ItemsSource = TranslatorFactory.Providers;
        OcrBox.ItemsSource = new[]
        {
            new KeyValuePair<OcrMode, string>(OcrMode.Auto, "Images + scanned pages"),
            new KeyValuePair<OcrMode, string>(OcrMode.FullPage, "Whole page (vector text too)"),
            new KeyValuePair<OcrMode, string>(OcrMode.Off, "Off"),
        };
        OcrBox.DisplayMemberPath = "Value";
        OcrBox.SelectedValuePath = "Key";
        AlignBox.ItemsSource = Enum.GetValues<TextAlign>();

        SourceBox.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == _settings.SourceLanguage) ?? Languages.All[0];
        TargetBox.SelectedItem = Languages.Targets.FirstOrDefault(l => l.Code == _settings.TargetLanguage) ?? Languages.Targets.First();
        ProviderBox.SelectedItem = TranslatorFactory.Providers.FirstOrDefault(p => p.Id == _settings.Provider) ?? TranslatorFactory.Providers[0];
        OcrBox.SelectedValue = _settings.Ocr;
        OutlinesBox.IsChecked = _settings.ShowBlockOutlines;

        Loaded += (_, _) => StartMcp();
        Closing += (_, _) => { SaveChoices(); _cts?.Cancel(); _mcp?.Dispose(); LocalLibre.Stop(); };
    }

    // ---------------------------------------------------------------- file

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Open PDF" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) is { } pdf)
            OpenFile(pdf);
    }

    public bool OpenFile(string path)
    {
        if (_cts != null) { SetStatus("Cancel the running translation first."); return false; }
        try
        {
            var session = new TranslationSession(path);
            _session?.Dispose();
            _session = session;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the PDF:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _originalCache.Clear();
        _translatedCache.Clear();
        _undo.Clear();
        _redo.Clear();
        Title = $"{AppInfo.Title} — {System.IO.Path.GetFileName(path)}";
        PageCountText.Text = $"/ {_session.PageCount}";
        Progress.Value = 0;
        SetBusy(false);
        SetStatus($"Opened {System.IO.Path.GetFileName(path)}: {_session.PageCount} page(s).");
        ShowPage(0);
        Dispatcher.InvokeAsync(FitWidth, System.Windows.Threading.DispatcherPriority.Loaded);
        return true;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        int done = _session.Pages.Count(p => p?.Translated == true);
        if (done < _session.PageCount &&
            MessageBox.Show(this, $"{done} of {_session.PageCount} pages are translated. Untranslated pages are saved unchanged. Continue?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var dlg = new SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = DefaultOutputName(),
            InitialDirectory = System.IO.Path.GetDirectoryName(_session.FilePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _session.Save(dlg.FileName);
            SetStatus($"Saved {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Saving failed:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    string DefaultOutputName() =>
        System.IO.Path.GetFileNameWithoutExtension(_session!.FilePath) + $".{(TargetBox.SelectedItem as Language)?.Code ?? "xx"}.pdf";

    // ---------------------------------------------------------------- translation

    TranslateOptions CurrentOptions() => new(
        (SourceBox.SelectedItem as Language)?.Code ?? Languages.Auto,
        (TargetBox.SelectedItem as Language)?.Code ?? "en",
        OcrBox.SelectedValue is OcrMode m ? m : OcrMode.Auto);

    /// <summary>The selected translator; null for "extract only". Throws with a readable message when a key is missing.</summary>
    ITranslator? CreateTranslator(string? providerId = null) =>
        TranslatorFactory.Create(providerId ?? ((ProviderInfo)ProviderBox.SelectedItem).Id, _settings);

    bool TryCreateTranslator(out ITranslator? translator)
    {
        try
        {
            translator = CreateTranslator();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            translator = null;
            return false;
        }
    }

    /// <summary>Errors with a fix the app can start (Claude Code sign-in) get buttons; the rest a message box.</summary>
    void ShowError(Exception ex)
    {
        switch (ex)
        {
            case ClaudeCodeSetupException setup: ShowClaudeCodeSetup(setup); break;
            case MissingKeyException missing: ShowMissingKey(missing.Help); break;
            case LocalLibreMissingException: ShowOfflineInstall(); break;
            default: ShowProblem("Something went wrong", ex.Message); break;
        }
    }

    void OpenSettingsSoon() => Dispatcher.InvokeAsync(() => Settings_Click(this, new RoutedEventArgs()));

    void UseProvider(string id)
    {
        ProviderBox.SelectedItem = TranslatorFactory.Providers.FirstOrDefault(p => p.Id == id) ?? ProviderBox.SelectedItem;
        SaveChoices();
        SetStatus($"Service: {((ProviderInfo)ProviderBox.SelectedItem).Name}. Press Translate again.");
    }

    /// <summary>A service needs a key: say how to get one, with the page, Settings and a free alternative a click away.</summary>
    internal void ShowMissingKey(KeyHelp help)
    {
        var actions = new List<ActionDialog.Action>();
        if (help.Url.Length > 0) actions.Add(new(help.LinkLabel, () => Launch.Url(help.Url), ToolTip: help.Url));
        actions.Add(new("Open Settings", OpenSettingsSoon, CloseAfter: true));
        actions.Add(new("Use free MyMemory instead", () => UseProvider("mymemory"), CloseAfter: true));
        ActionDialog.Show(this, $"{help.Service} needs {help.What}", help.Steps, actions.ToArray());
    }

    /// <summary>Offline LibreTranslate chosen but not installed.</summary>
    internal void ShowOfflineInstall()
    {
        ActionDialog.Show(this, "Install offline translation",
            "“LibreTranslate offline” translates on this PC: free, private, and no internet needed after setup.\n\n" +
            "Install downloads LibreTranslate (" + LocalLibre.ProjectUrl + ") with its own private Python into " +
            LocalLibre.Folder + " — about 1 GB, a few minutes. A window shows the progress; when it says Done, press Translate.\n" +
            "Each new language pair downloads its model (~100 MB) the first time it is used.",
            new ActionDialog.Action("Install", LocalLibre.StartInstall, ToolTip: "Opens a window running the install script"),
            new ActionDialog.Action("Project page", () => Launch.Url(LocalLibre.ProjectUrl)),
            new ActionDialog.Action("Use free MyMemory instead", () => UseProvider("mymemory"), CloseAfter: true));
    }

    /// <summary>Any other problem: the message (links clickable) and a way to Settings.</summary>
    void ShowProblem(string title, string message) =>
        ActionDialog.Show(this, title, message, new ActionDialog.Action("Open Settings", OpenSettingsSoon, CloseAfter: true));

    internal void ShowClaudeCodeSetup(ClaudeCodeSetupException ex)
    {
        if (ex.Exe == null)
        {
            ActionDialog.Show(this, "Claude Code needed",
                "Translating with your Claude account uses Claude Code, which was not found on this PC.\n\n" +
                "Install it, or if it is installed somewhere unusual, set its path in Settings.",
                new ActionDialog.Action("Download Claude Code", () => Launch.Url("https://claude.com/claude-code")),
                new ActionDialog.Action("Open Settings", () => Dispatcher.InvokeAsync(() => Settings_Click(this, new RoutedEventArgs())), CloseAfter: true));
            return;
        }
        ActionDialog.Show(this, "Sign in Claude Code",
            ex.Message + "\n\n" +
            "1.  Click “Get sign-in token”. A terminal window opens and runs Claude Code's setup-token; sign in with your " +
            "Claude account in the browser page it opens.\n" +
            "2.  Copy the token it prints in the terminal.\n" +
            "3.  Click “Open Settings”, paste it into “Claude Code token” and Save. Then translate again.\n\n" +
            "No API key is needed. Claude Code: " + ex.Exe,
            new ActionDialog.Action("Get sign-in token", () => Launch.Terminal(ex.Exe, "setup-token"),
                ToolTip: "Opens a terminal running: claude setup-token"),
            new ActionDialog.Action("Open Settings", () => Dispatcher.InvokeAsync(() => Settings_Click(this, new RoutedEventArgs())), CloseAfter: true));
    }

    async void TranslatePage_Click(object sender, RoutedEventArgs e) => await RunAsync(new[] { _page });

    async void TranslateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        // Current page first so the preview fills in immediately, then the rest in order.
        var order = new[] { _page }.Concat(Enumerable.Range(0, _session.PageCount).Where(i => i != _page)).ToList();
        await RunAsync(order);
    }

    async Task RunAsync(IReadOnlyList<int> pages)
    {
        if (_session == null || _cts != null) return;
        if (!TryCreateTranslator(out var translator)) return;
        var opt = CurrentOptions();
        if (opt.Source == opt.Target) { MessageBox.Show(this, "Source and target language are the same.", Title); return; }
        if (translator is LocalLibreTranslator)
        {
            // A restart mid-run (a newly detected language) reports here too.
            LocalLibre.Reporter ??= new Progress<string>(SetStatus);
            // Start (and on first use, download models for) the local server here, so progress shows in the status bar.
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try { await LocalLibre.EnsureRunningAsync(opt.Source, opt.Target, new Progress<string>(SetStatus), _cts.Token); }
            catch (OperationCanceledException) { SetStatus("Cancelled."); return; }
            catch (Exception ex) { ShowProblem("Offline LibreTranslate could not start", ex.Message); return; }
            finally { _cts.Dispose(); _cts = null; SetBusy(false); }
        }
        if (translator == null)
            SetStatus("“Extract only” finds blocks without translating. To translate with Claude, pick “Claude (Claude Code sign-in)”, " +
                      "or ask Claude in Claude Code to translate the open PDF (MCP).");
        SaveChoices();

        _translatedCache.Clear();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        Progress.Maximum = pages.Count;
        Progress.Value = 0;
        var log = new Progress<string>(SetStatus);
        var errors = new List<string>();
        var notes = new List<string>();
        Exception? setupProblem = null;
        try
        {
            foreach (var index in pages)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var result = await _session.TranslatePageAsync(index, opt, translator, log, _cts.Token);
                    _translatedCache.Remove(index);
                    if (index == _page) ShowPage(_page);
                    if (result.Note != null) notes.Add($"Page {index + 1}: {result.Note}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is ClaudeCodeSetupException or MissingKeyException or LocalLibreMissingException)
                {
                    // Every page would fail the same way; stop and offer the fix.
                    setupProblem = ex;
                    errors.Add(ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add($"Page {index + 1}: {ex.Message}");
                    if (errors.Count >= 3)
                    {
                        errors.Add("Stopped after repeated errors.");
                        break;
                    }
                }
                Progress.Value++;
            }
            string what = translator == null ? "Blocks extracted (no translation service)" : $"Translated to {Languages.NameOf(opt.Target)} with {translator.DisplayName}";
            SetStatus(errors.Count == 0
                ? $"{what}: {pages.Count} page(s)." + (notes.Count > 0 ? $"  ⚠ {notes[0]}" : "")
                : $"Finished with {errors.Count} error(s). {errors[0]}");
            if (setupProblem != null) ShowError(setupProblem);
            else if (errors.Count > 0)
                ShowProblem(errors.Count == 1 ? "Translation stopped" : $"{errors.Count} problems while translating",
                    string.Join("\n", errors.Concat(notes).Take(12)));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void SetBusy(bool busy)
    {
        bool open = _session != null;
        TranslatePageButton.IsEnabled = open && !busy;
        TranslateAllButton.IsEnabled = open && !busy;
        SaveButton.IsEnabled = open && !busy;
        CancelButton.IsEnabled = busy;
        SourceBox.IsEnabled = TargetBox.IsEnabled = ProviderBox.IsEnabled = OcrBox.IsEnabled = !busy;
        UpdateEditBar();
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ---------------------------------------------------------------- preview

    PageResult? CurrentPage => _session?.Pages[_page];

    void ShowPage(int index)
    {
        if (_session == null) return;
        CancelInlineEdit();
        _page = Math.Clamp(index, 0, _session.PageCount - 1);
        PageBox.Text = (_page + 1).ToString();

        if (!_originalCache.TryGetValue(_page, out var original))
        {
            try
            {
                original = ToBitmap(PageRenderer.Render(_session.Bytes, _page, PreviewDpi, _session.PageWidth(_page)));
                _originalCache[_page] = original;
            }
            catch (Exception ex)
            {
                SetStatus($"Could not render page {_page + 1}: {ex.Message}");
                return;
            }
        }
        LeftImage.Source = original;
        RefreshBlockList();
        _ = RenderTranslatedAsync(_page);
    }

    void RefreshBlockList()
    {
        var result = CurrentPage;
        var selectedIds = Selected.Select(r => r.Id).ToHashSet();
        BlocksGrid.ItemsSource = null;
        BlocksGrid.ItemsSource = result?.Regions;
        if (result != null)
            foreach (var r in result.Regions.Where(r => selectedIds.Contains(r.Id))) BlocksGrid.SelectedItems.Add(r);
        BlocksHeader.Text = result == null
            ? $"Page {_page + 1}: not analysed yet"
            : $"Page {_page + 1}: {result.Regions.Count} block(s)" + (result.Note != null ? $"  ⚠ {result.Note}" : "");
        RedrawOverlays();
        UpdateEditBar();
    }

    async Task RenderTranslatedAsync(int index)
    {
        if (_session == null) return;
        int version = ++_renderVersion;
        var result = _session.Pages[index];
        bool hasDeletions = _session.DeletedImages.GetValueOrDefault(index)?.Count > 0;
        if (result?.Translated != true && !hasDeletions)
        {
            RightImage.Source = null;
            RightPlaceholder.Visibility = Visibility.Visible;
            RightHeader.Text = "Translated";
            RedrawOverlays();
            return;
        }
        RightPlaceholder.Visibility = Visibility.Collapsed;
        RightHeader.Text = result?.Translated == true ? $"Translated — {Languages.NameOf(_session.TargetLanguage)}" : "Output (images removed)";
        if (!_translatedCache.TryGetValue(index, out var bmp))
        {
            var session = _session;
            try
            {
                double width = session.PageWidth(index);
                var raster = await Task.Run(() => PageRenderer.Render(session.ComposePage(index), 0, PreviewDpi, width));
                bmp = ToBitmap(raster);
            }
            catch (Exception ex)
            {
                SetStatus($"Preview failed: {ex.Message}");
                return;
            }
            if (version != _renderVersion) return; // a newer edit superseded this render
            _translatedCache[index] = bmp;
        }
        if (version == _renderVersion && index == _page)
        {
            RightImage.Source = bmp;
            RedrawOverlays();
        }
    }

    static BitmapSource ToBitmap(RasterPage r)
    {
        var bmp = BitmapSource.Create(r.Width, r.Height, PreviewDpi, PreviewDpi, PixelFormats.Bgra32, null, r.Bgra, r.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    void Prev_Click(object sender, RoutedEventArgs e) => ShowPage(_page - 1);
    void Next_Click(object sender, RoutedEventArgs e) => ShowPage(_page + 1);

    void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && int.TryParse(PageBox.Text, out int n)) ShowPage(n - 1);
    }

    void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LeftScale == null) return;
        LeftScale.ScaleX = LeftScale.ScaleY = RightScale.ScaleX = RightScale.ScaleY = e.NewValue;
        ZoomText.Text = $"{e.NewValue * 100:0}%";
    }

    /// <summary>Ctrl + wheel zooms both previews, keeping the point under the cursor in place.</summary>
    void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        var sv = (ScrollViewer)sender;
        var host = sv == LeftScroll ? LeftHost : RightHost;
        double old = ZoomSlider.Value;
        double zoom = Math.Clamp(old * (e.Delta > 0 ? 1.15 : 1 / 1.15), ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(zoom - old) < 1e-6) return;

        // Page coordinate (unscaled DIPs) under the cursor before zooming.
        var onPage = e.GetPosition(host);
        var inViewer = e.GetPosition(sv);
        ZoomSlider.Value = zoom;
        sv.UpdateLayout();
        // Where that point is now, relative to the viewport; scroll so it is back under the cursor.
        var moved = host.TranslatePoint(onPage, sv);
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + moved.X - inViewer.X);
        sv.ScrollToVerticalOffset(sv.VerticalOffset + moved.Y - inViewer.Y);
    }

    void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();

    void FitWidth()
    {
        if (LeftImage.Source is not BitmapSource bmp) return;
        double avail = LeftScroll.ViewportWidth - 40;
        if (avail > 50) ZoomSlider.Value = Math.Clamp(avail / bmp.Width, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    void LeftScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => Sync(LeftScroll, RightScroll);
    void RightScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => Sync(RightScroll, LeftScroll);

    void Sync(ScrollViewer from, ScrollViewer to)
    {
        if (_syncing || SyncScrollBox?.IsChecked != true) return;
        _syncing = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _syncing = false;
    }

    void OutlinesBox_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowBlockOutlines = OutlinesBox.IsChecked == true;
        RedrawOverlays();
    }

    // ---------------------------------------------------------------- settings

    void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is ProviderInfo p) ProviderBox.ToolTip = p.Requirement;
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();
        StartMcp();
    }

    void SaveChoices()
    {
        _settings.SourceLanguage = (SourceBox.SelectedItem as Language)?.Code ?? Languages.Auto;
        _settings.TargetLanguage = (TargetBox.SelectedItem as Language)?.Code ?? "en";
        _settings.Provider = (ProviderBox.SelectedItem as ProviderInfo)?.Id ?? "mymemory";
        _settings.Ocr = OcrBox.SelectedValue is OcrMode m ? m : OcrMode.Auto;
        try { _settings.Save(); } catch { /* settings are a convenience */ }
    }
}

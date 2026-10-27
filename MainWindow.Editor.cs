using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfTranslator.Core;
using PdfTranslator.Translators;

namespace PdfTranslator;

/// <summary>Direct manipulation of blocks on the preview: select, move, resize, edit, merge, split, undo.</summary>
public partial class MainWindow
{
    const double K = 96.0 / 72.0; // points -> DIPs on the preview canvases
    const double HandlePt = 6;

    enum DragMode { None, Move, Resize }

    readonly Dictionary<int, Stack<string>> _undo = new();
    readonly Dictionary<int, Stack<string>> _redo = new();
    DragMode _drag;
    Point _dragStart;         // points
    Vector _dragDelta;        // points
    bool _updatingBar;
    TextBox? _inlineEditor;
    TextRegion? _inlineRegion;

    List<TextRegion> Selected => BlocksGrid.SelectedItems.OfType<TextRegion>().ToList();

    // ---------------------------------------------------------------- overlays

    void RedrawOverlays()
    {
        LeftOverlay.Children.Clear();
        foreach (var child in RightOverlay.Children.OfType<UIElement>().Where(c => c != _inlineEditor).ToList())
            RightOverlay.Children.Remove(child);
        if (ImageModeButton.IsChecked == true) { DrawImageOverlays(); return; }
        var page = CurrentPage;
        if (page == null) return;

        var selected = Selected.ToHashSet();
        bool outlines = OutlinesBox.IsChecked == true;
        bool translatedShown = RightImage.Source != null;
        foreach (var r in page.Regions)
        {
            bool sel = selected.Contains(r);
            if (!sel && !outlines) continue;
            AddRect(LeftOverlay, r.Bounds, sel, r.KeepOriginal);
            if (!translatedShown) continue;

            var box = r.DrawBounds;
            if (sel && _drag == DragMode.Move) box = box with { X = box.X + _dragDelta.X, Y = box.Y + _dragDelta.Y };
            if (sel && _drag == DragMode.Resize)
                box = box with { W = Math.Max(8, box.W + _dragDelta.X), H = Math.Max(6, box.H + _dragDelta.Y) };
            AddRect(RightOverlay, box, sel, r.KeepOriginal);
            if (sel && selected.Count == 1)
            {
                var handle = new Rectangle { Width = HandlePt * K, Height = HandlePt * K, Fill = Brushes.DodgerBlue, Stroke = Brushes.White };
                Canvas.SetLeft(handle, (box.Right - HandlePt / 2) * K);
                Canvas.SetTop(handle, (box.Bottom - HandlePt / 2) * K);
                RightOverlay.Children.Add(handle);
            }
        }
    }

    static void AddRect(Canvas canvas, RectD b, bool selected, bool kept)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(2, b.W * K + 4),
            Height = Math.Max(2, b.H * K + 4),
            Stroke = selected ? Brushes.DodgerBlue : kept ? Brushes.Gray : new SolidColorBrush(Color.FromArgb(110, 255, 140, 0)),
            StrokeThickness = selected ? 2 : 1,
            StrokeDashArray = selected ? null : new DoubleCollection { 3, 2 },
            Fill = selected ? new SolidColorBrush(Color.FromArgb(35, 30, 144, 255)) : Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, b.X * K - 2);
        Canvas.SetTop(rect, b.Y * K - 2);
        canvas.Children.Add(rect);
    }

    // ---------------------------------------------------------------- selection

    TextRegion? HitTest(Point pt, bool drawBounds) =>
        CurrentPage?.Regions
            .Where(r => (drawBounds ? r.DrawBounds : r.Bounds).Inflate(1.5).Contains(pt.X, pt.Y))
            .OrderBy(r => (drawBounds ? r.DrawBounds : r.Bounds).Area)
            .FirstOrDefault();

    void Select(TextRegion? region, bool toggle)
    {
        if (!toggle) BlocksGrid.SelectedItems.Clear();
        if (region == null) return;
        if (toggle && BlocksGrid.SelectedItems.Contains(region)) BlocksGrid.SelectedItems.Remove(region);
        else BlocksGrid.SelectedItems.Add(region);
        BlocksGrid.ScrollIntoView(region);
    }

    void BlocksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RedrawOverlays();
        UpdateEditBar();
    }

    void UpdateEditBar()
    {
        var sel = Selected;
        EditBar.IsEnabled = sel.Count > 0 && _cts == null && CurrentPage != null;
        if (sel.Count == 0) return;
        _updatingBar = true;
        var first = sel[0];
        BoldButton.IsChecked = first.Bold;
        ItalicButton.IsChecked = first.Italic;
        KeepButton.IsChecked = first.KeepOriginal;
        SizeBox.Text = first.FontSizeOverride is { } s ? s.ToString("0.#") : "";
        SizeBox.ToolTip = $"Font size in points (source {first.FontSize:0.#}); empty = automatic";
        AlignBox.SelectedItem = first.Align;
        _updatingBar = false;
    }

    Point? _drawStart;
    Rectangle? _drawRect;

    void LeftOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pt = ToPoints(e.GetPosition(LeftOverlay));
        if (ImageModeButton.IsChecked == true) { SelectImageAt(pt); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && CurrentPage != null && _cts == null)
        {
            // Shift+drag: draw a new block around text the detector missed.
            _drawStart = pt;
            _drawRect = new Rectangle { Stroke = Brushes.SeaGreen, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 }, IsHitTestVisible = false };
            LeftOverlay.Children.Add(_drawRect);
            LeftOverlay.CaptureMouse();
            UpdateDrawRect(pt);
            return;
        }
        Select(HitTest(pt, false), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    void LeftOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drawStart != null) UpdateDrawRect(ToPoints(e.GetPosition(LeftOverlay)));
    }

    void UpdateDrawRect(Point pt)
    {
        if (_drawStart is not { } a || _drawRect == null) return;
        Canvas.SetLeft(_drawRect, Math.Min(a.X, pt.X) * K);
        Canvas.SetTop(_drawRect, Math.Min(a.Y, pt.Y) * K);
        _drawRect.Width = Math.Abs(pt.X - a.X) * K;
        _drawRect.Height = Math.Abs(pt.Y - a.Y) * K;
    }

    async void LeftOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawStart is not { } a) return;
        var b = ToPoints(e.GetPosition(LeftOverlay));
        _drawStart = null;
        LeftOverlay.ReleaseMouseCapture();
        if (_drawRect != null) LeftOverlay.Children.Remove(_drawRect);
        _drawRect = null;
        var box = new RectD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (box.W < 4 || box.H < 3) return;
        await AddBlockAsync(box);
    }

    /// <summary>Adds a block for a drawn box: OCR the box, then translate it (or let the user type it).</summary>
    async Task AddBlockAsync(RectD box)
    {
        var page = CurrentPage;
        if (_session == null || page == null || _cts != null) return;
        PushUndo(page);
        _cts = new CancellationTokenSource();
        SetBusy(true);
        TextRegion region;
        try
        {
            SetStatus("Reading the new block…");
            region = await _session.AddRegionAsync(_page, box, null, null, null, CurrentOptions().Source, new Progress<string>(SetStatus), _cts.Token);
        }
        catch (Exception ex)
        {
            SetStatus("Could not add the block: " + ex.Message);
            return;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
        BlocksGrid.Items.Refresh();
        Select(region, false);
        AfterEdit();
        if (region.Source.Length > 0 && ((ProviderInfo)ProviderBox.SelectedItem).Id != "none")
        {
            SetStatus($"Read \"{region.Source}\"; translating…");
            await TranslateRegionsAsync(new[] { region });
        }
        if (string.IsNullOrWhiteSpace(region.Translated))
        {
            SetStatus(region.Source.Length > 0 ? $"Read \"{region.Source}\". Type the translation." : "No text recognised in the box. Type the translation.");
            await RenderTranslatedAsync(_page);
            BeginInlineEdit(region);
        }
    }

    void DeleteBlock_Click(object sender, RoutedEventArgs e)
    {
        var page = CurrentPage;
        if (page == null) return;
        Edit(sel => sel.ForEach(r => page.Regions.Remove(r)));
        BlocksGrid.SelectedItems.Clear();
    }

    void RightOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ImageModeButton.IsChecked == true) { SelectImageAt(ToPoints(e.GetPosition(RightOverlay))); return; }
        if (CurrentPage == null || RightImage.Source == null) return;
        CommitInlineEdit();
        RightOverlay.Focus();
        var pt = ToPoints(e.GetPosition(RightOverlay));
        var sel = Selected;

        if (e.ClickCount == 2)
        {
            if (HitTest(pt, true) is { } hit) { Select(hit, false); BeginInlineEdit(hit); }
            return;
        }
        if (sel.Count == 1)
        {
            var b = sel[0].DrawBounds;
            if (Math.Abs(pt.X - b.Right) <= HandlePt && Math.Abs(pt.Y - b.Bottom) <= HandlePt)
            {
                StartDrag(DragMode.Resize, pt);
                return;
            }
        }
        var region = HitTest(pt, true);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (region == null) { if (!ctrl) Select(null, false); return; }
        if (ctrl) { Select(region, true); return; }
        if (!sel.Contains(region)) Select(region, false);
        StartDrag(DragMode.Move, pt);
    }

    void StartDrag(DragMode mode, Point pt)
    {
        _drag = mode;
        _dragStart = pt;
        _dragDelta = default;
        RightOverlay.CaptureMouse();
    }

    void RightOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None) return;
        _dragDelta = ToPoints(e.GetPosition(RightOverlay)) - _dragStart;
        RedrawOverlays();
    }

    void RightOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var mode = _drag;
        var delta = _dragDelta;
        _drag = DragMode.None;
        RightOverlay.ReleaseMouseCapture();
        if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) { RedrawOverlays(); return; }

        Edit(sel =>
        {
            foreach (var r in sel)
            {
                var b = r.DrawBounds;
                r.OutBounds = mode == DragMode.Move
                    ? b with { X = b.X + delta.X, Y = b.Y + delta.Y }
                    : b with { W = Math.Max(8, b.W + delta.X), H = Math.Max(6, b.H + delta.Y) };
            }
        });
    }

    static Point ToPoints(Point dip) => new(dip.X / K, dip.Y / K);

    // ---------------------------------------------------------------- edit plumbing

    /// <summary>Applies a change to the selection with undo, then refreshes the list and the translated preview.</summary>
    void Edit(Action<List<TextRegion>> change, bool needsSelection = true)
    {
        var page = CurrentPage;
        var sel = Selected;
        if (page == null || (needsSelection && sel.Count == 0)) return;
        PushUndo(page);
        change(sel);
        AfterEdit();
    }

    void AfterEdit()
    {
        var page = CurrentPage;
        if (page != null) page.Translated = true;
        _translatedCache.Remove(_page);
        BlocksGrid.Items.Refresh();
        RedrawOverlays();
        UpdateEditBar();
        _ = RenderTranslatedAsync(_page);
    }

    static readonly JsonSerializerOptions UndoJson = new() { IncludeFields = false };

    sealed record UndoState(List<TextRegion>? Regions, List<DeletedImage> Deleted);

    void PushUndo(PageResult page) => PushUndo(page.Index);

    void PushUndo(int index)
    {
        Stack(_undo, index).Push(SnapshotPage(index));
        Stack(_redo, index).Clear();
    }

    string SnapshotPage(int index) => JsonSerializer.Serialize(new UndoState(
        _session?.Pages[index]?.Regions,
        _session != null && _session.DeletedImages.TryGetValue(index, out var d) ? d.ToList() : new()), UndoJson);

    static Stack<string> Stack(Dictionary<int, Stack<string>> d, int page) =>
        d.TryGetValue(page, out var s) ? s : d[page] = new Stack<string>();

    void Undo_Click(object sender, RoutedEventArgs e) => UndoRedo(_undo, _redo);
    void Redo_Click(object sender, RoutedEventArgs e) => UndoRedo(_redo, _undo);

    void UndoRedo(Dictionary<int, Stack<string>> from, Dictionary<int, Stack<string>> to)
    {
        if (_session == null || !from.TryGetValue(_page, out var stack) || stack.Count == 0) { SetStatus("Nothing to undo/redo."); return; }
        CancelInlineEdit();
        Stack(to, _page).Push(SnapshotPage(_page));
        var state = JsonSerializer.Deserialize<UndoState>(stack.Pop(), UndoJson)!;
        if (CurrentPage is { } page && state.Regions != null)
        {
            page.Regions.Clear();
            page.Regions.AddRange(state.Regions);
        }
        _session.DeletedImages[_page] = state.Deleted;
        RefreshBlockList();
        AfterEdit();
    }

    // ---------------------------------------------------------------- commands

    void Bold_Click(object sender, RoutedEventArgs e)
    {
        bool on = BoldButton.IsChecked == true;
        Edit(sel => sel.ForEach(r => { r.Bold = on; r.BoldLead = false; }));
    }

    void Italic_Click(object sender, RoutedEventArgs e)
    {
        bool on = ItalicButton.IsChecked == true;
        Edit(sel => sel.ForEach(r => r.Italic = on));
    }

    void Keep_Click(object sender, RoutedEventArgs e)
    {
        bool on = KeepButton.IsChecked == true;
        Edit(sel => sel.ForEach(r => r.KeepOriginal = on));
    }

    void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (string.IsNullOrWhiteSpace(SizeBox.Text)) { SizeAuto_Click(sender, e); return; }
        if (double.TryParse(SizeBox.Text, out double v) && v is >= 2 and <= 200)
            Edit(sel => sel.ForEach(r => r.FontSizeOverride = v));
    }

    void SizeUp_Click(object sender, RoutedEventArgs e) => StepSize(1.1);
    void SizeDown_Click(object sender, RoutedEventArgs e) => StepSize(1 / 1.1);

    void StepSize(double factor) =>
        Edit(sel => sel.ForEach(r => r.FontSizeOverride = Math.Round(Math.Clamp((r.FontSizeOverride ?? r.FontSize) * factor, 2, 200), 1)));

    void SizeAuto_Click(object sender, RoutedEventArgs e) => Edit(sel => sel.ForEach(r => r.FontSizeOverride = null));

    void AlignBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingBar || AlignBox.SelectedItem is not TextAlign a) return;
        Edit(sel => sel.ForEach(r => r.Align = a));
    }

    void ResetLayout_Click(object sender, RoutedEventArgs e) =>
        Edit(sel => sel.ForEach(r => { r.OutBounds = null; r.FontSizeOverride = null; }));

    void Merge_Click(object sender, RoutedEventArgs e)
    {
        var page = CurrentPage;
        if (page == null || Selected.Count < 2) { SetStatus("Select two or more blocks to merge (Ctrl+click)."); return; }
        PushUndo(page);
        var merged = MergeRegions(page, Selected, (TargetBox.SelectedItem as Language)?.Code ?? "en");
        BlocksGrid.Items.Refresh();
        Select(merged, false);
        AfterEdit();
    }

    /// <summary>Joins blocks in reading order into the first one; used by the editor and the MCP tools.</summary>
    internal static TextRegion MergeRegions(PageResult page, IReadOnlyList<TextRegion> regions, string target)
    {
        var ordered = regions.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).ToList();
        var first = ordered[0];
        string sep = Languages.IsCjk(target) ? "" : " ";
        first.Source = PdfAnalyzer.JoinLines(ordered.Select(r => r.Source));
        first.Translated = ordered.Any(r => r.Translated != null)
            ? string.Join(sep, ordered.Select(r => (r.Translated ?? r.Source).Trim()))
            : null;
        first.ManualText = ordered.Any(r => r.ManualText);
        first.Lines = ordered.SelectMany(r => r.Lines).ToList();
        first.LineCount = ordered.Sum(r => r.LineCount);
        first.Bounds = RectD.Union(ordered.Select(r => r.Bounds));
        first.OutBounds = ordered.Any(r => r.OutBounds != null) ? RectD.Union(ordered.Select(r => r.DrawBounds)) : null;
        first.MaxRight = ordered.Max(r => r.MaxRight);
        first.MaxBottom = ordered.Max(r => r.MaxBottom);
        first.MinTop = ordered.Min(r => r.MinTop > 0 ? r.MinTop : r.Bounds.Y);
        first.BoldLead = false;
        foreach (var r in ordered.Skip(1)) page.Regions.Remove(r);
        return first;
    }

    async void Split_Click(object sender, RoutedEventArgs e)
    {
        var page = CurrentPage;
        var sel = Selected;
        if (page == null || sel.Count != 1 || sel[0].Lines.Count < 2) { SetStatus("Select one block that has more than one line."); return; }
        PushUndo(page);
        var parts = SplitRegion(page, sel[0]);
        BlocksGrid.Items.Refresh();
        BlocksGrid.SelectedItems.Clear();
        foreach (var p in parts) BlocksGrid.SelectedItems.Add(p);
        AfterEdit();
        await TranslateRegionsAsync(parts);
    }

    /// <summary>Replaces a block with one block per source line (translations are cleared).</summary>
    internal static List<TextRegion> SplitRegion(PageResult page, TextRegion r)
    {
        int index = page.Regions.IndexOf(r);
        int id = page.NextId();
        var parts = r.Lines.Select((line, i) => new TextRegion
        {
            Id = i == 0 ? r.Id : id++,
            Bounds = line.Bounds,
            Source = line.Text,
            Lines = new() { line },
            LineCount = 1,
            FontSize = r.FontSize,
            Bold = r.Bold,
            Italic = r.Italic,
            BoldLead = i == 0 && r.BoldLead,
            Align = r.Align,
            FontFamilyHint = r.FontFamilyHint,
            TextColor = r.TextColor,
            Background = r.Background,
            Origin = r.Origin,
            MaxRight = r.MaxRight,
            MaxBottom = i == r.Lines.Count - 1 ? r.MaxBottom : line.Bounds.Bottom,
            MinTop = i == 0 ? r.MinTop : line.Bounds.Y,
        }).ToList();
        page.Regions.RemoveAt(index);
        page.Regions.InsertRange(index, parts);
        return parts;
    }

    async void Retranslate_Click(object sender, RoutedEventArgs e)
    {
        var page = CurrentPage;
        if (page == null || Selected.Count == 0) { SetStatus("Select one or more blocks first."); return; }
        PushUndo(page);
        await TranslateRegionsAsync(Selected);
    }

    async Task TranslateRegionsAsync(IReadOnlyList<TextRegion> regions)
    {
        var page = CurrentPage;
        if (_session == null || page == null || _cts != null) return;
        if (!TryCreateTranslator(out var translator)) return;
        if (translator == null) { SetStatus("No translation service selected; type the text or use Claude via MCP."); return; }
        if (translator is LocalLibreTranslator)
            try { await LocalLibre.EnsureRunningAsync(CurrentOptions().Source, CurrentOptions().Target, new Progress<string>(SetStatus), CancellationToken.None); }
            catch (Exception ex) { ShowProblem("Offline LibreTranslate could not start", ex.Message); return; }
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await _session.RetranslateAsync(page, regions, CurrentOptions(), translator, new Progress<string>(SetStatus), _cts.Token);
            SetStatus($"Translated {regions.Count} block(s).");
        }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
        AfterEdit();
    }

    async void Reanalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _cts != null) return;
        if (CurrentPage != null &&
            MessageBox.Show(this, "Detect the blocks on this page again? Edits on this page are discarded (Undo still works).",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (CurrentPage is { } page) PushUndo(page);
        _session.Forget(_page);
        await RunAsync(new[] { _page });
    }

    // ---------------------------------------------------------------- text editing

    void EditText_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } r) BeginInlineEdit(r);
    }

    void BeginInlineEdit(TextRegion r)
    {
        if (RightImage.Source == null) return;
        CancelInlineEdit();
        var b = r.DrawBounds;
        _inlineRegion = r;
        _inlineEditor = new TextBox
        {
            Text = r.Translated ?? r.Source,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontSize = Math.Clamp((r.FontSizeOverride ?? r.FontSize) * K, 9, 28),
            MinWidth = 160,
            Width = Math.Max(160, b.W * K + 8),
            MinHeight = Math.Max(28, b.H * K + 8),
            Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 235)),
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(2),
            ToolTip = "Ctrl+Enter or click outside to apply, Esc to cancel",
        };
        Canvas.SetLeft(_inlineEditor, b.X * K - 4);
        Canvas.SetTop(_inlineEditor, b.Y * K - 4);
        _inlineEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelInlineEdit(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitInlineEdit(); e.Handled = true; }
        };
        _inlineEditor.LostKeyboardFocus += (_, _) => Dispatcher.InvokeAsync(CommitInlineEdit);
        RightOverlay.Children.Add(_inlineEditor);
        _inlineEditor.Focus();
        _inlineEditor.SelectAll();
    }

    void CommitInlineEdit()
    {
        if (_inlineEditor == null || _inlineRegion == null) return;
        var (editor, region) = (_inlineEditor, _inlineRegion);
        _inlineEditor = null;
        _inlineRegion = null;
        RightOverlay.Children.Remove(editor);
        string text = editor.Text.Trim();
        if (text == (region.Translated ?? region.Source).Trim()) return;
        var page = CurrentPage;
        if (page == null || !page.Regions.Contains(region)) return;
        PushUndo(page);
        region.Translated = text;
        region.ManualText = true;
        region.KeepOriginal = false;
        AfterEdit();
    }

    void CancelInlineEdit()
    {
        if (_inlineEditor != null) RightOverlay.Children.Remove(_inlineEditor);
        _inlineEditor = null;
        _inlineRegion = null;
    }

    void BlocksGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (CurrentPage is { } page) PushUndo(page);
    }

    void BlocksGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not TextRegion r) return;
        // The binding commits after this event; re-render once it has.
        Dispatcher.InvokeAsync(() =>
        {
            r.ManualText = true;
            AfterEdit();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- context menu & keys

    void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var canvas = (Canvas)sender;
        var pt = ToPoints(e.GetPosition(canvas));
        if (ImageModeButton.IsChecked == true) { ShowImageMenu(canvas, pt); e.Handled = true; return; }
        var hit = HitTest(pt, canvas == RightOverlay);
        if (hit != null && !Selected.Contains(hit)) Select(hit, false);
        if (Selected.Count == 0) return;

        var menu = new ContextMenu();
        void Item(string header, RoutedEventHandler handler, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += handler;
            menu.Items.Add(mi);
        }
        var sel = Selected;
        Item("Edit text…", EditText_Click);
        Item(sel[0].Bold ? "Not bold" : "Bold", (s, a) => { BoldButton.IsChecked = !sel[0].Bold; Bold_Click(s, a); });
        Item(sel[0].Italic ? "Not italic" : "Italic", (s, a) => { ItalicButton.IsChecked = !sel[0].Italic; Italic_Click(s, a); });
        Item("Larger", SizeUp_Click);
        Item("Smaller", SizeDown_Click);
        Item("Automatic size", SizeAuto_Click);
        menu.Items.Add(new Separator());
        foreach (var a in Enum.GetValues<TextAlign>())
            Item($"Align {a.ToString().ToLowerInvariant()}", (_, _) => Edit(s => s.ForEach(r => r.Align = a)));
        menu.Items.Add(new Separator());
        Item("Merge selected", Merge_Click, sel.Count >= 2);
        Item("Split into lines", Split_Click, sel.Count == 1 && sel[0].Lines.Count >= 2);
        Item("Re-translate", Retranslate_Click);
        Item(sel[0].KeepOriginal ? "Use translation" : "Keep original", (s, a) => { KeepButton.IsChecked = !sel[0].KeepOriginal; Keep_Click(s, a); });
        Item("Reset layout", ResetLayout_Click);
        Item("Delete block", DeleteBlock_Click);
        menu.PlacementTarget = canvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool typing = e.OriginalSource is TextBox || (e.OriginalSource is DependencyObject d && IsInside(d, BlocksGrid) && BlocksGrid.IsKeyboardFocusWithin && IsGridEditing());
        if (typing) return;

        if (ctrl && e.Key == Key.O) { Open_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.PageDown) { ShowPage(_page + 1); e.Handled = true; return; }
        if (e.Key == Key.PageUp) { ShowPage(_page - 1); e.Handled = true; return; }

        if (ImageModeButton.IsChecked == true && e.Key == Key.Delete && _selectedImages.Count > 0)
        {
            DeleteImage_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (Selected.Count == 0 || BlocksGrid.IsKeyboardFocusWithin && e.Key is Key.Up or Key.Down) return;
        switch (e.Key)
        {
            case Key.F2: EditText_Click(sender, e); break;
            case Key.Delete: KeepButton.IsChecked = !Selected[0].KeepOriginal; Keep_Click(sender, e); break;
            case Key.B when ctrl: BoldButton.IsChecked = !Selected[0].Bold; Bold_Click(sender, e); break;
            case Key.I when ctrl: ItalicButton.IsChecked = !Selected[0].Italic; Italic_Click(sender, e); break;
            case Key.M when ctrl: Merge_Click(sender, e); break;
            case Key.OemPlus or Key.Add when ctrl: SizeUp_Click(sender, e); break;
            case Key.OemMinus or Key.Subtract when ctrl: SizeDown_Click(sender, e); break;
            case Key.Left or Key.Right or Key.Up or Key.Down when RightOverlay.IsKeyboardFocused:
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                Edit(sel => sel.ForEach(r => r.OutBounds = r.DrawBounds with { X = r.DrawBounds.X + dx, Y = r.DrawBounds.Y + dy }));
                break;
            default: return;
        }
        e.Handled = true;
    }

    bool IsGridEditing() => BlocksGrid.CurrentCell.Column != null && Keyboard.FocusedElement is TextBox;

    static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    /// <summary>Used by the --snapshot command: pick options, translate one page, select a block to show the editor.</summary>
    internal async Task TranslateForSnapshotAsync(int page, string target, string provider)
    {
        TargetBox.SelectedItem = Languages.Targets.FirstOrDefault(l => l.Code == target) ?? TargetBox.SelectedItem;
        ProviderBox.SelectedItem = TranslatorFactory.Providers.FirstOrDefault(p => p.Id == provider) ?? ProviderBox.SelectedItem;
        ShowPage(page);
        await RunAsync(new[] { _page });
        if (BlocksGrid.Items.Count > 1) BlocksGrid.SelectedIndex = 1;
    }
}

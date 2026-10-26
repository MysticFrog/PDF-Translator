using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfTranslator.Core;

namespace PdfTranslator;

/// <summary>Image mode: select raster objects (stamps, watermarks, screenshots) and remove them from the output.</summary>
public partial class MainWindow
{
    readonly HashSet<int> _selectedImages = new();
    int _selectedImagesPage = -1;

    HashSet<int> SelectedImages
    {
        get
        {
            if (_selectedImagesPage != _page) { _selectedImages.Clear(); _selectedImagesPage = _page; }
            return _selectedImages;
        }
    }

    void ImageMode_Click(object sender, RoutedEventArgs e)
    {
        CancelInlineEdit();
        SelectedImages.Clear();
        if (ImageModeButton.IsChecked == true)
        {
            int n = _session?.ImagesOn(_page).Count ?? 0;
            SetStatus(n == 0 ? "No images, stamps or annotations on this page." : $"{n} object(s) on this page (images, stamps, watermarks, annotations): click to select, Ctrl+click for several, Del to delete.");
        }
        RedrawOverlays();
        UpdateImageButtons();
    }

    void UpdateImageButtons()
    {
        bool on = ImageModeButton.IsChecked == true && _session != null && _cts == null;
        var sel = SelectedImages;
        bool anyLive = sel.Any(id => !_session!.IsDeleted(_page, id));
        bool anyDeleted = sel.Any(id => _session!.IsDeleted(_page, id));
        DeleteImageButton.IsEnabled = on && anyLive;
        DeleteImageAllButton.IsEnabled = on && sel.Count == 1;
        RestoreImageButton.IsEnabled = on && anyDeleted;
    }

    void DrawImageOverlays()
    {
        if (_session == null) return;
        var sel = SelectedImages;
        bool translatedShown = RightImage.Source != null;
        foreach (var img in _session.ImagesOn(_page))
        {
            bool deleted = _session.IsDeleted(_page, img.Id);
            bool selected = sel.Contains(img.Id);
            foreach (var canvas in translatedShown ? new[] { LeftOverlay, RightOverlay } : new[] { LeftOverlay })
            {
                var b = img.Bounds;
                var rect = new Rectangle
                {
                    Width = Math.Max(2, b.W * K), Height = Math.Max(2, b.H * K),
                    Stroke = deleted ? Brushes.Red : img.Kind == PageObjectKind.Image ? Brushes.MediumPurple : Brushes.Teal,
                    StrokeThickness = selected ? 3 : 1.5,
                    StrokeDashArray = deleted ? new DoubleCollection { 4, 3 } : null,
                    Fill = selected ? new SolidColorBrush(Color.FromArgb(45, 147, 112, 219)) : Brushes.Transparent,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, b.X * K);
                Canvas.SetTop(rect, b.Y * K);
                canvas.Children.Add(rect);
                if (deleted && canvas == LeftOverlay)
                {
                    foreach (var (x1, y1, x2, y2) in new[] { (b.X, b.Y, b.Right, b.Bottom), (b.Right, b.Y, b.X, b.Bottom) })
                        canvas.Children.Add(new Line
                        {
                            X1 = x1 * K, Y1 = y1 * K, X2 = x2 * K, Y2 = y2 * K,
                            Stroke = new SolidColorBrush(Color.FromArgb(150, 220, 0, 0)), StrokeThickness = 1.5, IsHitTestVisible = false,
                        });
                }
            }
        }
    }

    PageImage? ImageAt(Point pt) =>
        _session?.ImagesOn(_page).Where(i => i.Bounds.Contains(pt.X, pt.Y)).OrderBy(i => i.Bounds.Area).FirstOrDefault();

    void SelectImageAt(Point pt)
    {
        var img = ImageAt(pt);
        var sel = SelectedImages;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) sel.Clear();
        if (img != null && !sel.Remove(img.Id)) sel.Add(img.Id);
        if (img != null)
            SetStatus($"#{img.Id} {img.Label} at ({img.Bounds.X:0}, {img.Bounds.Y:0})" +
                      (_session!.IsDeleted(_page, img.Id) ? " — deleted" : ""));
        RedrawOverlays();
        UpdateImageButtons();
    }

    void ShowImageMenu(Canvas canvas, Point pt)
    {
        if (ImageAt(pt) is { } img && !SelectedImages.Contains(img.Id)) SelectImageAt(pt);
        if (SelectedImages.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = canvas };
        void Item(string header, RoutedEventHandler handler, bool enabled)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += handler;
            menu.Items.Add(mi);
        }
        Item("Delete image", DeleteImage_Click, DeleteImageButton.IsEnabled);
        Item("Delete it on all pages", DeleteImageAll_Click, DeleteImageAllButton.IsEnabled);
        Item("Restore image", RestoreImage_Click, RestoreImageButton.IsEnabled);
        menu.IsOpen = true;
    }

    async void DeleteImage_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || SelectedImages.Count == 0) return;
        PushUndo(_page);
        await _session.DeleteImagesAsync(_page, SelectedImages.ToList());
        SetStatus($"Deleted {SelectedImages.Count} image(s). Ctrl+Z restores.");
        AfterImageEdit();
    }

    async void DeleteImageAll_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || SelectedImages.Count != 1) return;
        var img = _session.ImagesOn(_page).First(i => i.Id == SelectedImages.First());
        // Undo is per page: snapshot every page that has a matching image first.
        for (int p = 0; p < _session.PageCount; p++)
            if (_session.ImagesOn(p).Any(i => i.Kind == img.Kind && Math.Abs(i.Bounds.X - img.Bounds.X) < 2 && Math.Abs(i.Bounds.Y - img.Bounds.Y) < 2 &&
                                              Math.Abs(i.Bounds.W - img.Bounds.W) < 2 && Math.Abs(i.Bounds.H - img.Bounds.H) < 2))
                PushUndo(p);
        var pages = await _session.DeleteMatchingOnAllPagesAsync(img);
        foreach (var p in pages) _translatedCache.Remove(p);
        SetStatus($"Deleted the image on {pages.Count} page(s). Ctrl+Z restores it page by page.");
        AfterImageEdit();
    }

    void RestoreImage_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || SelectedImages.Count == 0) return;
        PushUndo(_page);
        _session.RestoreImages(_page, SelectedImages.ToList());
        SetStatus("Image restored.");
        AfterImageEdit();
    }

    /// <summary>Used by --snapshot --images: show image mode with the first object deleted and the second selected.</summary>
    internal async Task ImageModeForSnapshotAsync(int page)
    {
        ShowPage(page);
        ImageModeButton.IsChecked = true;
        ImageMode_Click(this, new RoutedEventArgs());
        var objects = _session!.ImagesOn(_page);
        if (objects.Count == 0) return;
        await _session.DeleteImagesAsync(_page, new[] { objects[0].Id });
        AfterImageEdit();
        if (objects.Count > 1) SelectedImages.Add(objects[1].Id);
        RedrawOverlays();
        UpdateImageButtons();
    }

    void AfterImageEdit()
    {
        _translatedCache.Remove(_page);
        RefreshBlockList();
        UpdateImageButtons();
        _ = RenderTranslatedAsync(_page);
    }
}

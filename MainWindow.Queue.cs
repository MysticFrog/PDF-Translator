using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PdfTranslator.Core;
using PdfTranslator.Translators;

namespace PdfTranslator;

public enum QueueState { Waiting, Translating, Saved, Failed, Cancelled }

/// <summary>A file waiting in, or done by, the queue.</summary>
public sealed class QueueItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);

    QueueState _state;
    string _detail = "Waiting";
    string? _output;

    public QueueState State { get => _state; set { _state = value; Changed(); Changed(nameof(Icon)); } }
    /// <summary>One line under the name: progress, where it was saved, or what went wrong.</summary>
    public string Detail { get => _detail; set { _detail = value; Changed(); } }
    public string? Output { get => _output; set { _output = value; Changed(); } }

    public string Icon => State switch
    {
        QueueState.Translating => "▶",
        QueueState.Saved => "✔",
        QueueState.Failed => "✖",
        QueueState.Cancelled => "■",
        _ => "…",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// The queue: several PDFs translated one after another with the current From / To / Service / OCR choices,
/// each saved automatically as "name.&lt;language&gt;.pdf" next to the original or in a chosen folder.
/// </summary>
public partial class MainWindow
{
    readonly ObservableCollection<QueueItem> _queue = new();
    bool _queueRunning;
    bool _queueStop;

    void InitQueue()
    {
        QueueList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => UpdateQueue();
        QueueFolderBox.IsChecked = _settings.QueueOutputFolder.Length > 0;
        UpdateQueue();
    }

    void AddToQueue(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_queue.Any(q => q.State == QueueState.Waiting && string.Equals(q.Path, p, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(p));
        QueuePanel.Visibility = Visibility.Visible;
        QueueToggle.IsChecked = true;
        SetStatus(_queueRunning
            ? "Added to the running queue."
            : $"{_queue.Count(q => q.State == QueueState.Waiting)} file(s) waiting. Choose From / To / Service, then Start queue.");
    }

    void UpdateQueue()
    {
        int waiting = _queue.Count(q => q.State == QueueState.Waiting);
        QueueToggle.Content = _queue.Count == 0 ? "Queue" : $"Queue ({waiting})";
        QueueStartButton.IsEnabled = !_queueRunning && _cts == null && waiting > 0;
        QueueStartButton.Content = _queueRunning ? "Running…" : "Start queue";
        QueueStopButton.IsEnabled = _queueRunning;
        QueueClearButton.IsEnabled = _queue.Any(q => q.State is QueueState.Saved or QueueState.Failed or QueueState.Cancelled);
        QueueRemoveButton.IsEnabled = QueueList.SelectedItems.Cast<QueueItem>().Any(q => q.State != QueueState.Translating);
        QueueFolderText.Text = _settings.QueueOutputFolder.Length > 0 ? _settings.QueueOutputFolder : "next to each original";
    }

    void QueueToggle_Click(object sender, RoutedEventArgs e) =>
        QueuePanel.Visibility = QueueToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    void QueueAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Add PDFs to the queue", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddToQueue(dlg.FileNames);
    }

    void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var q in QueueList.SelectedItems.Cast<QueueItem>().Where(q => q.State != QueueState.Translating).ToList())
            _queue.Remove(q);
    }

    void QueueClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var q in _queue.Where(q => q.State is QueueState.Saved or QueueState.Failed or QueueState.Cancelled).ToList())
            _queue.Remove(q);
    }

    void QueueList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateQueue();

    /// <summary>Double-click: a saved file opens in the PDF viewer; a waiting one opens here to look at or edit.</summary>
    void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueItem q) return;
        if (q.State == QueueState.Saved && q.Output != null && File.Exists(q.Output)) Launch.Url(q.Output);
        else if (!_queueRunning) OpenFile(q.Path);
    }

    /// <summary>Checked: ask for a folder for all translated files. Unchecked: save next to each original.</summary>
    void QueueFolder_Click(object sender, RoutedEventArgs e)
    {
        if (QueueFolderBox.IsChecked == true)
        {
            var dlg = new OpenFolderDialog { Title = "Save translated files to", InitialDirectory = _settings.QueueOutputFolder };
            if (dlg.ShowDialog(this) == true) _settings.QueueOutputFolder = dlg.FolderName;
            else QueueFolderBox.IsChecked = _settings.QueueOutputFolder.Length > 0;
        }
        else _settings.QueueOutputFolder = "";
        SaveChoices();
        UpdateQueue();
    }

    void QueueStop_Click(object sender, RoutedEventArgs e)
    {
        _queueStop = true;
        _cts?.Cancel();
    }

    async void QueueStart_Click(object sender, RoutedEventArgs e) => await RunQueueAsync();

    /// <summary>Translates every waiting file in turn and saves it. One file's failure doesn't stop the others.</summary>
    async Task RunQueueAsync()
    {
        if (_queueRunning || _cts != null) return;
        if (!TryCreateTranslator(out var translator)) return;
        var opt = CurrentOptions();
        if (opt.Source == opt.Target) { MessageBox.Show(this, "Source and target language are the same.", Title); return; }
        SaveChoices();

        _queueRunning = true;
        _queueStop = false;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        UpdateQueue();
        int saved = 0, failed = 0;
        Exception? setupProblem = null;
        try
        {
            if (!await StartLocalServiceAsync(translator, opt)) return;
            // Files added while it runs are picked up too.
            while (!_queueStop && _queue.FirstOrDefault(q => q.State == QueueState.Waiting) is { } item)
            {
                QueueList.ScrollIntoView(item);
                item.State = QueueState.Translating;
                item.Detail = "Opening…";
                try
                {
                    try { LoadFile(item.Path, quiet: true); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException("Couldn't open: " + ex.Message, ex); }
                    var pages = Enumerable.Range(0, _session!.PageCount).ToList();
                    var progress = new Progress<string>(s => { SetStatus($"{item.Name}: {s}"); item.Detail = s; });
                    var run = await TranslatePagesAsync(pages, opt, translator, _cts.Token, progress);
                    if (run.SetupProblem != null)
                    {
                        // Every file would fail the same way: put this one back and stop.
                        setupProblem = run.SetupProblem;
                        item.State = QueueState.Waiting;
                        item.Detail = "Waiting";
                        break;
                    }
                    // Stopped after repeated errors, or nothing translated: not worth saving.
                    if (run.Errors.Count >= Math.Min(3, pages.Count))
                        throw new InvalidOperationException(run.Errors[0]);

                    string output = OutputPathFor(item.Path, opt.Target);
                    _session.Save(output);
                    item.Output = output;
                    item.State = QueueState.Saved;
                    item.Detail = (run.Errors.Count > 0 ? $"Saved with {run.Errors.Count} page problem(s) as " : "Saved as ") + System.IO.Path.GetFileName(output);
                    saved++;
                }
                catch (OperationCanceledException)
                {
                    item.State = QueueState.Cancelled;
                    item.Detail = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    item.State = QueueState.Failed;
                    item.Detail = ex.Message;
                    failed++;
                }
                UpdateQueue();
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _queueRunning = false;
            SetBusy(false);
            UpdateQueue();
        }
        int left = _queue.Count(q => q.State == QueueState.Waiting);
        SetStatus($"Queue: {saved} saved" + (failed > 0 ? $", {failed} failed" : "") + (left > 0 ? $", {left} still waiting" : "") + ".");
        if (setupProblem != null) ShowError(setupProblem);
    }

    /// <summary>Command-line test (--queue): queue the files and run them with the given service and language.</summary>
    internal async Task RunQueueForTestAsync(IEnumerable<string> files, string provider, string target)
    {
        _keepSettings = true;   // a test run must not change the user's saved choices
        ProviderBox.SelectedItem = TranslatorFactory.Providers.FirstOrDefault(p => p.Id == provider) ?? ProviderBox.SelectedItem;
        TargetBox.SelectedItem = Languages.Targets.FirstOrDefault(l => l.Code == target) ?? TargetBox.SelectedItem;
        AddToQueue(files);
        await RunQueueAsync();
    }

    internal IEnumerable<string> QueueSummary() => _queue.Select(q => $"{q.State}: {q.Name} — {q.Detail}");

    /// <summary>"name.&lt;lang&gt;.pdf" in the chosen folder or beside the original; never over an existing file.</summary>
    string OutputPathFor(string input, string target)
    {
        string folder = _settings.QueueOutputFolder.Length > 0 && Directory.Exists(_settings.QueueOutputFolder)
            ? _settings.QueueOutputFolder
            : System.IO.Path.GetDirectoryName(input)!;
        string stem = System.IO.Path.GetFileNameWithoutExtension(input) + "." + target;
        string path = System.IO.Path.Combine(folder, stem + ".pdf");
        for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, $"{stem} ({n}).pdf");
        return path;
    }
}

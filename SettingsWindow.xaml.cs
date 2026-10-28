using System.Windows;
using PdfTranslator.Core;
using PdfTranslator.Translators;

namespace PdfTranslator;

public partial class SettingsWindow : Window
{
    readonly AppSettings _s;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        DeepLKey.Password = _s.DeepLKey;
        GoogleKey.Password = _s.GoogleCloudKey;
        AzureKey.Password = _s.AzureKey;
        AzureRegion.Text = _s.AzureRegion;
        LibreUrl.Text = _s.LibreUrl;
        LibreKey.Password = _s.LibreKey;
        MyMemoryEmail.Text = _s.MyMemoryEmail;
        AnthropicKey.Password = _s.AnthropicKey;
        AnthropicModel.Text = string.IsNullOrWhiteSpace(_s.AnthropicModel) ? ClaudeModels.DefaultApiModel : _s.AnthropicModel;
        McpEnabled.IsChecked = _s.McpEnabled;
        OfflineStatus.Text = LocalLibre.Status();
        if (LocalLibre.IsInstalled) OfflineInstallButton.Content = "Repair…";
        OllamaStatus.Text = LocalOllama.Status();
        _ollamaModel = _s.OllamaModel;
        OllamaModel.SelectionChanged += OllamaModel_SelectionChanged;

        // The program: the configured path, or the one found automatically (kept automatic unless changed).
        _foundClaudeCode = ClaudeCodeTranslator.Locate(null);
        ClaudeCodePath.Text = string.IsNullOrWhiteSpace(_s.ClaudeCodePath) ? _foundClaudeCode ?? "" : _s.ClaudeCodePath;
        ClaudeCodeFound.Text = string.IsNullOrWhiteSpace(_s.ClaudeCodePath)
            ? _foundClaudeCode != null ? "Found automatically." : "Not found on this PC. Use “Download…” to install Claude Code, then sign in with claude /login."
            : "Set by you.";
        ClaudeCodeModel.Text = _s.ClaudeCodeModel;
        ClaudeCodeToken.Password = _s.ClaudeCodeToken;
        McpPort.Text = _s.McpPort.ToString();
        try
        {
            var langs = OcrService.InstalledLanguages;
            OcrLanguages.Text = langs.Count == 0 ? "none" : string.Join(", ", langs);
        }
        catch (Exception ex)
        {
            OcrLanguages.Text = "unavailable: " + ex.Message;
        }
        Loaded += async (_, _) => await Task.WhenAll(LoadAccountModelsAsync(), LoadClaudeCodeModelsAsync(), LoadOllamaModelsAsync());
    }

    readonly string? _foundClaudeCode;
    string _listedForKey = "";

    /// <summary>
    /// The models the API key can use, as Anthropic lists them. No key, no list: a list built into the app
    /// would go out of date.
    /// </summary>
    async Task LoadAccountModelsAsync()
    {
        string key = AnthropicKey.Password.Trim();
        if (key.Length == 0) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        if (key == _listedForKey && AnthropicModel.ItemsSource != null) return;
        _listedForKey = key;
        string current = AnthropicModel.Text;
        if (key.Length == 0)
        {
            AnthropicModel.ItemsSource = null;
            AnthropicModelHint.Text = "Enter an API key to list the models it can use.";
            return;
        }
        AnthropicModelHint.Text = "Loading the models this key can use…";
        var models = await ClaudeModels.ListAsync(key);
        if (models is not { Count: > 0 })
        {
            AnthropicModel.ItemsSource = null;
            AnthropicModelHint.Text = "Could not list models with this key (check the key and internet connection). You can type a model ID.";
            return;
        }
        AnthropicModel.ItemsSource = models;
        AnthropicModel.Text = current;
        AnthropicModelHint.Text = $"{models.Count} models available to this key, listed by Anthropic.";
    }

    /// <summary>Claude Code's own model names (from its --help), so the list follows Claude Code updates.</summary>
    async Task LoadClaudeCodeModelsAsync()
    {
        var exe = ClaudeCodeTranslator.Locate(ClaudeCodePath.Text);
        if (exe == null) { ClaudeCodeModelHint.Text = "Install Claude Code to choose a model."; return; }
        string current = ClaudeCodeModel.Text;
        var aliases = await ClaudeModels.ClaudeCodeAliasesAsync(exe);
        var items = new List<ModelChoice> { new("", "Claude Code's default") };
        if (aliases != null) items.AddRange(aliases);
        ClaudeCodeModel.ItemsSource = items;
        ClaudeCodeModel.Text = current;
        ClaudeCodeModelHint.Text = aliases is { Count: > 0 }
            ? "Names offered by Claude Code; each follows the latest model of that family. Empty = Claude Code's default."
            : "Empty = Claude Code's default; or type a model name.";
    }

    /// <summary>The model chosen for translation (kept even while the list is still loading).</summary>
    string _ollamaModel = "";
    string? _downloading;

    /// <summary>The models downloaded to Ollama, as Ollama lists them.</summary>
    async Task LoadOllamaModelsAsync()
    {
        // Memory decides how big a model this PC can run; a rough third of it for the model file.
        double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        OllamaDownloadHint.Text =
            $"Type a name as ollama.com/library shows it, family:size, e.g. {LocalOllama.SuggestedModel} or qwen3:8b. " +
            $"Bigger models translate better but run slower; with {ramGb:0} GB of memory, this PC runs models up to about {ramGb * 0.35:0} GB.";
        if (OllamaDownloadName.Text.Length == 0) OllamaDownloadName.Text = LocalOllama.SuggestedModel;

        if (!LocalOllama.IsInstalled)
        {
            OllamaInstallButton.Content = "Install and download";
            OllamaModel.IsEnabled = false;
            OllamaModelHint.Text = "Nothing downloaded yet: Ollama is installed with the first model you download below.";
            return;
        }
        OllamaModelHint.Text = "Reading the downloaded models…";
        var models = await LocalOllama.ModelsAsync();
        OllamaStatus.Text = LocalOllama.Status();
        if (models == null) { OllamaModelHint.Text = "Ollama didn't answer. Downloading a model below also repairs it."; return; }
        OllamaModel.ItemsSource = models;
        OllamaModel.IsEnabled = models.Count > 0;
        // A model still downloading stays chosen; otherwise a choice that is gone falls back to one that is here.
        if (_downloading == null && models.All(m => m.Id != _ollamaModel)) _ollamaModel = models.FirstOrDefault()?.Id ?? "";
        OllamaModel.SelectedValue = _ollamaModel;
        OllamaModelHint.Text = models.Count == 0
            ? "No models downloaded yet: download one below."
            : $"{models.Count} downloaded to this PC. To add another, download it below; it then appears here.";
    }

    void OllamaModel_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OllamaModel.SelectedValue is string id) _ollamaModel = id;
    }

    /// <summary>A download started from here may have finished: list again.</summary>
    async void OllamaModel_DropDownOpened(object? sender, EventArgs e)
    {
        if (_downloading != null) await LoadOllamaModelsAsync();
    }

    void OllamaInstall_Click(object sender, RoutedEventArgs e)
    {
        string model = OllamaDownloadName.Text.Trim();
        if (model.Length == 0) model = LocalOllama.SuggestedModel;
        // The model downloaded is the one used, unless another is chosen afterwards.
        _ollamaModel = _downloading = model;
        _s.OllamaModel = model;
        LocalOllama.StartInstall(model);
        OllamaStatus.Text = $"Downloading {model} in the window that opened… when it says Done, it is ready to use.";
    }

    async void AnthropicKey_LostFocus(object sender, RoutedEventArgs e) => await LoadAccountModelsAsync();

    void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) Launch.Url(url);
    }

    void LibreSite_Click(object sender, RoutedEventArgs e)
    {
        var url = LibreUrl.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) Launch.Url(url);
    }

    void OfflineInstall_Click(object sender, RoutedEventArgs e)
    {
        PdfTranslator.Translators.LocalLibre.StartInstall();
        OfflineStatus.Text = "Installing in the window that opened… when it says Done, choose “LibreTranslate offline (on this PC)”.";
    }

    void GetToken_Click(object sender, RoutedEventArgs e)
    {
        var exe = PdfTranslator.Translators.ClaudeCodeTranslator.Locate(ClaudeCodePath.Text);
        if (exe == null)
        {
            MessageBox.Show(this, "Claude Code was not found. Use “Download…” to install it, or enter its path.", Title);
            return;
        }
        Launch.Terminal(exe, "setup-token");
        ClaudeCodeToken.Focus();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.DeepLKey = DeepLKey.Password.Trim();
        _s.GoogleCloudKey = GoogleKey.Password.Trim();
        _s.AzureKey = AzureKey.Password.Trim();
        _s.AzureRegion = AzureRegion.Text.Trim();
        _s.LibreUrl = LibreUrl.Text.Trim();
        _s.LibreKey = LibreKey.Password.Trim();
        _s.MyMemoryEmail = MyMemoryEmail.Text.Trim();
        _s.AnthropicKey = AnthropicKey.Password.Trim();
        _s.AnthropicModel = string.IsNullOrWhiteSpace(AnthropicModel.Text) ? ClaudeModels.DefaultApiModel : AnthropicModel.Text.Trim();
        _s.McpEnabled = McpEnabled.IsChecked == true;
        // Keep finding Claude Code automatically unless a different program was entered.
        string path = ClaudeCodePath.Text.Trim();
        _s.ClaudeCodePath = string.Equals(path, _foundClaudeCode, StringComparison.OrdinalIgnoreCase) ? "" : path;
        _s.ClaudeCodeModel = ClaudeCodeModel.Text.Trim();
        _s.ClaudeCodeToken = ClaudeCodeToken.Password.Trim();
        _s.OllamaModel = _ollamaModel;
        if (int.TryParse(McpPort.Text, out int port) && port is > 1024 and < 65536) _s.McpPort = port;
        DialogResult = true;
    }
}

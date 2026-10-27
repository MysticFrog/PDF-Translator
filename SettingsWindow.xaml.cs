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
        AnthropicModel.ItemsSource = ClaudeModels.Api;
        AnthropicModel.Text = string.IsNullOrWhiteSpace(_s.AnthropicModel) ? ClaudeModels.DefaultApiModel : _s.AnthropicModel;
        AnthropicModelSource.Text = "built-in list";
        McpEnabled.IsChecked = _s.McpEnabled;
        OfflineStatus.Text = PdfTranslator.Translators.LocalLibre.Status();
        if (PdfTranslator.Translators.LocalLibre.IsInstalled) OfflineInstallButton.Content = "Repair…";
        ClaudeCodePath.Text = _s.ClaudeCodePath;
        ClaudeCodeModel.ItemsSource = ClaudeModels.ClaudeCode;
        ClaudeCodeModel.Text = _s.ClaudeCodeModel;
        ClaudeCodeToken.Password = _s.ClaudeCodeToken;
        if (string.IsNullOrWhiteSpace(_s.ClaudeCodePath) && PdfTranslator.Translators.ClaudeCodeTranslator.Locate(null) is { } found)
            ClaudeCodePath.ToolTip = "Found automatically: " + found;
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
        Loaded += async (_, _) => await LoadAccountModelsAsync();
    }

    /// <summary>With an API key (saved or ANTHROPIC_API_KEY), list the models the account can use.</summary>
    async Task LoadAccountModelsAsync()
    {
        string key = AnthropicKey.Password.Trim();
        if (key.Length == 0) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        if (key.Length == 0) return;
        AnthropicModelSource.Text = "loading…";
        var models = await ClaudeModels.ListAsync(key);
        if (models is not { Count: > 0 }) { AnthropicModelSource.Text = "built-in list"; return; }
        string current = AnthropicModel.Text;
        AnthropicModel.ItemsSource = models;
        AnthropicModel.Text = current;
        AnthropicModelSource.Text = "from your account";
    }

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
        _s.ClaudeCodePath = ClaudeCodePath.Text.Trim();
        _s.ClaudeCodeModel = ClaudeCodeModel.Text.Trim();
        _s.ClaudeCodeToken = ClaudeCodeToken.Password.Trim();
        if (int.TryParse(McpPort.Text, out int port) && port is > 1024 and < 65536) _s.McpPort = port;
        DialogResult = true;
    }
}

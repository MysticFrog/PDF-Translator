using System.Windows;
using PdfTranslator.Core;

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
        AnthropicModel.Text = _s.AnthropicModel;
        McpEnabled.IsChecked = _s.McpEnabled;
        ClaudeCodePath.Text = _s.ClaudeCodePath;
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
        _s.AnthropicModel = string.IsNullOrWhiteSpace(AnthropicModel.Text) ? "claude-opus-5-5" : AnthropicModel.Text.Trim();
        _s.McpEnabled = McpEnabled.IsChecked == true;
        _s.ClaudeCodePath = ClaudeCodePath.Text.Trim();
        _s.ClaudeCodeModel = ClaudeCodeModel.Text.Trim();
        _s.ClaudeCodeToken = ClaudeCodeToken.Password.Trim();
        if (int.TryParse(McpPort.Text, out int port) && port is > 1024 and < 65536) _s.McpPort = port;
        DialogResult = true;
    }
}

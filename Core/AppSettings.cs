using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfTranslator.Core;

/// <summary>User settings; API keys are stored encrypted with Windows DPAPI (current user).</summary>
public sealed class AppSettings
{
    public string Provider { get; set; } = "mymemory";
    public string SourceLanguage { get; set; } = Languages.Auto;
    public string TargetLanguage { get; set; } = "en";
    public OcrMode Ocr { get; set; } = OcrMode.Auto;

    public string DeepLKey { get; set; } = "";
    public string GoogleCloudKey { get; set; } = "";
    public string AzureKey { get; set; } = "";
    public string AzureRegion { get; set; } = "";
    public string LibreUrl { get; set; } = "https://libretranslate.com";
    public string LibreKey { get; set; } = "";
    public string MyMemoryEmail { get; set; } = "";
    public string AnthropicKey { get; set; } = "";
    public string AnthropicModel { get; set; } = PdfTranslator.Translators.ClaudeModels.DefaultApiModel;
    /// <summary>Path of the Claude Code CLI; empty = find it (PATH, ~/.local/bin, Claude Desktop's bundled copy).</summary>
    public string ClaudeCodePath { get; set; } = "";
    /// <summary>Model alias for Claude Code (e.g. opus, sonnet); empty = Claude Code's default.</summary>
    public string ClaudeCodeModel { get; set; } = "";
    /// <summary>The Ollama model for offline AI translation (e.g. qwen3:4b).</summary>
    public string OllamaModel { get; set; } = "";
    /// <summary>Long-lived sign-in token from <c>claude setup-token</c>; used when the CLI has no saved /login.</summary>
    public string ClaudeCodeToken { get; set; } = "";

    /// <summary>Serve the MCP endpoint so Claude Code / Claude Desktop can drive the open document.</summary>
    public bool McpEnabled { get; set; } = true;
    public int McpPort { get; set; } = 8765;
    public bool ShowBlockOutlines { get; set; } = true;

    static readonly string[] SecretProps = { nameof(DeepLKey), nameof(GoogleCloudKey), nameof(AzureKey), nameof(LibreKey), nameof(AnthropicKey), nameof(ClaudeCodeToken) };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfTranslator", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            s.TransformSecrets(Unprotect);
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
        copy.TransformSecrets(Protect);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, Json));
    }

    void TransformSecrets(Func<string, string> f)
    {
        foreach (var name in SecretProps)
        {
            var p = typeof(AppSettings).GetProperty(name)!;
            p.SetValue(this, f((string)p.GetValue(this)!));
        }
    }

    static string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" :
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string stored)
    {
        if (!stored.StartsWith("dpapi:")) return stored;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
}

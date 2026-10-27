using System.Diagnostics;
using System.Text.RegularExpressions;
using Anthropic;

namespace PdfTranslator.Translators;

/// <summary>A model choice for a dropdown: the value saved in settings and what the list shows.</summary>
public sealed record ModelChoice(string Id, string Label)
{
    public override string ToString() => Id;
}

/// <summary>
/// Model choices for Settings, always read from the source (never a list baked into the app, which would go
/// stale): the Anthropic Models API for an API key, and Claude Code's own help for its model names.
/// </summary>
public static class ClaudeModels
{
    /// <summary>Used when no model is chosen for the Anthropic API.</summary>
    public const string DefaultApiModel = "claude-opus-5-5";

    /// <summary>The models the key can use, newest first, from the Models API; null when it can't be reached.</summary>
    public static async Task<List<ModelChoice>?> ListAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        try
        {
            var client = new AnthropicClient { ApiKey = apiKey.Trim() };
            var page = await client.Models.List(cancellationToken: ct);
            return page.Items.Select(m => new ModelChoice(m.ID, m.DisplayName)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The model names Claude Code itself suggests for --model ("fable", "opus", "sonnet"…: each follows the
    /// latest model of that family), read from its --help. Null when Claude Code can't be run.
    /// </summary>
    public static async Task<List<ModelChoice>?> ClaudeCodeAliasesAsync(string exe, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--help")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token);
            string help = await output;

            // The --model option's description, up to the next option.
            var m = Regex.Match(help, @"--model\b(?<desc>.*?)(?=\n\s*-{1,2}[a-zA-Z])", RegexOptions.Singleline);
            if (!m.Success) return new();
            return Regex.Matches(m.Groups["desc"].Value, @"'([a-z][a-z0-9-]*)'")
                .Select(a => a.Groups[1].Value).Distinct()
                .Select(a => new ModelChoice(a, $"{a} · latest {char.ToUpper(a[0])}{a[1..]} model"))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

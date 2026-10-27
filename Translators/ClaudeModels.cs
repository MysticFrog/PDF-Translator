using Anthropic;

namespace PdfTranslator.Translators;

/// <summary>A model choice for a dropdown: the value saved in settings and what the list shows.</summary>
public sealed record ModelChoice(string Id, string Label)
{
    public override string ToString() => Id;
}

/// <summary>Claude models offered in Settings, for the API and for Claude Code.</summary>
public static class ClaudeModels
{
    public const string DefaultApiModel = "claude-opus-5-5";

    /// <summary>Current models (the Models API replaces this list when an API key is available).</summary>
    public static readonly IReadOnlyList<ModelChoice> Api = new ModelChoice[]
    {
        new("claude-opus-5-5", "Claude Opus 5.5 · recommended"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 · faster, about half the price"),
        new("claude-haiku-5-5", "Claude Haiku 5.5 · fastest, cheapest"),
        new("claude-fable-5-1", "Claude Fable 5.1 · most capable, highest price"),
        new("claude-opus-5", "Claude Opus 5"),
        new("claude-sonnet-5", "Claude Sonnet 5"),
    };

    /// <summary>Claude Code takes aliases for the latest model of a family, or a full model name.</summary>
    public static readonly IReadOnlyList<ModelChoice> ClaudeCode = new ModelChoice[]
    {
        new("", "Claude Code's default"),
        new("opus", "opus · latest Opus"),
        new("sonnet", "sonnet · latest Sonnet, faster and cheaper"),
        new("haiku", "haiku · latest Haiku, fastest and cheapest"),
        new("fable", "fable · latest Fable, most capable"),
    }.Concat(Api).ToList();

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
}

using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using PdfTranslator.Core;

namespace PdfTranslator.Translators;

/// <summary>
/// Translation through Claude via the official Anthropic SDK. Sends a page's blocks as one JSON array so the
/// model sees surrounding context, and asks for an array of the same length back.
/// Server-side fallback ("default") is enabled so a policy decline is retried on the model's default fallback.
/// </summary>
public sealed class ClaudeTranslator : ITranslator
{
    readonly AnthropicClient _client;
    readonly string _model;

    public ClaudeTranslator(string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new MissingKeyException(KeyHelp.For("claude"));
        _client = new AnthropicClient { ApiKey = apiKey.Trim() };
        _model = model;
    }

    public string Id => "claude";
    public string DisplayName => "Claude";

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        foreach (var batch in Batch(texts, 60, 12_000))
        {
            var items = batch.Select(b => b.text).ToList();
            var translated = await TranslateBatchAsync(items, from, to, ct);
            if (translated == null || translated.Count != items.Count)
            {
                // Length mismatch: fall back to one request per block so alignment is guaranteed.
                translated = new List<string>();
                foreach (var item in items)
                {
                    var one = await TranslateBatchAsync(new List<string> { item }, from, to, ct);
                    translated.Add(one is { Count: 1 } ? one[0] : item);
                }
            }
            for (int i = 0; i < batch.Count; i++) results[batch[i].index] = translated[i];
        }
        return results;
    }

    async Task<List<string>?> TranslateBatchAsync(List<string> items, string from, string to, CancellationToken ct)
    {
        string source = from == Languages.Auto ? "the source language (detect it)" : Languages.NameOf(from);
        string system =
            $"You translate text extracted from PDF pages from {source} into {Languages.NameOf(to)}. " +
            "The user message is a JSON array of text blocks from one page, in reading order. " +
            "Translate every block faithfully, using the neighbouring blocks as context. Keep numbers, units, part numbers, " +
            "codes, URLs, e-mail addresses and proper names unchanged. Keep translations about as short as the source, " +
            "because each must fit back into the original layout box. Placeholders like {{1}} stand for icons inside " +
            "the sentence: keep each exactly once, at the matching place. If a block is already in the target language or is " +
            "not translatable, return it unchanged. Reply with only a JSON array of strings, the same length and order as the input.";

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 16000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = system,
            Messages = [new BetaUserMessageParam { Content = JsonSerializer.Serialize(items) }],
        };

        var response = await _client.Beta.Messages.Create(parameters, ct);
        if (response.StopReason == BetaStopReason.Refusal)
            throw new InvalidOperationException("Claude declined to translate this content.");

        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var text)) sb.Append(text.Text);
        return ParseArray(sb.ToString());
    }

    static List<string>? ParseArray(string reply)
    {
        int a = reply.IndexOf('['), b = reply.LastIndexOf(']');
        if (a < 0 || b <= a) return null;
        try { return JsonSerializer.Deserialize<List<string>>(reply[a..(b + 1)]); }
        catch (JsonException) { return null; }
    }

    static IEnumerable<List<(int index, string text)>> Batch(IReadOnlyList<string> texts, int maxItems, int maxChars)
    {
        var batch = new List<(int, string)>();
        int chars = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            if (batch.Count > 0 && (batch.Count >= maxItems || chars + texts[i].Length > maxChars))
            {
                yield return batch;
                batch = new();
                chars = 0;
            }
            batch.Add((i, texts[i]));
            chars += texts[i].Length;
        }
        if (batch.Count > 0) yield return batch;
    }
}

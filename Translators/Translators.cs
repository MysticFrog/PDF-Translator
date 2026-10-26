using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfTranslator.Core;

namespace PdfTranslator.Translators;

public interface ITranslator
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Translates each string; the result has the same length and order. <paramref name="from"/> may be "auto".</summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct);
}

public sealed record ProviderInfo(string Id, string Name, string Requirement)
{
    public override string ToString() => Name;
}

public static class TranslatorFactory
{
    public static readonly IReadOnlyList<ProviderInfo> Providers = new[]
    {
        new ProviderInfo("mymemory", "MyMemory (free)", "No key. Optional e-mail raises the daily quota (5,000 → 50,000 chars/day)."),
        new ProviderInfo("google-free", "Google Translate (free web endpoint)", "No key. Unofficial endpoint; Google may refuse it on some networks."),
        new ProviderInfo("libre", "LibreTranslate", "Server URL (self-hosted or libretranslate.com) and optional key."),
        new ProviderInfo("deepl", "DeepL API", "DeepL API key (Free keys end in :fx)."),
        new ProviderInfo("google-cloud", "Google Cloud Translation", "Google Cloud API key."),
        new ProviderInfo("azure", "Microsoft Translator (Azure)", "Azure Translator key and region."),
        new ProviderInfo("claude-code", "Claude (Claude Code sign-in, no API key)", "Uses the Claude Code CLI with your Claude account (one-time /login). Context-aware, best for technical text."),
        new ProviderInfo("claude", "Claude (Anthropic API key)", "Anthropic API key. Context-aware, best for technical text."),
        new ProviderInfo("none", "Extract only — no translation", "Finds the blocks but does not translate. Fill them in the editor, or ask Claude in Claude Code / Claude Desktop to translate the open PDF through the MCP server."),
    };

    public static ITranslator? Create(string id, AppSettings s) => id switch
    {
        "none" => null,
        "claude-code" => new ClaudeCodeTranslator(s.ClaudeCodePath, s.ClaudeCodeModel),
        "google-free" => new GoogleFreeTranslator(),
        "mymemory" => new MyMemoryTranslator(s.MyMemoryEmail),
        "libre" => new LibreTranslator(Require(s.LibreUrl, "LibreTranslate URL"), s.LibreKey),
        "deepl" => new DeepLTranslator(Require(s.DeepLKey, "DeepL API key")),
        "google-cloud" => new GoogleCloudTranslator(Require(s.GoogleCloudKey, "Google Cloud API key")),
        "azure" => new AzureTranslator(Require(s.AzureKey, "Azure Translator key"), s.AzureRegion),
        "claude" => new ClaudeTranslator(
            string.IsNullOrWhiteSpace(s.AnthropicKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "" : s.AnthropicKey,
            string.IsNullOrWhiteSpace(s.AnthropicModel) ? "claude-opus-5-5" : s.AnthropicModel.Trim()),
        _ => throw new ArgumentException($"Unknown provider '{id}'."),
    };

    static string Require(string value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{what} is not set. Open Settings to enter it.") : value.Trim();
}

/// <summary>Shared HTTP plumbing: one HttpClient, bounded parallelism, retry on 429/5xx.</summary>
public abstract class HttpTranslator : ITranslator
{
    protected static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PdfTranslator/1.0");
        return c;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct);

    protected static async Task<string> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = make();
            using var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return body;
            if (body.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("MyMemory's free daily quota is used up. Add an e-mail in Settings (raises the quota) or choose another service.");
            if (body.Contains("<title>Sorry", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("The service refused automated requests from this computer (429). Choose another translation service.");
            bool retry = (int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500;
            if (!retry || attempt >= 4)
                throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 300)}");
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
        }
    }

    /// <summary>Runs a per-item translation with limited concurrency.</summary>
    protected static async Task<IReadOnlyList<string>> PerItemAsync(IReadOnlyList<string> texts, int parallel,
        Func<string, Task<string>> one, CancellationToken ct)
    {
        var results = new string[texts.Count];
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(texts.Select(async (t, i) =>
        {
            await gate.WaitAsync(ct);
            try { results[i] = await one(t); }
            finally { gate.Release(); }
        }));
        return results;
    }

    /// <summary>Splits into batches bounded by item count and total characters.</summary>
    protected static IEnumerable<List<(int index, string text)>> Batches(IReadOnlyList<string> texts, int maxItems, int maxChars)
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

    /// <summary>Splits long text at sentence boundaries for services with per-request size limits.</summary>
    protected static List<string> SplitSentences(string text, int maxLen)
    {
        if (text.Length <= maxLen) return new() { text };
        var parts = new List<string>();
        var cur = new StringBuilder();
        foreach (var piece in System.Text.RegularExpressions.Regex.Split(text, @"(?<=[\.\!\?;:])\s+|(?<=[。！？；])"))
        {
            if (cur.Length > 0 && cur.Length + piece.Length + 1 > maxLen) { parts.Add(cur.ToString()); cur.Clear(); }
            if (piece.Length > maxLen)
            {
                for (int i = 0; i < piece.Length; i += maxLen) parts.Add(piece.Substring(i, Math.Min(maxLen, piece.Length - i)));
                continue;
            }
            if (cur.Length > 0 && !TextTools.IsCjk(piece.FirstOrDefault())) cur.Append(' ');
            cur.Append(piece);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts;
    }

    protected static string JoinParts(List<string> parts) =>
        string.Concat(parts.Select((p, i) => i == 0 || p.Length == 0 || TextTools.IsCjk(p[0]) ? p : " " + p));

    protected static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed class GoogleFreeTranslator : HttpTranslator
{
    public override string Id => "google-free";
    public override string DisplayName => "Google Translate (free)";

    static string Code(string c) => c switch { "nb" => "no", "he" => "iw", _ => c };

    public override Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct) =>
        PerItemAsync(texts, 4, async text =>
        {
            var outParts = new List<string>();
            foreach (var part in SplitSentences(text, 1800))
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
                             $"&sl={Code(from)}&tl={Code(to)}&q={Uri.EscapeDataString(part)}";
                var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
                var root = JsonNode.Parse(body)!.AsArray();
                var sb = new StringBuilder();
                foreach (var seg in root[0]!.AsArray()) sb.Append(seg?[0]?.GetValue<string>());
                outParts.Add(sb.ToString());
            }
            return JoinParts(outParts);
        }, ct);
}

public sealed class MyMemoryTranslator(string email) : HttpTranslator
{
    public override string Id => "mymemory";
    public override string DisplayName => "MyMemory";

    public override Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct) =>
        PerItemAsync(texts, 3, async text =>
        {
            var outParts = new List<string>();
            foreach (var part in SplitSentences(text, 450))
            {
                string src = from != Languages.Auto ? from : TextTools.GuessLanguage(part) ?? "Autodetect";
                if (src == to) { outParts.Add(part); continue; }
                string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(part)}&langpair={src}|{to}";
                if (!string.IsNullOrWhiteSpace(email)) url += "&de=" + Uri.EscapeDataString(email.Trim());
                var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
                var node = JsonNode.Parse(body)!;
                int status = node["responseStatus"]?.GetValueKind() == JsonValueKind.Number ? node["responseStatus"]!.GetValue<int>() : 200;
                if (status != 200) throw new HttpRequestException($"MyMemory: {node["responseDetails"]}");
                string result = System.Net.WebUtility.HtmlDecode(node["responseData"]?["translatedText"]?.GetValue<string>() ?? part);
                if (result.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("MyMemory daily quota used up. Add an e-mail in Settings or choose another service.");
                // MyMemory answers in-band with an error text when it thinks source and target are the same.
                if (result.StartsWith("PLEASE SELECT TWO DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase) ||
                    result.StartsWith("INVALID ", StringComparison.Ordinal) || result.StartsWith("NO QUERY SPECIFIED", StringComparison.Ordinal))
                    result = part;
                outParts.Add(result);
            }
            return JoinParts(outParts);
        }, ct);
}

public sealed class LibreTranslator(string baseUrl, string apiKey) : HttpTranslator
{
    public override string Id => "libre";
    public override string DisplayName => "LibreTranslate";

    static string Code(string c) => c switch { "zh-CN" => "zh", "zh-TW" => "zt", _ => c };

    public override async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        string url = baseUrl.TrimEnd('/') + "/translate";
        foreach (var batch in Batches(texts, 25, 8000))
        {
            var payload = new JsonObject
            {
                ["q"] = new JsonArray(batch.Select(b => (JsonNode?)b.text).ToArray()),
                ["source"] = Code(from),
                ["target"] = Code(to),
                ["format"] = "text",
            };
            if (!string.IsNullOrWhiteSpace(apiKey)) payload["api_key"] = apiKey.Trim();
            var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            }, ct);
            var arr = JsonNode.Parse(body)!["translatedText"]!.AsArray();
            for (int i = 0; i < batch.Count; i++) results[batch[i].index] = arr[i]!.GetValue<string>();
        }
        return results;
    }
}

public sealed class DeepLTranslator(string key) : HttpTranslator
{
    public override string Id => "deepl";
    public override string DisplayName => "DeepL";

    static string Target(string c) => c switch
    {
        "en" => "EN-US", "pt" => "PT-BR", "zh-CN" => "ZH-HANS", "zh-TW" => "ZH-HANT", _ => c.ToUpperInvariant(),
    };

    static string Source(string c) => c.StartsWith("zh") ? "ZH" : c.ToUpperInvariant();

    public override async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        string host = key.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";
        foreach (var batch in Batches(texts, 50, 100_000))
        {
            var payload = new JsonObject
            {
                ["text"] = new JsonArray(batch.Select(b => (JsonNode?)b.text).ToArray()),
                ["target_lang"] = Target(to),
                ["preserve_formatting"] = true,
            };
            if (from != Languages.Auto) payload["source_lang"] = Source(from);
            var body = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
                return req;
            }, ct);
            var arr = JsonNode.Parse(body)!["translations"]!.AsArray();
            for (int i = 0; i < batch.Count; i++) results[batch[i].index] = arr[i]!["text"]!.GetValue<string>();
        }
        return results;
    }
}

public sealed class GoogleCloudTranslator(string key) : HttpTranslator
{
    public override string Id => "google-cloud";
    public override string DisplayName => "Google Cloud Translation";

    public override async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        foreach (var batch in Batches(texts, 100, 25_000))
        {
            var payload = new JsonObject
            {
                ["q"] = new JsonArray(batch.Select(b => (JsonNode?)b.text).ToArray()),
                ["target"] = to,
                ["format"] = "text",
            };
            if (from != Languages.Auto) payload["source"] = from;
            var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                "https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(key))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            }, ct);
            var arr = JsonNode.Parse(body)!["data"]!["translations"]!.AsArray();
            for (int i = 0; i < batch.Count; i++)
                results[batch[i].index] = System.Net.WebUtility.HtmlDecode(arr[i]!["translatedText"]!.GetValue<string>());
        }
        return results;
    }
}

public sealed class AzureTranslator(string key, string region) : HttpTranslator
{
    public override string Id => "azure";
    public override string DisplayName => "Microsoft Translator";

    static string Code(string c) => c switch { "zh-CN" => "zh-Hans", "zh-TW" => "zh-Hant", _ => c };

    public override async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, CancellationToken ct)
    {
        var results = new string[texts.Count];
        string url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Code(to);
        if (from != Languages.Auto) url += "&from=" + Code(from);
        foreach (var batch in Batches(texts, 100, 45_000))
        {
            var payload = new JsonArray(batch.Select(b => (JsonNode?)new JsonObject { ["Text"] = b.text }).ToArray());
            var body = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("Ocp-Apim-Subscription-Key", key);
                if (!string.IsNullOrWhiteSpace(region)) req.Headers.Add("Ocp-Apim-Subscription-Region", region.Trim());
                return req;
            }, ct);
            var arr = JsonNode.Parse(body)!.AsArray();
            for (int i = 0; i < batch.Count; i++)
                results[batch[i].index] = arr[i]!["translations"]![0]!["text"]!.GetValue<string>();
        }
        return results;
    }
}

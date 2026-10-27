using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfTranslator.Mcp;

public sealed record McpTool(string Name, string Description, JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<McpResult>> Handler);

/// <summary>Tool output: text and/or PNG images.</summary>
public sealed class McpResult
{
    public List<JsonObject> Content { get; } = new();
    public bool IsError { get; init; }

    public static McpResult Text(string text, bool error = false)
    {
        var r = new McpResult { IsError = error };
        r.Content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return r;
    }

    public static McpResult Json(JsonNode node) => Text(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public McpResult AddImage(byte[] png)
    {
        Content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" });
        return this;
    }
}

/// <summary>
/// Minimal Model Context Protocol server (Streamable HTTP transport, JSON responses, stateless) on
/// http://localhost:{port}/mcp. Only loopback connections are served and browser origins other than
/// localhost are refused (DNS-rebinding protection).
/// </summary>
public sealed class McpServer : IDisposable
{
    static readonly string[] SupportedVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    readonly HttpListener _listener = new();
    readonly IReadOnlyList<McpTool> _tools;
    readonly string _instructions;
    readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/mcp";

    public McpServer(int port, IReadOnlyList<McpTool> tools, string instructions)
    {
        Port = port;
        _tools = tools;
        _instructions = instructions;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { resp.StatusCode = 403; return; }
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.IsLoopback))
            {
                resp.StatusCode = 403;
                return;
            }
            if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { resp.StatusCode = 404; return; }
            if (req.HttpMethod != "POST")
            {
                resp.StatusCode = 405;
                resp.AddHeader("Allow", "POST");
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            var answer = await ProcessAsync(body);
            if (answer == null) { resp.StatusCode = 202; return; }

            var bytes = Encoding.UTF8.GetBytes(answer);
            resp.StatusCode = 200;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            try
            {
                resp.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes(Error(null, -32603, ex.Message).ToJsonString());
                await resp.OutputStream.WriteAsync(bytes);
            }
            catch { /* client went away */ }
        }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    /// <summary>Handles one JSON-RPC message or batch; returns null when there is nothing to send back.</summary>
    public async Task<string?> ProcessAsync(string body)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { return Error(null, -32700, "Parse error").ToJsonString(); }

        if (node is JsonArray batch)
        {
            var results = new JsonArray();
            foreach (var item in batch)
                if (item is JsonObject m && await HandleMessageAsync(m) is { } r) results.Add(r);
            return results.Count == 0 ? null : results.ToJsonString();
        }
        if (node is JsonObject msg) return (await HandleMessageAsync(msg))?.ToJsonString();
        return Error(null, -32600, "Invalid request").ToJsonString();
    }

    async Task<JsonObject?> HandleMessageAsync(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        string method = msg["method"]?.GetValue<string>() ?? "";
        if (id == null) return null; // notification (e.g. notifications/initialized) or a response

        var p = msg["params"] as JsonObject ?? new JsonObject();
        switch (method)
        {
            case "initialize":
                string requested = p["protocolVersion"]?.GetValue<string>() ?? SupportedVersions[0];
                return Ok(id, new JsonObject
                {
                    ["protocolVersion"] = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "pdf-translator", ["version"] = AppInfo.Version },
                    ["instructions"] = _instructions,
                });
            case "ping":
                return Ok(id, new JsonObject());
            case "tools/list":
                return Ok(id, new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                    }).ToArray()),
                });
            case "tools/call":
                string name = p["name"]?.GetValue<string>() ?? "";
                var tool = _tools.FirstOrDefault(t => t.Name == name);
                if (tool == null) return Error(id, -32602, $"Unknown tool '{name}'");
                McpResult result;
                try { result = await tool.Handler(p["arguments"] as JsonObject ?? new JsonObject(), _cts.Token); }
                catch (Exception ex) { result = McpResult.Text($"{ex.GetType().Name}: {ex.Message}", true); }
                var obj = new JsonObject { ["content"] = new JsonArray(result.Content.Select(c => (JsonNode)c.DeepClone()).ToArray()) };
                if (result.IsError) obj["isError"] = true;
                return Ok(id, obj);
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    static JsonObject Ok(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>
/// stdio transport for clients that only launch local servers (Claude Desktop): forwards newline-delimited
/// JSON-RPC from stdin to the running app's HTTP endpoint, starting the app if it isn't running.
/// </summary>
public static class McpStdioBridge
{
    public static async Task<int> RunAsync(int port)
    {
        string url = $"http://localhost:{port}/mcp";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        if (!await IsUpAsync(http, url))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            for (int i = 0; i < 60 && !await IsUpAsync(http, url); i++) await Task.Delay(500);
        }

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? reply;
            try
            {
                using var resp = await http.PostAsync(url, new StringContent(line, Encoding.UTF8, "application/json"));
                reply = resp.StatusCode == HttpStatusCode.Accepted ? null : await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                var id = (JsonNode.Parse(line) as JsonObject)?["id"]?.DeepClone();
                if (id == null) continue;
                reply = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "PDF Translator is not reachable: " + ex.Message },
                }.ToJsonString();
            }
            if (!string.IsNullOrEmpty(reply)) await stdout.WriteLineAsync(reply.ReplaceLineEndings(" "));
        }
        return 0;
    }

    static async Task<bool> IsUpAsync(HttpClient http, string url)
    {
        try
        {
            using var resp = await http.PostAsync(url, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

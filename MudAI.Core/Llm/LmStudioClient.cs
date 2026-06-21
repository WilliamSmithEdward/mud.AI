using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;

namespace MudAI.Core.Llm;

/// <summary>
/// Talks to LM Studio's OpenAI-compatible server (POST /v1/chat/completions) at the
/// configured base URL (default http://127.0.0.1:1234).
///
/// Thread-safe: stateless apart from the injected HttpClient, which is safe for concurrent use.
/// </summary>
public sealed class LmStudioClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly MudAiOptions _options;
    private readonly ILogger<LmStudioClient> _logger;

    public LmStudioClient(HttpClient http, IOptions<MudAiOptions> options, ILogger<LmStudioClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress ??= new Uri(_options.LlmBaseUrl);
        // Local models can be slow; allow generous time but still bounded. Streaming uses a
        // per-read idle timeout (below) since HttpClient.Timeout does not cover the response body.
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var request = BuildRequest(messages, stream: false);

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", request, JsonOpts, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Log status only (no request body, which carries the prompt/context).
            _logger.LogWarning("LM Studio completion failed: {Status} {Reason}",
                (int)response.StatusCode, response.ReasonPhrase);
            var body = await SafeReadAsync(response, ct);
            throw new HttpRequestException(
                $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
        return parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? "";
    }

    public async IAsyncEnumerable<string> CompleteStreamAsync(
        IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(messages, stream: true);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(request, options: JsonOpts)
        };

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("LM Studio stream failed: {Status} {Reason}",
                (int)response.StatusCode, response.ReasonPhrase);
            var body = await SafeReadAsync(response, ct);
            throw new HttpRequestException(
                $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        int idleMs = _options.StreamIdleTimeoutMs;

        while (true)
        {
            string? line;
            bool idleTimedOut = false;

            // Per-read idle timeout: if no token arrives within the deadline, stop rather than
            // hanging the agent loop on a stalled stream. HttpClient.Timeout does not cover this.
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (idleMs > 0) idleCts.CancelAfter(idleMs);
                try
                {
                    line = await reader.ReadLineAsync(idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    idleTimedOut = true;
                    line = null;
                }
            }

            if (idleTimedOut)
            {
                _logger.LogWarning("LM Studio stream idle for {IdleMs}ms; ending stream early.", idleMs);
                yield break;
            }

            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            // A "length" stop means the model hit max_tokens before finishing - the usual cause of a
            // truncated or empty decision. Surface it cheaply so it is diagnosable.
            if (data.Contains("\"finish_reason\":\"length\"", StringComparison.Ordinal))
                _logger.LogWarning("LM Studio stopped at max_tokens (finish_reason=length); raise MaxResponseTokens if decisions look cut off.");

            string? delta = TryExtractDelta(data);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("/v1/models", ct);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LM Studio ping failed");
            return false;
        }
    }

    private ChatRequest BuildRequest(IReadOnlyList<ChatMessage> messages, bool stream) => new()
    {
        Model = _options.LlmModel,
        Temperature = _options.Temperature,
        MaxTokens = _options.MaxResponseTokens,
        Stream = stream,
        // Force a guaranteed-JSON response so the model can't return rambling prose or nothing.
        ResponseFormat = _options.UseJsonResponseFormat ? new ResponseFormat() : null,
        Messages = [.. messages.Select(m => new WireMessage { Role = m.Role, Content = m.Content })]
    };

    private static string? TryExtractDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;

            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    // --- wire DTOs ---

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<WireMessage> Messages { get; set; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; set; }
    }

    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "json_object";
    }

    private sealed class WireMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public WireMessage? Message { get; set; }
    }
}

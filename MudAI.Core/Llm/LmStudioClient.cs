using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;

namespace MudAI.Core.Llm;

/// <summary>
/// Talks to LM Studio's OpenAI-compatible server (POST /v1/chat/completions) at the
/// configured base URL (default http://127.0.0.1:1234).
/// </summary>
public sealed class LmStudioClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly MudAiOptions _options;

    public LmStudioClient(HttpClient http, IOptions<MudAiOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress ??= new Uri(_options.LlmBaseUrl);
        // Local models can be slow; allow generous time but still bounded.
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Model = _options.LlmModel,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxResponseTokens,
            Stream = false,
            Messages = [.. messages.Select(m => new WireMessage { Role = m.Role, Content = m.Content })]
        };

        using var response = await _http.PostAsJsonAsync("/v1/chat/completions", request, JsonOpts, ct);
        if (!response.IsSuccessStatusCode)
        {
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
        var request = new ChatRequest
        {
            Model = _options.LlmModel,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxResponseTokens,
            Stream = true,
            Messages = [.. messages.Select(m => new WireMessage { Role = m.Role, Content = m.Content })]
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(request, options: JsonOpts)
        };

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct);
            throw new HttpRequestException(
                $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line["data:".Length..].Trim();
            if (data == "[DONE]") yield break;

            string? delta = TryExtractDelta(data);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

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

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("/v1/models", ct);
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
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

namespace MudAI.Core.Llm;

/// <summary>Abstraction over the local OpenAI-compatible chat completions endpoint.</summary>
public interface ILlmClient
{
    /// <summary>Sends a chat completion request and returns the assistant's text content.</summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>Streams the assistant's content token-by-token as it is generated (SSE).</summary>
    IAsyncEnumerable<string> CompleteStreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>Lightweight reachability check (GET /v1/models).</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

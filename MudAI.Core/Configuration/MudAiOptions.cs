using MudAI.Core.Models;

namespace MudAI.Core.Configuration;

/// <summary>Strongly-typed configuration bound from the "MudAi" section of appsettings.json.</summary>
public sealed class MudAiOptions
{
    public const string SectionName = "MudAi";

    // --- Connection ---
    public string MudHost { get; set; } = "mud.arctic.org";
    public int MudPort { get; set; } = 2700;

    // --- LM Studio / LLM (OpenAI-compatible) ---
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string LlmModel { get; set; } = "local-model";
    public double Temperature { get; set; } = 0.4;
    public int MaxResponseTokens { get; set; } = 1024;

    /// <summary>Stream the model's output token-by-token (shown live in the UI).</summary>
    public bool StreamResponses { get; set; } = true;

    /// <summary>Total context window of the local model (tokens).</summary>
    public int ContextWindowTokens { get; set; } = 98304;

    /// <summary>Tokens reserved on top of the response for safety/overhead.</summary>
    public int ReservedOutputTokens { get; set; } = 2048;

    // --- Agent loop pacing ---
    /// <summary>Minimum delay between agent turns (ms).</summary>
    public int MinTurnDelayMs { get; set; } = 1500;

    /// <summary>How long MUD output must be quiet before we consider the screen "settled" (ms).</summary>
    public int OutputSettleMs { get; set; } = 600;

    /// <summary>Max time to wait for a command's first reply line before treating it as no-effect (ms).</summary>
    public int ResponseTimeoutMs { get; set; } = 4000;

    // --- Memory ---
    /// <summary>Path to the SQLite memory DB. Null/empty => %LOCALAPPDATA%\MudAI\memory.db.</summary>
    public string? MemoryDbPath { get; set; }

    /// <summary>Max recent lines retained in the rolling screen buffer.</summary>
    public int MaxRecentLines { get; set; } = 200;

    // --- Anti-loop ---
    /// <summary>Identical failing command this many times in a row => suppress it.</summary>
    public int RepeatThreshold { get; set; } = 3;

    /// <summary>How long a suppressed command stays suppressed (seconds).</summary>
    public int SuppressionCooldownSeconds { get; set; } = 120;

    // --- Auto-login ---
    public LoginOptions Login { get; set; } = new();

    // --- Autonomy ---
    public AutonomyMode DefaultAutonomyMode { get; set; } = AutonomyMode.AutoWithInterrupt;

    /// <summary>Whether the master AI toggle starts ON. Default off so you can log in manually first.</summary>
    public bool AiEnabledOnStart { get; set; }
}

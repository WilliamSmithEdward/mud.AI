using MudAI.Core.Models;

namespace MudAI.Core.Configuration;

/// <summary>Strongly-typed configuration bound from the "MudAi" section of appsettings.json.</summary>
public sealed class MudAiOptions
{
    public const string SectionName = "MudAi";

    // --- Connection ---
    public string MudHost { get; set; } = "mud.arctic.org";
    public int MudPort { get; set; } = 2700;

    /// <summary>Max time to wait for the TCP connect before giving up (ms).</summary>
    public int ConnectTimeoutMs { get; set; } = 15000;

    // --- LM Studio / LLM (OpenAI-compatible) ---
    public string LlmBaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string LlmModel { get; set; } = "local-model";
    public double Temperature { get; set; } = 0.4;

    /// <summary>Cap on tokens generated per decision. The decision JSON is small, so a tight cap
    /// keeps decode (the dominant latency) short. Raise it if a "thinking" model needs headroom.</summary>
    public int MaxResponseTokens { get; set; } = 512;

    /// <summary>Ask the server for a guaranteed-JSON response (OpenAI response_format=json_object).
    /// Suppresses rambling/thinking preamble and prevents empty/garbled decisions. Disable if your
    /// model or server rejects the parameter.</summary>
    public bool UseJsonResponseFormat { get; set; } = true;

    /// <summary>Stream the model's output token-by-token (shown live in the UI).</summary>
    public bool StreamResponses { get; set; } = true;

    /// <summary>Max time to wait for the next streamed token before ending a stalled stream (ms).</summary>
    public int StreamIdleTimeoutMs { get; set; } = 30000;

    /// <summary>Total context window of the local model (tokens).</summary>
    public int ContextWindowTokens { get; set; } = 98304;

    /// <summary>Tokens reserved on top of the response for safety/overhead.</summary>
    public int ReservedOutputTokens { get; set; } = 2048;

    // --- Agent loop pacing ---
    /// <summary>Minimum delay between agent turns (ms). Kept small for a brisk cadence; the settle
    /// and reply waits already pace sends to real server output.</summary>
    public int MinTurnDelayMs { get; set; } = 400;

    /// <summary>How long MUD output must be quiet before we consider the screen "settled" (ms).</summary>
    public int OutputSettleMs { get; set; } = 300;

    /// <summary>Max time to wait for a command's first reply line before treating it as no-effect (ms).</summary>
    public int ResponseTimeoutMs { get; set; } = 2000;

    // --- Memory ---
    /// <summary>Path to the SQLite memory DB. Null/empty => %LOCALAPPDATA%\MudAI\memory.db.</summary>
    public string? MemoryDbPath { get; set; }

    /// <summary>Max recent screen lines fed to the model each turn. This (not the token window) is
    /// the real prompt-size lever: smaller = faster prefill. Durable facts live in memory/awareness.</summary>
    public int MaxRecentLines { get; set; } = 80;

    // --- Anti-loop ---
    /// <summary>Identical failing command this many times in a row => suppress it.</summary>
    public int RepeatThreshold { get; set; } = 3;

    /// <summary>How long a suppressed command stays suppressed (seconds).</summary>
    public int SuppressionCooldownSeconds { get; set; } = 120;

    // --- Awareness knowledge base ---
    /// <summary>Markdown awareness dump path. Null/empty => %LOCALAPPDATA%\MudAI\awareness.md.</summary>
    public string? AwarenessExportPath { get; set; }

    /// <summary>Top awareness entries recalled per category into the prompt (balanced recall).</summary>
    public int AwarenessRecallPerCategory { get; set; } = 2;

    /// <summary>Hard token cap on the rendered "WHAT YOU KNOW" block (backstop against bloat).</summary>
    public int AwarenessRecallTokens { get; set; } = 220;

    /// <summary>Re-write the awareness.md dump after this many new awareness writes during a session.</summary>
    public int AwarenessExportEveryNWrites { get; set; } = 10;

    // --- Auto-login ---
    public LoginOptions Login { get; set; } = new();

    // --- Autonomy ---
    public AutonomyMode DefaultAutonomyMode { get; set; } = AutonomyMode.AutoWithInterrupt;

    /// <summary>Whether the master AI toggle starts ON. Default off so you can log in manually first.</summary>
    public bool AiEnabledOnStart { get; set; }
}

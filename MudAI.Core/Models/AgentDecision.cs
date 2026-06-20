namespace MudAI.Core.Models;

/// <summary>The model's structured decision for a single turn.</summary>
public sealed class AgentDecision
{
    /// <summary>The MUD command to send. Empty if the agent chooses to wait/observe.</summary>
    public string Command { get; init; } = "";

    /// <summary>Short human-readable reasoning, surfaced in the UI.</summary>
    public string Reasoning { get; init; } = "";

    /// <summary>Optional updated short-term goal.</summary>
    public string? Goal { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>Confidence in [0,1].</summary>
    public double Confidence { get; init; } = 0.5;

    /// <summary>A durable lesson worth persisting to memory, if any.</summary>
    public string? Lesson { get; init; }

    /// <summary>True when the agent deliberately wants to wait rather than send a command.</summary>
    public bool Wait { get; init; }

    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
}

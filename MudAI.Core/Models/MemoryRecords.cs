namespace MudAI.Core.Models;

/// <summary>A durable insight the agent has learned and should recall in future sessions.</summary>
public sealed class Lesson
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public string Tags { get; set; } = "";
    public double Confidence { get; set; } = 0.5;
    public int TimesReinforced { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Running success/failure tally for a command verb (e.g. "kill", "north", "open").</summary>
public sealed class CommandKnowledge
{
    public string Command { get; set; } = "";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public int Total => SuccessCount + FailureCount;
}

/// <summary>What the agent knows about a room it has visited.</summary>
public sealed class RoomKnowledge
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Exits { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

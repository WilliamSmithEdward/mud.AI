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

/// <summary>A known directional exit from a room (e.g. "n" leads to "Temple Square").</summary>
public sealed record RoomExit(string Direction, string ToRoom);

/// <summary>A categorized, model-asserted awareness fact keyed by (category, subject).</summary>
public sealed class AwarenessEntry
{
    public long Id { get; set; }
    public string Category { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Fact { get; set; } = "";
    public double Confidence { get; set; } = 0.5;
    public int TimesReinforced { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>A categorized awareness note the model asks to file this turn (category, subject, fact).</summary>
public sealed record AwarenessNote(string Category, string Subject, string Fact);

/// <summary>Room count for a single zone.</summary>
public sealed record ZoneCount(string Zone, int Rooms);

/// <summary>Derived geographic awareness: rooms per zone and how many rooms still have unmapped exits.</summary>
public sealed class ZoneAwareness
{
    public IReadOnlyList<ZoneCount> Zones { get; init; } = [];
    public int FrontierRooms { get; init; }

    /// <summary>Compact one-line summary, or empty if nothing is known yet.</summary>
    public string ToLine()
    {
        if (Zones.Count == 0 && FrontierRooms == 0) return "";

        var parts = new List<string>(2);
        if (Zones.Count > 0)
            parts.Add("Zones: " + string.Join(", ", Zones.Select(z => $"{z.Zone} {z.Rooms}r")));
        if (FrontierRooms > 0)
            parts.Add($"frontier: {FrontierRooms} room(s) with unmapped exits");
        return string.Join(". ", parts) + ".";
    }
}

/// <summary>What the agent knows about a room: its zone, exits, visit count, and mapped destinations.</summary>
public sealed class RoomRecall
{
    public required string Name { get; init; }
    public string Zone { get; init; } = "";
    public string Exits { get; init; } = "";
    public int Visits { get; init; }
    public IReadOnlyList<RoomExit> KnownExits { get; init; } = [];

    /// <summary>Compact one-line summary for the LLM context (token-efficient).</summary>
    public string ToSummary()
    {
        string head = $"Here: {Name}{(string.IsNullOrEmpty(Zone) ? "" : $" [{Zone}]")}, visited {Visits}x";
        if (KnownExits.Count == 0) return head;
        string mapped = string.Join(", ", KnownExits.Select(e => $"{e.Direction}->{e.ToRoom}"));
        return $"{head}. Mapped exits: {mapped}";
    }
}

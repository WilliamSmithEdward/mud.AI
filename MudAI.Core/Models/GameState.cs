namespace MudAI.Core.Models;

/// <summary>
/// Structured, live character/room state assembled from GMCP/MSDP messages. Immutable;
/// updates produce a new instance via <c>with</c>. Null fields are simply "unknown".
/// </summary>
public sealed record GameState
{
    public string? CharName { get; init; }
    public int? Level { get; init; }

    public int? Hp { get; init; }
    public int? MaxHp { get; init; }
    public int? Mp { get; init; }
    public int? MaxMp { get; init; }
    public int? Mv { get; init; }
    public int? MaxMv { get; init; }

    public long? Exp { get; init; }
    public long? Gold { get; init; }

    public string? RoomName { get; init; }
    public string? Zone { get; init; }
    public string? Exits { get; init; }

    public bool HasAny =>
        CharName is not null || Level is not null ||
        Hp is not null || Mp is not null || Mv is not null ||
        RoomName is not null || Exits is not null;

    /// <summary>Compact one-line summary for the LLM context and the UI.</summary>
    public string ToSummary()
    {
        var parts = new List<string>(6);

        if (CharName is not null || Level is not null)
            parts.Add($"Char {CharName ?? "?"}{(Level is not null ? $" (lvl {Level})" : "")}");
        if (Hp is not null) parts.Add($"HP {Hp}{(MaxHp is not null ? "/" + MaxHp : "")}");
        if (Mp is not null) parts.Add($"MP {Mp}{(MaxMp is not null ? "/" + MaxMp : "")}");
        if (Mv is not null) parts.Add($"MV {Mv}{(MaxMv is not null ? "/" + MaxMv : "")}");
        if (RoomName is not null) parts.Add($"Room {RoomName}{(Zone is not null ? $" [{Zone}]" : "")}");
        if (!string.IsNullOrEmpty(Exits)) parts.Add($"Exits {Exits}");

        return string.Join(" | ", parts);
    }
}

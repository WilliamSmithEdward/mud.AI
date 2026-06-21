using MudAI.Core.Models;

namespace MudAI.Core.Memory;

/// <summary>
/// Durable, cross-session knowledge store. Keeps the agent's lessons, per-command
/// success/failure stats, and room notes so it can "learn and grow" between runs.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Creates the database/schema if needed. Safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Inserts a lesson or, if the text already exists, reinforces it (count + confidence up).</summary>
    Task AddOrReinforceLessonAsync(string text, string tags = "", double confidence = 0.6, CancellationToken ct = default);

    /// <summary>Highest-confidence, most-reinforced lessons first.</summary>
    Task<IReadOnlyList<Lesson>> GetTopLessonsAsync(int limit, CancellationToken ct = default);

    /// <summary>Records the outcome of a command verb (e.g. "north", "kill").</summary>
    Task RecordCommandResultAsync(string command, bool success, CancellationToken ct = default);

    /// <summary>Most-used command verbs with their success/failure tallies.</summary>
    Task<IReadOnlyList<CommandKnowledge>> GetCommandKnowledgeAsync(int limit, CancellationToken ct = default);

    /// <summary>Records a visit to a room (creates it or bumps its visit count, updating zone/exits).</summary>
    Task RecordRoomVisitAsync(string name, string zone, string exits, CancellationToken ct = default);

    /// <summary>Records that <paramref name="direction"/> from <paramref name="fromRoom"/> leads to <paramref name="toRoom"/>.</summary>
    Task RecordExitAsync(string fromRoom, string direction, string toRoom, CancellationToken ct = default);

    /// <summary>What we know about a room (zone, exits, visit count, mapped directional exits), or null if unseen.</summary>
    Task<RoomRecall?> GetRoomRecallAsync(string name, CancellationToken ct = default);

    /// <summary>Insert-or-reinforce a categorized awareness fact keyed by (category, subject). The
    /// category is normalized to the closed vocabulary and subject/fact are length-clamped; on
    /// conflict the fact text is refreshed and confidence/reinforcement bumped.</summary>
    Task AddOrReinforceAwarenessAsync(string category, string subject, string fact,
        double confidence = 0.6, CancellationToken ct = default);

    /// <summary>Top <paramref name="perCategory"/> awareness entries per category, ranked for balanced recall.</summary>
    Task<IReadOnlyList<AwarenessEntry>> GetBalancedAwarenessAsync(int perCategory, CancellationToken ct = default);

    /// <summary>All awareness rows ordered by category then rank, for the readable dump.</summary>
    Task<IReadOnlyList<AwarenessEntry>> GetAllAwarenessAsync(CancellationToken ct = default);

    /// <summary>Derived geographic awareness: rooms per zone and how many rooms still have unmapped exits.</summary>
    Task<ZoneAwareness> GetZoneAwarenessAsync(int maxZones, CancellationToken ct = default);

    /// <summary>Row count for a known table ("lessons", "command_knowledge", "rooms", "awareness").</summary>
    Task<int> CountAsync(string table, CancellationToken ct = default);
}

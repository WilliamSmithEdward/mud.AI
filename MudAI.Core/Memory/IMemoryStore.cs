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

    /// <summary>Inserts/updates what we know about a room.</summary>
    Task UpsertRoomAsync(string name, string exits, string notes, CancellationToken ct = default);

    /// <summary>Row count for a known table ("lessons", "command_knowledge", "rooms").</summary>
    Task<int> CountAsync(string table, CancellationToken ct = default);
}

using MudAI.Core.Llm;
using MudAI.Core.Models;

namespace MudAI.Core.Agent;

/// <summary>Everything the context builder needs to assemble one turn's prompt.</summary>
public sealed class AgentContextInput
{
    /// <summary>Recent MUD screen (plain text, newest at the bottom).</summary>
    public required string RecentScreen { get; init; }

    public string Goal { get; init; } = "";
    public string Steering { get; init; } = "";

    /// <summary>Compact live state from GMCP/MSDP (HP/MP/room/exits), empty if unavailable.</summary>
    public string GameStateSummary { get; init; } = "";

    /// <summary>Summary of commands that have been failing (from the anti-loop tracker).</summary>
    public string FailureContext { get; init; } = "";

    /// <summary>Currently-suppressed commands the agent must not send.</summary>
    public IReadOnlyList<string> Suppressed { get; init; } = [];

    public IReadOnlyList<Lesson> Lessons { get; init; } = [];
    public IReadOnlyList<CommandKnowledge> Commands { get; init; } = [];

    public AutonomyMode Mode { get; init; }
}

/// <summary>Assembles the system+user chat messages for a turn, kept within the token budget.</summary>
public interface IContextBuilder
{
    IReadOnlyList<ChatMessage> Build(AgentContextInput input);
}

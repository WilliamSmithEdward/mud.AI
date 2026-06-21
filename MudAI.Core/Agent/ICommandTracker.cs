using MudAI.Core.Models;

namespace MudAI.Core.Agent;

/// <summary>
/// Tracks command-and-outcome history to stop the agent from looping commands that don't work.
/// </summary>
public interface ICommandTracker
{
    /// <summary>Note that a command was just sent (before its response is known).</summary>
    void RecordSent(string command);

    /// <summary>
    /// Classify the MUD's response to a command, update counters, and possibly suppress
    /// the command if it has failed too many times in a row.
    /// </summary>
    CommandOutcome ClassifyAndRecord(string command, string responseText);

    /// <summary>True if this command is currently suppressed (failed repeatedly, cooling down).</summary>
    bool ShouldSuppress(string command);

    /// <summary>Currently-suppressed commands, for display.</summary>
    IReadOnlyList<string> GetActiveSuppressions();

    /// <summary>Human-readable summary of recently-failing commands, to inject into the prompt.</summary>
    string GetFailureContext(int maxItems = 8);

    void Reset();
}

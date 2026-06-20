namespace MudAI.Core.Models;

/// <summary>
/// Controls how the agent's chosen commands reach the MUD. The master AI toggle
/// (on the orchestrator) decides <em>whether</em> the agent runs at all; this enum
/// decides what happens with the commands it produces while it is running.
/// </summary>
public enum AutonomyMode
{
    /// <summary>AI proposes a command; nothing is sent until the user approves (and may edit) it.</summary>
    Proposal,

    /// <summary>AI sends every command automatically; the user can pause/steer/override at any time.</summary>
    AutoWithInterrupt,

    /// <summary>AI auto-sends low-risk commands but pauses for approval on medium/high-risk ones.</summary>
    HybridByRisk,

    /// <summary>Fully autonomous: AI sends everything with no gating whatsoever.</summary>
    FullAuto
}

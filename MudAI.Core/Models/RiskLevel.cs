namespace MudAI.Core.Models;

/// <summary>How risky the agent judges a command to be (used by <see cref="AutonomyMode.HybridByRisk"/>).</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High
}

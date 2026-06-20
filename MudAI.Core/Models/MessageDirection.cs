namespace MudAI.Core.Models;

/// <summary>The origin of a line shown in the transcript.</summary>
public enum MessageDirection
{
    /// <summary>Text received from the MUD.</summary>
    Incoming,

    /// <summary>A command we sent to the MUD (echoed locally).</summary>
    Outgoing,

    /// <summary>A local system/status note (not part of the MUD stream).</summary>
    System
}

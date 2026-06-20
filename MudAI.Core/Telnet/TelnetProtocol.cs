namespace MudAI.Core.Telnet;

/// <summary>Telnet protocol byte constants (RFC 854 + common options).</summary>
internal static class TelnetBytes
{
    public const byte IAC = 255;  // Interpret As Command
    public const byte DONT = 254;
    public const byte DO = 253;
    public const byte WONT = 252;
    public const byte WILL = 251;
    public const byte SB = 250;   // Subnegotiation begin
    public const byte GA = 249;   // Go Ahead
    public const byte SE = 240;   // Subnegotiation end

    // Options
    public const byte OptEcho = 1;
    public const byte OptSuppressGoAhead = 3;
    public const byte OptTerminalType = 24;
    public const byte OptNaws = 31;   // negotiate about window size
    public const byte OptMsdp = 69;   // Mud Server Data Protocol
    public const byte OptGmcp = 201;  // Generic Mud Communication Protocol
}

/// <summary>
/// A completed telnet subnegotiation block (the bytes between IAC SB &lt;option&gt; and IAC SE).
/// Used to carry out-of-band structured data such as GMCP (201) and MSDP (69).
/// </summary>
public sealed record TelnetSubnegotiation(byte Option, byte[] Payload);

namespace MudAI.Core.Telnet;

/// <summary>
/// A minimal async telnet client: connects to a MUD, strips telnet IAC control
/// sequences, and surfaces decoded text (which may still contain ANSI colour codes).
/// </summary>
public interface ITelnetClient : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Decoded text chunk (telnet-stripped). May contain ANSI escape codes and partial lines.</summary>
    event EventHandler<string>? TextReceived;

    /// <summary>Raised when the connection opens (true) or closes (false).</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Raised on a socket/IO error with a human-readable message.</summary>
    event EventHandler<string>? Error;

    /// <summary>Raised when a telnet subnegotiation (e.g. GMCP/MSDP) block is received.</summary>
    event EventHandler<TelnetSubnegotiation>? SubnegotiationReceived;

    Task ConnectAsync(string host, int port, CancellationToken ct = default);

    /// <summary>Sends a command followed by CRLF.</summary>
    Task SendLineAsync(string line, CancellationToken ct = default);

    Task DisconnectAsync();
}

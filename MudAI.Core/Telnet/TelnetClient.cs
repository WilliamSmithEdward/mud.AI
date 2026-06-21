using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;

namespace MudAI.Core.Telnet;

/// <summary>
/// TCP telnet client with a small IAC state machine. We answer negotiations conservatively
/// (refuse almost everything, accept Suppress-Go-Ahead and GMCP/MSDP) so the server sends a
/// clean character stream, and we strip all IAC sequences before raising <see cref="TextReceived"/>.
///
/// Thread-safe: public members may be called from any thread. Sends are serialized by a send
/// lock and connection state is guarded by a state lock. Events are raised on the reader thread,
/// so subscribers must marshal to their own thread as needed.
/// </summary>
public sealed class TelnetClient : ITelnetClient
{
    // MUDs are byte-oriented (Latin-1 / CP437-ish); Latin1 maps every byte 1:1 to a char
    // so we never throw on "invalid" bytes the way UTF-8 decoding would.
    private static readonly Encoding Wire = Encoding.Latin1;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly int _connectTimeoutMs;
    private readonly ILogger<TelnetClient> _logger;

    // Last negotiation response we sent per (verb<<8|option). Touched only on the reader thread.
    private readonly Dictionary<int, byte> _negotiationState = new();
    private bool _gmcpHandshakeSent; // reader-thread only; reset per connection

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public TelnetClient(IOptions<MudAiOptions> options, ILogger<TelnetClient> logger)
    {
        _connectTimeoutMs = options.Value.ConnectTimeoutMs;
        _logger = logger;
    }

    public bool IsConnected => _tcp?.Connected == true;

    public event EventHandler<string>? TextReceived;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<TelnetSubnegotiation>? SubnegotiationReceived;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await DisconnectAsync();
        _negotiationState.Clear();
        _gmcpHandshakeSent = false;

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_connectTimeoutMs > 0) connectCts.CancelAfter(_connectTimeoutMs);
            await tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            _logger.LogWarning("Telnet connect to {Host}:{Port} timed out after {TimeoutMs}ms", host, port, _connectTimeoutMs);
            throw new TimeoutException($"Connection to {host}:{port} timed out after {_connectTimeoutMs}ms.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        TryEnableKeepAlive(tcp);

        lock (_stateLock)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _readCts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));
        }

        _logger.LogInformation("Telnet connected to {Host}:{Port}", host, port);
        ConnectionStateChanged?.Invoke(this, true);
    }

    private static void TryEnableKeepAlive(TcpClient tcp)
    {
        // Best-effort: lets a silently dropped connection surface instead of hanging the reader.
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch { /* not fatal if the platform refuses it */ }
    }

    private enum State { Data, Iac, Negotiate, SubnegOption, Subneg, SubnegIac }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder(buffer.Length);
        var state = State.Data;
        byte verb = 0;
        byte sbOption = 0;
        var sbBuffer = new List<byte>(128);
        var stream = _stream;

        try
        {
            while (!ct.IsCancellationRequested && stream is not null)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (read <= 0) break; // remote closed the connection

                text.Clear();
                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    switch (state)
                    {
                        case State.Data:
                            if (b == TelnetBytes.IAC) state = State.Iac;
                            else text.Append((char)b);
                            break;

                        case State.Iac:
                            if (b == TelnetBytes.IAC) { text.Append((char)b); state = State.Data; } // escaped 0xFF
                            else if (b is TelnetBytes.DO or TelnetBytes.DONT or TelnetBytes.WILL or TelnetBytes.WONT)
                            { verb = b; state = State.Negotiate; }
                            else if (b == TelnetBytes.SB) state = State.SubnegOption;
                            else state = State.Data; // GA / NOP / other single-byte command: ignore
                            break;

                        case State.Negotiate:
                            await RespondAsync(verb, option: b, ct);
                            state = State.Data;
                            break;

                        case State.SubnegOption:
                            sbOption = b;
                            sbBuffer.Clear();
                            state = State.Subneg;
                            break;

                        case State.Subneg:
                            if (b == TelnetBytes.IAC) state = State.SubnegIac;
                            else sbBuffer.Add(b);
                            break;

                        case State.SubnegIac:
                            if (b == TelnetBytes.SE)
                            {
                                SubnegotiationReceived?.Invoke(this,
                                    new TelnetSubnegotiation(sbOption, sbBuffer.ToArray()));
                                state = State.Data;
                            }
                            else if (b == TelnetBytes.IAC)
                            {
                                sbBuffer.Add(TelnetBytes.IAC); // escaped 0xFF inside the payload
                                state = State.Subneg;
                            }
                            else
                            {
                                state = State.Subneg; // malformed; ignore the stray command byte
                            }
                            break;
                    }
                }

                if (text.Length > 0)
                    TextReceived?.Invoke(this, text.ToString());
            }
        }
        catch (OperationCanceledException) { /* normal on disconnect */ }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Telnet read loop ended on error");
                Error?.Invoke(this, ex.Message);
            }
        }
        finally
        {
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Conservative negotiation: we WILL Suppress-Go-Ahead, we accept the server's
    /// ECHO/SGA offers, and we refuse everything else. This is enough for a clean stream.
    /// </summary>
    private async Task RespondAsync(byte verb, byte option, CancellationToken ct)
    {
        byte response;
        switch (verb)
        {
            case TelnetBytes.DO:
                response = option is TelnetBytes.OptSuppressGoAhead or TelnetBytes.OptGmcp or TelnetBytes.OptMsdp
                    ? TelnetBytes.WILL
                    : TelnetBytes.WONT;
                break;
            case TelnetBytes.WILL:
                response = option is TelnetBytes.OptSuppressGoAhead or TelnetBytes.OptEcho
                    or TelnetBytes.OptGmcp or TelnetBytes.OptMsdp
                    ? TelnetBytes.DO
                    : TelnetBytes.DONT;
                break;
            case TelnetBytes.WONT:
                // Peer turned the option off: forget our prior WILL-side answer so a later WILL
                // re-negotiates rather than being suppressed (e.g. the ECHO on/off toggle used
                // for password entry).
                _negotiationState.Remove((TelnetBytes.WILL << 8) | option);
                return;
            case TelnetBytes.DONT:
                _negotiationState.Remove((TelnetBytes.DO << 8) | option);
                return;
            default:
                return;
        }

        // Only reply when our answer for this (request, option) actually changes, to avoid
        // redundant acknowledgements / negotiation ping-pong with a peer.
        int key = (verb << 8) | option;
        if (_negotiationState.TryGetValue(key, out var previous) && previous == response)
            return;

        await SendRawAsync([TelnetBytes.IAC, response, option], ct);
        _negotiationState[key] = response; // record only after a successful send

        // Once we've agreed to GMCP, advertise the packages we care about so the server starts
        // sending structured data (Char.Vitals, Room.Info, ...). Send the handshake at most once
        // per connection, since servers often negotiate GMCP in both directions (WILL + DO).
        if (option == TelnetBytes.OptGmcp && response is TelnetBytes.DO or TelnetBytes.WILL && !_gmcpHandshakeSent)
        {
            _gmcpHandshakeSent = true;
            await SendGmcpHandshakeAsync(ct);
        }
    }

    /// <summary>Wraps a payload in IAC SB &lt;option&gt; ... IAC SE (escaping IAC) and sends it.</summary>
    private Task SendSubnegotiationAsync(byte option, byte[] payload, CancellationToken ct)
    {
        var body = EscapeIac(payload);
        var packet = new byte[body.Length + 5];
        packet[0] = TelnetBytes.IAC;
        packet[1] = TelnetBytes.SB;
        packet[2] = option;
        Array.Copy(body, 0, packet, 3, body.Length);
        packet[^2] = TelnetBytes.IAC;
        packet[^1] = TelnetBytes.SE;
        return SendRawAsync(packet, ct);
    }

    private async Task SendGmcpHandshakeAsync(CancellationToken ct)
    {
        await SendSubnegotiationAsync(TelnetBytes.OptGmcp,
            Wire.GetBytes("Core.Hello { \"client\": \"MudAI\", \"version\": \"1.0\" }"), ct);
        await SendSubnegotiationAsync(TelnetBytes.OptGmcp,
            Wire.GetBytes("Core.Supports.Set [ \"Char 1\", \"Char.Vitals 1\", \"Room 1\", \"Room.Info 1\" ]"), ct);
    }

    private async Task SendRawAsync(byte[] data, CancellationToken ct, bool throwIfClosed = false)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            // Snapshot the stream inside the send lock so DisconnectAsync (which disposes the
            // stream under the same lock) cannot pull it out from under an in-flight write.
            NetworkStream? stream;
            lock (_stateLock) stream = _stream;
            if (stream is null)
            {
                // For user/AI sends, signal "not connected" definitively rather than silently
                // dropping the line. Negotiation sends (default) just no-op.
                if (throwIfClosed) throw new InvalidOperationException("Not connected.");
                return;
            }

            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendLineAsync(string line, CancellationToken ct = default) =>
        SendRawAsync(EscapeIac(Wire.GetBytes(line + "\r\n")), ct, throwIfClosed: true);

    /// <summary>Doubles any literal 0xFF data byte to IAC IAC, per RFC 854, so data can't be
    /// mistaken for a telnet command. Negotiation packets bypass this (they are genuine IACs).</summary>
    private static byte[] EscapeIac(byte[] data)
    {
        int extra = 0;
        foreach (var b in data)
            if (b == TelnetBytes.IAC) extra++;
        if (extra == 0) return data;

        var escaped = new byte[data.Length + extra];
        int k = 0;
        foreach (var b in data)
        {
            escaped[k++] = b;
            if (b == TelnetBytes.IAC) escaped[k++] = TelnetBytes.IAC;
        }
        return escaped;
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        NetworkStream? stream;
        TcpClient? tcp;

        lock (_stateLock)
        {
            cts = _readCts;
            loop = _readLoop;
            stream = _stream;
            tcp = _tcp;
            _readCts = null;
            _readLoop = null;
            _stream = null;
            _tcp = null;
        }

        if (cts is not null)
        {
            try { await cts.CancelAsync(); } catch { /* ignore */ }
        }

        if (loop is not null)
        {
            try { await loop; } catch { /* ignore */ }
        }

        // Dispose the socket under the send lock so we never tear it down mid-write.
        await _sendLock.WaitAsync();
        try
        {
            stream?.Dispose();
            tcp?.Dispose();
        }
        finally
        {
            _sendLock.Release();
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }
}

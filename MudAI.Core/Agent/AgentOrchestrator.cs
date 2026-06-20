using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudAI.Core.Ansi;
using MudAI.Core.Configuration;
using MudAI.Core.GameData;
using MudAI.Core.Llm;
using MudAI.Core.Memory;
using MudAI.Core.Models;
using MudAI.Core.Telnet;

namespace MudAI.Core.Agent;

/// <summary>
/// The central play loop and façade. Owns the connection, feeds telnet output through the
/// ANSI processor into the screen buffer, and—when the master AI toggle is on—repeatedly
/// asks the LLM for the next command, gates it by <see cref="Mode"/>, sends it, observes the
/// outcome, and feeds anti-loop + memory. All UI talks to this one object.
/// </summary>
public sealed class AgentOrchestrator : IAsyncDisposable
{
    private readonly ITelnetClient _telnet;
    private readonly ILlmClient _llm;
    private readonly IMemoryStore _memory;
    private readonly ICommandTracker _tracker;
    private readonly IContextBuilder _contextBuilder;
    private readonly MudOutputProcessor _processor;
    private readonly ScreenBuffer _screen;
    private readonly LoginManager _loginManager;
    private readonly MudAiOptions _options;
    private readonly ILogger<AgentOrchestrator> _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private long _lastOutputTicks;
    private long _maskEchoUntilTicks; // while > now, mask the password in echoed text

    // Serializes ConnectAsync/DisconnectAsync so a reconnect can't interleave with an in-flight
    // disconnect and leave a stale loop behind.
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private readonly object _approvalGate = new();
    private TaskCompletionSource<ApprovalResult>? _pendingApproval;

    private readonly object _gameStateLock = new();
    private GameState _gameState = new();

    private volatile bool _aiEnabled;
    private volatile AutonomyMode _mode;
    private volatile string _goal = "";
    private volatile string _steering = "";

    public AgentOrchestrator(
        ITelnetClient telnet,
        ILlmClient llm,
        IMemoryStore memory,
        ICommandTracker tracker,
        IContextBuilder contextBuilder,
        MudOutputProcessor processor,
        ScreenBuffer screen,
        LoginManager loginManager,
        IOptions<MudAiOptions> options,
        ILogger<AgentOrchestrator> logger)
    {
        _telnet = telnet;
        _llm = llm;
        _memory = memory;
        _tracker = tracker;
        _contextBuilder = contextBuilder;
        _processor = processor;
        _screen = screen;
        _loginManager = loginManager;
        _options = options.Value;
        _logger = logger;

        _aiEnabled = _options.AiEnabledOnStart;
        _mode = _options.DefaultAutonomyMode;
        _lastOutputTicks = Stopwatch.GetTimestamp();

        _processor.LineCompleted += OnLineCompleted;
        _processor.PromptChanged += OnPromptChanged;
        _telnet.TextReceived += OnTextReceived;
        _telnet.ConnectionStateChanged += OnConnectionStateChanged;
        _telnet.Error += OnTelnetError;
        _telnet.SubnegotiationReceived += OnSubnegotiation;
    }

    // --- public control surface (bound by the UI) ---

    public bool IsConnected => _telnet.IsConnected;

    /// <summary>Master toggle: when false the loop idles and sends nothing of its own.</summary>
    public bool AiEnabled { get => _aiEnabled; set => _aiEnabled = value; }

    public AutonomyMode Mode { get => _mode; set => _mode = value; }

    public string Goal { get => _goal; set => _goal = value ?? ""; }

    /// <summary>Live human guidance injected into every turn until changed/cleared.</summary>
    public string Steering { get => _steering; set => _steering = value ?? ""; }

    /// <summary>Auto-login: run the configured expect/send script on connect.</summary>
    public bool AutoLogin { get => _loginManager.Enabled; set => _loginManager.Enabled = value; }
    public string LoginUsername { get => _loginManager.Username; set => _loginManager.Username = value ?? ""; }
    public string LoginPassword { get => _loginManager.Password; set => _loginManager.Password = value ?? ""; }

    // --- events (raised from background threads; UI marshals to the dispatcher) ---

    public event EventHandler<MudMessage>? MudOutput;
    public event EventHandler<MudMessage>? PromptOutput;
    public event EventHandler<string>? Reasoning;
    public event EventHandler<AgentDecision>? Decision;
    public event EventHandler<string>? Status;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<AgentDecision>? ApprovalRequested;

    /// <summary>Live structured state (HP/MP/room/exits) assembled from GMCP/MSDP.</summary>
    public event EventHandler<GameState>? GameStateChanged;

    /// <summary>Raised when a new streamed completion begins (UI should clear the live buffer).</summary>
    public event EventHandler? StreamingStarted;

    /// <summary>Raised for each streamed token/delta of the model's output.</summary>
    public event EventHandler<string>? StreamingDelta;

    public GameState GameState { get { lock (_gameStateLock) return _gameState; } }

    // --- connection ---

    public Task ConnectAsync(CancellationToken ct = default) =>
        ConnectAsync(_options.MudHost, _options.MudPort, ct);

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _connectionGate.WaitAsync(ct);
        try
        {
            if (IsConnected) return;

            // Always tear down any prior loop first (e.g. one left idling after the server
            // dropped the connection) so the loop's lifetime is tied 1:1 to a connection.
            await StopLoopAsync();

            _processor.Reset();
            _screen.Clear();
            _tracker.Reset();
            _loginManager.Reset();
            lock (_gameStateLock) _gameState = new GameState();
            GameStateChanged?.Invoke(this, GameState);
            Touch();

            await _telnet.ConnectAsync(host, port, ct);
            StartLoop();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await StopLoopAsync();
            await _telnet.DisconnectAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void StartLoop()
    {
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token));
    }

    /// <summary>Cancels and awaits the current play loop, if any. Caller holds <see cref="_connectionGate"/>.</summary>
    private async Task StopLoopAsync()
    {
        var cts = _loopCts;
        var loop = _loopTask;
        _loopCts = null;
        _loopTask = null;

        if (cts is not null)
        {
            try { await cts.CancelAsync(); } catch { /* ignore */ }
        }

        CancelPendingApproval();

        if (loop is not null)
        {
            try { await loop; } catch { /* ignore */ }
        }

        cts?.Dispose();
    }

    // --- sending ---

    /// <summary>Sends a command the human typed (no anti-loop suppression applied to manual input).</summary>
    public Task SendManualAsync(string text) => SendInternalAsync(text, manual: true, CancellationToken.None);

    private async Task SendInternalAsync(string command, bool manual, CancellationToken ct, string? displayEcho = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!IsConnected)
        {
            Status?.Invoke(this, "Not connected.");
            return;
        }

        string echo = displayEcho ?? "> " + command;
        _screen.AppendLine(echo);
        MudOutput?.Invoke(this, MudMessage.Outgoing(echo));

        if (!manual) _tracker.RecordSent(command);

        // Note: we intentionally do NOT Touch() here. The settle/quiet clock must track SERVER
        // output only, so our own echo doesn't make a slow reply look like it already settled.
        await _telnet.SendLineAsync(command, ct);
    }

    // --- approval (proposal / hybrid modes) ---

    public void ResolveApproval(bool approved, string command)
    {
        lock (_approvalGate)
            _pendingApproval?.TrySetResult(new ApprovalResult(approved, command));
    }

    private Task<ApprovalResult> RequestApprovalAsync(AgentDecision decision, CancellationToken ct)
    {
        TaskCompletionSource<ApprovalResult> tcs;
        lock (_approvalGate)
        {
            _pendingApproval?.TrySetResult(new ApprovalResult(false, "")); // supersede any stale request
            tcs = new TaskCompletionSource<ApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingApproval = tcs;
        }

        ApprovalRequested?.Invoke(this, decision);
        return AwaitApprovalAsync(tcs, ct);
    }

    private async Task<ApprovalResult> AwaitApprovalAsync(TaskCompletionSource<ApprovalResult> tcs, CancellationToken ct)
    {
        using var reg = ct.Register(() => tcs.TrySetResult(new ApprovalResult(false, "")));
        var result = await tcs.Task;
        lock (_approvalGate)
            if (ReferenceEquals(_pendingApproval, tcs)) _pendingApproval = null;
        return result;
    }

    private void CancelPendingApproval()
    {
        lock (_approvalGate)
        {
            _pendingApproval?.TrySetResult(new ApprovalResult(false, ""));
            _pendingApproval = null;
        }
    }

    // --- the loop ---

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Status?.Invoke(this, $"Agent loop started (AI {(AiEnabled ? "ON" : "OFF")}).");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!IsConnected) { await Task.Delay(300, ct); continue; }
                    if (!AiEnabled) { await Task.Delay(200, ct); continue; }
                    // Don't let the AI act while auto-login is still running — its sends would
                    // interleave with the login script and corrupt the reply window.
                    if (_loginManager.LoginInProgress) { await Task.Delay(300, ct); continue; }

                    await WaitForSettleAsync(ct, afterSend: false);
                    if (ct.IsCancellationRequested || !AiEnabled || !IsConnected) continue;

                    Status?.Invoke(this, "Thinking…");
                    var messages = _contextBuilder.Build(await BuildInputAsync(ct));
                    string raw = await GetCompletionAsync(messages, ct);
                    var decision = DecisionParser.Parse(raw);

                    if (!string.IsNullOrWhiteSpace(decision.Reasoning))
                        Reasoning?.Invoke(this, decision.Reasoning);
                    Decision?.Invoke(this, decision);

                    if (!string.IsNullOrWhiteSpace(decision.Goal))
                        Goal = decision.Goal!;
                    if (!string.IsNullOrWhiteSpace(decision.Lesson))
                        await _memory.AddOrReinforceLessonAsync(decision.Lesson!, "agent", 0.6, ct);

                    if (decision.Wait || !decision.HasCommand)
                    {
                        Status?.Invoke(this, "Observing (no command).");
                        await Task.Delay(_options.MinTurnDelayMs, ct);
                        continue;
                    }

                    if (_tracker.ShouldSuppress(decision.Command))
                    {
                        Status?.Invoke(this, $"Blocked looping command: \"{decision.Command}\".");
                        await Task.Delay(_options.MinTurnDelayMs, ct);
                        continue;
                    }

                    bool autoSend = Mode switch
                    {
                        AutonomyMode.FullAuto => true,
                        AutonomyMode.AutoWithInterrupt => true,
                        AutonomyMode.HybridByRisk => decision.Risk == RiskLevel.Low,
                        AutonomyMode.Proposal => false,
                        _ => false
                    };

                    string commandToSend = decision.Command;
                    if (!autoSend)
                    {
                        Status?.Invoke(this, "Awaiting your approval…");
                        var verdict = await RequestApprovalAsync(decision, ct);
                        if (!verdict.Approved || string.IsNullOrWhiteSpace(verdict.Command))
                        {
                            Status?.Invoke(this, "Proposal rejected.");
                            await Task.Delay(_options.MinTurnDelayMs, ct);
                            continue;
                        }
                        commandToSend = verdict.Command;
                    }

                    if (ct.IsCancellationRequested || !AiEnabled || !IsConnected) continue;

                    await SendInternalAsync(commandToSend, manual: false, ct);
                    long mark = _screen.Head;

                    await WaitForReplyAsync(mark, ct);

                    string response = _screen.GetTextSince(mark);
                    var outcome = _tracker.ClassifyAndRecord(commandToSend, response);
                    await _memory.RecordCommandResultAsync(
                        FirstWord(commandToSend), outcome.Kind == OutcomeKind.Success, ct);
                    Status?.Invoke(this, $"\"{commandToSend}\" → {outcome.Kind}");

                    await Task.Delay(_options.MinTurnDelayMs, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Agent loop iteration failed");
                    Status?.Invoke(this, $"Loop error: {ex.Message}");
                    try { await Task.Delay(1500, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            Status?.Invoke(this, "Agent loop stopped.");
        }
    }

    /// <summary>Runs the completion, streaming token-by-token when enabled, and returns the full text.</summary>
    private async Task<string> GetCompletionAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (!_options.StreamResponses)
            return await _llm.CompleteAsync(messages, ct);

        StreamingStarted?.Invoke(this, EventArgs.Empty);
        var sb = new StringBuilder();
        await foreach (var delta in _llm.CompleteStreamAsync(messages, ct))
        {
            sb.Append(delta);
            StreamingDelta?.Invoke(this, delta);
        }
        return sb.ToString();
    }

    private async Task<AgentContextInput> BuildInputAsync(CancellationToken ct)
    {
        var lessons = await _memory.GetTopLessonsAsync(12, ct);
        var commands = await _memory.GetCommandKnowledgeAsync(15, ct);
        return new AgentContextInput
        {
            RecentScreen = _screen.GetRecentText(_options.MaxRecentLines),
            Goal = Goal,
            Steering = Steering,
            FailureContext = _tracker.GetFailureContext(),
            Suppressed = _tracker.GetActiveSuppressions(),
            Lessons = lessons,
            Commands = commands,
            GameStateSummary = GameState.ToSummary(),
            Mode = Mode
        };
    }

    /// <summary>Waits until MUD output has been quiet for <see cref="MudAiOptions.OutputSettleMs"/> (bounded).</summary>
    private async Task WaitForSettleAsync(CancellationToken ct, bool afterSend)
    {
        int settle = _options.OutputSettleMs;
        int maxWait = afterSend ? 4000 : 8000;
        long start = Stopwatch.GetTimestamp();

        await Task.Delay(afterSend ? Math.Min(settle, 350) : 120, ct);

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            if (ElapsedMs(Volatile.Read(ref _lastOutputTicks), now) >= settle) return;
            if (ElapsedMs(start, now) >= maxWait) return;
            await Task.Delay(80, ct);
        }
    }

    /// <summary>
    /// Waits for the first reply line to appear after <paramref name="mark"/> (up to
    /// <see cref="MudAiOptions.ResponseTimeoutMs"/>), then for output to settle. If no reply
    /// arrives, returns so the command is classified as no-effect rather than a false success.
    /// </summary>
    private async Task WaitForReplyAsync(long mark, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            if (_screen.Head > mark) break; // a reply line arrived
            if (ElapsedMs(start, Stopwatch.GetTimestamp()) >= _options.ResponseTimeoutMs) return;
            await Task.Delay(60, ct);
        }

        await WaitForSettleAsync(ct, afterSend: true);
    }

    // --- telnet/processor event handlers (run on the reader thread) ---

    private void OnTextReceived(object? sender, string chunk) => _processor.Ingest(chunk);

    private void OnLineCompleted(object? sender, MudMessage m)
    {
        m = MaskSecret(m);
        _screen.AppendLine(m.PlainText);
        Touch();
        MudOutput?.Invoke(this, m);
        TryLogin(m.PlainText);
    }

    private void OnPromptChanged(object? sender, MudMessage m)
    {
        m = MaskSecret(m);
        _screen.SetPrompt(m.PlainText);
        Touch();
        PromptOutput?.Invoke(this, m);
        TryLogin(m.PlainText);
    }

    /// <summary>
    /// Briefly after sending a password, mask any echo of it so the cleartext password never
    /// lands in the transcript, the prompt display, or (most importantly) the LLM context.
    /// </summary>
    private MudMessage MaskSecret(MudMessage m)
    {
        if (Stopwatch.GetTimestamp() >= Volatile.Read(ref _maskEchoUntilTicks)) return m;

        string pw = _loginManager.Password;
        if (string.IsNullOrEmpty(pw) || !m.PlainText.Contains(pw, StringComparison.Ordinal)) return m;

        string masked = m.PlainText.Replace(pw, "********");
        return new MudMessage
        {
            Direction = m.Direction,
            PlainText = masked,
            Segments = [new AnsiSegment(masked, AnsiColor.Default, AnsiColor.Default, false, false, false)]
        };
    }

    private void TryLogin(string text)
    {
        var action = _loginManager.OnLine(text);
        if (action is null) return;
        _ = SendLoginAsync(action.Value);
    }

    private async Task SendLoginAsync(LoginAction action)
    {
        try
        {
            if (action.Secret)
            {
                // Mask any echo of the password for a short window after sending it.
                long window = Stopwatch.GetTimestamp() + (long)(1500.0 * Stopwatch.Frequency / 1000.0);
                Volatile.Write(ref _maskEchoUntilTicks, window);
            }

            string display = action.Secret ? "> ********" : "> " + action.Command;
            await SendInternalAsync(action.Command, manual: true, CancellationToken.None, displayEcho: display);
            Status?.Invoke(this, "Auto-login: sent " + (action.Secret ? "password" : action.Command));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-login send failed");
        }
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        ConnectionStateChanged?.Invoke(this, connected);
        Status?.Invoke(this, connected ? "Connected." : "Disconnected.");
    }

    private void OnTelnetError(object? sender, string message) =>
        Status?.Invoke(this, "Network error: " + message);

    private void OnSubnegotiation(object? sender, TelnetSubnegotiation sub)
    {
        GameState updated;
        if (sub.Option == TelnetBytes.OptGmcp)
        {
            var msg = GmcpParser.Parse(Encoding.UTF8.GetString(sub.Payload));
            lock (_gameStateLock) { _gameState = GameStateMapper.ApplyGmcp(_gameState, msg); updated = _gameState; }
        }
        else if (sub.Option == TelnetBytes.OptMsdp)
        {
            var dict = MsdpParser.Parse(sub.Payload);
            lock (_gameStateLock) { _gameState = GameStateMapper.ApplyMsdp(_gameState, dict); updated = _gameState; }
        }
        else
        {
            return; // other subnegotiations (e.g. TTYPE) are ignored
        }

        GameStateChanged?.Invoke(this, updated);
    }

    // --- helpers ---

    private void Touch() => Volatile.Write(ref _lastOutputTicks, Stopwatch.GetTimestamp());

    private static double ElapsedMs(long fromTicks, long toTicks) =>
        (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;

    private static string FirstWord(string command)
    {
        var trimmed = command.Trim();
        int space = trimmed.IndexOf(' ');
        return (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
    }

    public Task<bool> PingLlmAsync(CancellationToken ct = default) => _llm.PingAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _processor.LineCompleted -= OnLineCompleted;
        _processor.PromptChanged -= OnPromptChanged;
        _telnet.TextReceived -= OnTextReceived;
        _telnet.ConnectionStateChanged -= OnConnectionStateChanged;
        _telnet.Error -= OnTelnetError;
        _telnet.SubnegotiationReceived -= OnSubnegotiation;

        await DisconnectAsync();
        _connectionGate.Dispose();
    }
}

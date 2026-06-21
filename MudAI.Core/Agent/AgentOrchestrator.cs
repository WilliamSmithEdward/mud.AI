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
/// The central play loop and facade. Owns the connection, feeds telnet output through the
/// ANSI processor into the screen buffer, and (when the master AI toggle is on) repeatedly
/// asks the LLM for the next command, gates it by <see cref="Mode"/>, sends it, observes the
/// outcome, and feeds anti-loop + memory. All UI talks to this one object.
///
/// Thread-safe: public members may be called from any thread. The play loop runs on a background
/// task; events (MudOutput, Reasoning, Decision, Status, ...) are raised off the UI thread, so the
/// UI must marshal them to the dispatcher. Connect/Disconnect are serialized by a connection gate,
/// game state is guarded by its own lock, and the approval handshake by another.
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
    private volatile bool _passwordSent; // once true, mask any echo of the login password for the session

    // Counts server lines (not our own echoes) so we can tell the model when the MUD is idle.
    private long _serverLineSeq;
    private long _lastTurnLineSeq;

    // Number of awareness facts written this process; drives the periodic markdown dump.
    private long _awarenessWrites;
    // Serializes awareness.md exports so the periodic and end-of-session writes never overlap.
    private readonly SemaphoreSlim _exportGate = new(1, 1);

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

    // Room mapping: the last room we were in and the single movement command awaiting its result.
    private volatile string? _lastRoom;
    private volatile string? _pendingMovement;

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

    /// <summary>Raised true while the agent is generating a turn, false when idle (drives a busy indicator).</summary>
    public event EventHandler<bool>? BusyChanged;

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
            _passwordSent = false;
            _lastRoom = null;
            _pendingMovement = null;
            Interlocked.Exchange(ref _serverLineSeq, 0);
            _lastTurnLineSeq = 0;
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
            await ExportAwarenessSafeAsync(); // end-of-session dump while the store is still alive
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
    public Task SendManualAsync(string text) => SendCommandsAsync(text, manual: true, CancellationToken.None);

    /// <summary>
    /// Splits on ';' and sends each command in order (client-side multi-command). A single
    /// movement command is remembered so the resulting room change can be mapped.
    /// </summary>
    private async Task SendCommandsAsync(string text, bool manual, CancellationToken ct)
    {
        var commands = CommandSplitter.Split(text);
        if (commands.Count == 0) return;

        // Only track an unambiguous single move; a multi-command batch can't be mapped to one edge.
        _pendingMovement = commands.Count == 1 && IsMovement(commands[0]) ? Normalize(commands[0]) : null;

        foreach (var command in commands)
            await SendInternalAsync(command, manual, ct);
    }

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
                bool busy = false;
                try
                {
                    if (!IsConnected) { await Task.Delay(300, ct); continue; }
                    if (!AiEnabled) { await Task.Delay(200, ct); continue; }
                    // Don't let the AI act while auto-login is still running, since its sends would
                    // interleave with the login script and corrupt the reply window.
                    if (_loginManager.LoginInProgress) { await Task.Delay(300, ct); continue; }

                    await WaitForSettleAsync(ct, afterSend: false);
                    if (ct.IsCancellationRequested || !AiEnabled || !IsConnected) continue;

                    busy = true;
                    BusyChanged?.Invoke(this, true);
                    Status?.Invoke(this, "Thinking...");
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
                    if (decision.Awareness is { } note)
                    {
                        await _memory.AddOrReinforceAwarenessAsync(note.Category, note.Subject, note.Fact, 0.6, ct);
                        if (Interlocked.Increment(ref _awarenessWrites) % Math.Max(1, _options.AwarenessExportEveryNWrites) == 0)
                            _ = ExportAwarenessSafeAsync(waitForGate: false);
                    }

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
                        Status?.Invoke(this, "Awaiting your approval...");
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

                    await SendCommandsAsync(commandToSend, manual: false, ct);
                    long mark = _screen.Head;

                    await WaitForReplyAsync(mark, ct);

                    string response = _screen.GetTextSince(mark);
                    var outcome = _tracker.ClassifyAndRecord(commandToSend, response);
                    bool success = outcome.Kind == OutcomeKind.Success;
                    foreach (var part in CommandSplitter.Split(commandToSend))
                        await _memory.RecordCommandResultAsync(FirstWord(part), success, ct);
                    Status?.Invoke(this, $"\"{commandToSend}\" -> {outcome.Kind}");

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
                finally
                {
                    if (busy) BusyChanged?.Invoke(this, false);
                }
            }
        }
        finally
        {
            Status?.Invoke(this, "Agent loop stopped.");
        }
    }

    /// <summary>Runs the completion, retrying once if the model returns nothing (a wasted turn).</summary>
    private async Task<string> GetCompletionAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        string raw = await CompleteOnceAsync(messages, ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("Empty model response; retrying once.");
            raw = await CompleteOnceAsync(messages, ct);
        }
        return raw;
    }

    /// <summary>One completion. When streaming, stops as soon as the decision JSON is fully formed
    /// so we send the command without waiting for trailing tokens the model might still generate.</summary>
    private async Task<string> CompleteOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (!_options.StreamResponses)
            return await _llm.CompleteAsync(messages, ct);

        // Early-stop is only safe when the server guarantees pure JSON (response_format): then the
        // first '{' is the real decision object. Without it, a stray brace in prose could stop us
        // short, so we let the whole stream run.
        bool canEarlyStop = _options.UseJsonResponseFormat;

        StreamingStarted?.Invoke(this, EventArgs.Empty);
        var sb = new StringBuilder();
        await foreach (var delta in _llm.CompleteStreamAsync(messages, ct))
        {
            sb.Append(delta);
            StreamingDelta?.Invoke(this, delta);
            // Once a complete top-level JSON object has arrived, the decision is in hand; closing the
            // stream early stops further server-side decoding (the bulk of per-turn latency).
            if (canEarlyStop && delta.Contains('}') && DecisionParser.ContainsBalancedObject(sb.ToString()))
                break;
        }
        return sb.ToString();
    }

    private async Task<AgentContextInput> BuildInputAsync(CancellationToken ct)
    {
        var lessons = await _memory.GetTopLessonsAsync(6, ct);
        var commands = await _memory.GetCommandKnowledgeAsync(10, ct);
        var awareness = await _memory.GetBalancedAwarenessAsync(_options.AwarenessRecallPerCategory, ct);
        string mapRecall = await BuildMapRecallAsync(ct);

        long lineSeq = Interlocked.Read(ref _serverLineSeq);
        bool noNewOutput = lineSeq == _lastTurnLineSeq;
        _lastTurnLineSeq = lineSeq;

        return new AgentContextInput
        {
            RecentScreen = _screen.GetRecentText(_options.MaxRecentLines),
            Goal = Goal,
            Steering = Steering,
            FailureContext = _tracker.GetFailureContext(),
            Suppressed = _tracker.GetActiveSuppressions(),
            Lessons = lessons,
            Commands = commands,
            Awareness = awareness,
            GameStateSummary = GameState.ToSummary(),
            MapRecall = mapRecall,
            NoNewOutput = noNewOutput,
            Mode = Mode
        };
    }

    /// <summary>Compact, token-efficient map recall: the current room's known exits plus the map size.</summary>
    private async Task<string> BuildMapRecallAsync(CancellationToken ct)
    {
        var gs = GameState;
        string recall = "";
        if (!string.IsNullOrWhiteSpace(gs.RoomName))
        {
            var room = await _memory.GetRoomRecallAsync(gs.RoomName!, ct);
            if (room is not null) recall = room.ToSummary();
        }

        int rooms = await _memory.CountAsync("rooms", ct);
        if (rooms > 0)
            recall = recall.Length == 0 ? $"Rooms mapped: {rooms}." : $"{recall} Rooms mapped: {rooms}.";

        var zones = await _memory.GetZoneAwarenessAsync(5, ct);
        string zoneLine = zones.ToLine();
        if (zoneLine.Length > 0)
            recall = recall.Length == 0 ? zoneLine : $"{recall} {zoneLine}";

        return recall;
    }

    /// <summary>
    /// Regenerates the human-readable awareness.md dump from the DB. Serialized by a gate so the
    /// periodic and end-of-session exports never overlap. The periodic (fire-and-forget) caller
    /// passes <paramref name="waitForGate"/>=false to coalesce: if an export is already running it
    /// skips, since that running export will already include its writes. The disconnect caller
    /// waits so the final dump reflects everything.
    /// </summary>
    private async Task ExportAwarenessSafeAsync(bool waitForGate = true)
    {
        if (waitForGate)
            await _exportGate.WaitAsync();
        else if (!await _exportGate.WaitAsync(0))
            return; // an export is already in flight; this periodic one would be redundant

        try
        {
            var entries = await _memory.GetAllAwarenessAsync();
            var commands = await _memory.GetCommandKnowledgeAsync(20);
            int rooms = await _memory.CountAsync("rooms");
            await AwarenessExporter.ExportAsync(ResolveAwarenessPath(), entries, commands, rooms);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Awareness export failed");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private string ResolveAwarenessPath()
    {
        var configured = _options.AwarenessExportPath;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MudAI");
        return Path.Combine(dir, "awareness.md");
    }

    /// <summary>Waits until MUD output has been quiet for <see cref="MudAiOptions.OutputSettleMs"/> (bounded).</summary>
    private async Task WaitForSettleAsync(CancellationToken ct, bool afterSend)
    {
        int settle = _options.OutputSettleMs;
        int maxWait = afterSend ? 4000 : 8000;
        long start = Stopwatch.GetTimestamp();

        // If output has already been quiet long enough, pay no settle delay at all.
        if (ElapsedMs(Volatile.Read(ref _lastOutputTicks), Stopwatch.GetTimestamp()) >= settle)
            return;

        await Task.Delay(afterSend ? Math.Min(settle, 250) : 80, ct);

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            if (ElapsedMs(Volatile.Read(ref _lastOutputTicks), now) >= settle) return;
            if (ElapsedMs(start, now) >= maxWait) return;
            await Task.Delay(40, ct);
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
            await Task.Delay(30, ct);
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
        Interlocked.Increment(ref _serverLineSeq);
        MudOutput?.Invoke(this, m);
        TryLogin(m.PlainText);
    }

    private void OnPromptChanged(object? sender, MudMessage m)
    {
        m = MaskSecret(m);
        _screen.SetPrompt(m.PlainText);
        // Deliberately NOT Touch()ing here: many MUDs continuously redraw a status prompt
        // (e.g. "< hp mp mv >"), and counting that as fresh output would keep re-arming the
        // settle/quiet clock and stall every turn. The quiet clock tracks real line output only.
        PromptOutput?.Invoke(this, m);
        TryLogin(m.PlainText);
    }

    /// <summary>
    /// Once the login password has been sent, mask any echo of it so the cleartext password never
    /// lands in the transcript, the prompt display, or (most importantly) the LLM context. This is
    /// unconditional for the rest of the session, not time-windowed, so a late echo cannot leak.
    /// </summary>
    private MudMessage MaskSecret(MudMessage m)
    {
        if (!_passwordSent) return m;

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
                // From now on, mask any echo of the password in output that reaches the UI or the LLM.
                _passwordSent = true;
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
        TrackRoom(updated);
    }

    /// <summary>Persists the current room and, if a single move led here, the edge that connects them.</summary>
    private void TrackRoom(GameState state)
    {
        string? room = string.IsNullOrWhiteSpace(state.RoomName) ? null : state.RoomName;
        if (room is null || room == _lastRoom) return;

        string? from = _lastRoom;
        string? move = _pendingMovement;
        _lastRoom = room;
        _pendingMovement = null;

        _ = RecordRoomAsync(from, move, room, state.Zone ?? "", state.Exits ?? "");
    }

    private async Task RecordRoomAsync(string? from, string? move, string room, string zone, string exits)
    {
        try
        {
            await _memory.RecordRoomVisitAsync(room, zone, exits);
            if (from is not null && move is not null)
                await _memory.RecordExitAsync(from, move, room);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Room mapping write failed");
        }
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

    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "s", "e", "w", "u", "d", "ne", "nw", "se", "sw",
        "north", "south", "east", "west", "up", "down",
        "northeast", "northwest", "southeast", "southwest"
    };

    private static bool IsMovement(string command) => Directions.Contains(command.Trim());

    private static string Normalize(string command) => command.Trim().ToLowerInvariant();

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
        _exportGate.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudAI.Core.Agent;
using MudAI.Core.Configuration;
using MudAI.Core.Memory;
using MudAI.Core.Models;

namespace MudAI.App.ViewModels;

/// <summary>
/// The single view-model behind the whole window. It is the only place that touches the
/// orchestrator; it marshals the orchestrator's background-thread events onto the UI
/// dispatcher and exposes bindable state + commands.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 300;

    private readonly AgentOrchestrator _orchestrator;
    private readonly ICommandTracker _tracker;
    private readonly IMemoryStore _memory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private bool _applyingFromAgent; // guards the Goal feedback loop

    // Busy state: the agent's "thinking" phase OR an in-flight command (connect/ping) shows the bar.
    private bool _agentBusy;
    private int _commandBusy;

    // Streamed deltas are accumulated here and flushed to LiveResponse on a timer, so a 1000-token
    // stream produces ~12 UI updates/sec instead of 1000 PropertyChanged posts + O(n^2) concatenation.
    private readonly StringBuilder _liveBuffer = new();
    private readonly DispatcherTimer _liveFlushTimer;
    private bool _liveDirty;

    /// <summary>Raised on the UI thread when a new MUD line should be rendered in the terminal.</summary>
    public event Action<MudMessage>? MessageReceived;

    /// <summary>Raised on the UI thread when the transcript should be cleared.</summary>
    public event Action? TranscriptCleared;

    /// <summary>Raised on the UI thread with the live prompt (carrying its ANSI colour segments).</summary>
    public event Action<MudMessage>? PromptChanged;

    public MainViewModel(
        AgentOrchestrator orchestrator,
        ICommandTracker tracker,
        IMemoryStore memory,
        IOptions<MudAiOptions> options,
        ILogger<MainViewModel> logger)
    {
        _orchestrator = orchestrator;
        _tracker = tracker;
        _memory = memory;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var o = options.Value;
        _host = o.MudHost;
        _port = o.MudPort;
        _aiEnabled = orchestrator.AiEnabled;
        _selectedMode = orchestrator.Mode;
        _goal = orchestrator.Goal;
        _autoLogin = orchestrator.AutoLogin;
        _loginUsername = orchestrator.LoginUsername;

        _orchestrator.MudOutput += (_, m) => OnUi(() => MessageReceived?.Invoke(m));
        _orchestrator.PromptOutput += (_, m) => OnUi(() =>
        {
            CurrentPrompt = m.PlainText; // plain text for accessibility / screen readers
            PromptChanged?.Invoke(m);    // coloured rendering in the code-behind
        });
        _orchestrator.Reasoning += (_, r) => OnUi(() => AppendReasoning(r));
        _orchestrator.Decision += (_, d) => OnUi(() => OnDecision(d));
        _orchestrator.Status += (_, s) => OnUi(() => StatusMessage = s);
        _orchestrator.ConnectionStateChanged += (_, c) => OnUi(() => OnConnectionChanged(c));
        _orchestrator.ApprovalRequested += (_, d) => OnUi(() => OnApprovalRequested(d));
        _orchestrator.GameStateChanged += (_, s) => OnUi(() =>
            GameStateText = s.HasAny ? s.ToSummary() : "(no structured data; server may not support GMCP/MSDP)");
        _orchestrator.StreamingStarted += (_, _) => OnUi(ResetLiveBuffer);
        _orchestrator.StreamingDelta += (_, d) => OnUi(() => { _liveBuffer.Append(d); _liveDirty = true; });
        _orchestrator.BusyChanged += (_, b) => OnUi(() => { _agentBusy = b; UpdateBusy(); });

        _liveFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _liveFlushTimer.Tick += (_, _) =>
        {
            if (!_liveDirty) return;
            LiveResponse = _liveBuffer.ToString();
            _liveDirty = false;
        };
        _liveFlushTimer.Start();

        _ = RefreshMemoryStatsAsync();
    }

    private void ResetLiveBuffer()
    {
        _liveBuffer.Clear();
        _liveDirty = false;
        LiveResponse = "";
    }

    // --- connection ---

    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private string _llmStatus = "LM Studio: not checked";
    [ObservableProperty] private string _statusMessage = "Ready.";

    /// <summary>True while the agent is thinking or a connect/ping command is in flight.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>True once any MUD output has been rendered, so the terminal empty state can hide.</summary>
    [ObservableProperty] private bool _hasTerminalContent;

    private void UpdateBusy() => IsBusy = _agentBusy || _commandBusy > 0;

    // --- AI control ---

    [ObservableProperty] private bool _aiEnabled;
    [ObservableProperty] private AutonomyMode _selectedMode;
    [ObservableProperty] private string _goal;
    [ObservableProperty] private string _steering = "";

    public IReadOnlyList<AutonomyMode> AutonomyModes { get; } = Enum.GetValues<AutonomyMode>();

    // --- auto-login (password is set via code-behind from a PasswordBox, never bound) ---

    [ObservableProperty] private bool _autoLogin;
    [ObservableProperty] private string _loginUsername;

    public string InitialLoginPassword => _orchestrator.LoginPassword;
    public void SetLoginPassword(string password) => _orchestrator.LoginPassword = password ?? "";

    // --- terminal / input ---

    [ObservableProperty] private string _manualCommand = "";
    [ObservableProperty] private string _currentPrompt = "";
    [ObservableProperty] private string _liveResponse = "";

    // --- panels ---

    public ObservableCollection<string> ReasoningLog { get; } = [];
    public ObservableCollection<string> Suppressions { get; } = [];
    [ObservableProperty] private string _memoryStats = "Memory: -";
    [ObservableProperty] private string _gameStateText = "(not connected)";

    // --- approval (proposal / hybrid) ---

    [ObservableProperty] private bool _hasPendingApproval;
    [ObservableProperty] private string _pendingCommand = "";
    [ObservableProperty] private string _pendingReasoning = "";
    [ObservableProperty] private string _pendingRisk = "";

    // --- bound-property change hooks ---

    partial void OnAiEnabledChanged(bool value)
    {
        _orchestrator.AiEnabled = value;
        StatusMessage = value ? "AI control ENABLED." : "AI control disabled.";
    }

    partial void OnSelectedModeChanged(AutonomyMode value) => _orchestrator.Mode = value;

    partial void OnGoalChanged(string value)
    {
        if (!_applyingFromAgent) _orchestrator.Goal = value ?? "";
    }

    partial void OnSteeringChanged(string value) => _orchestrator.Steering = value ?? "";

    partial void OnAutoLoginChanged(bool value) => _orchestrator.AutoLogin = value;

    partial void OnLoginUsernameChanged(string value) => _orchestrator.LoginUsername = value ?? "";

    // --- commands ---

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        _commandBusy++;
        UpdateBusy();
        try
        {
            StatusMessage = $"Connecting to {Host}:{Port}...";
            await _orchestrator.ConnectAsync(Host, Port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connect to {Host}:{Port} failed", Host, Port);
            StatusMessage = "Connect failed: " + ex.Message;
        }
        finally
        {
            _commandBusy--;
            UpdateBusy();
        }
    }

    private bool CanConnect() => !IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try { await _orchestrator.DisconnectAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disconnect failed");
            StatusMessage = "Disconnect error: " + ex.Message;
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand]
    private async Task SendManualAsync()
    {
        var text = ManualCommand;
        ManualCommand = "";
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await _orchestrator.SendManualAsync(text); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual send failed");
            StatusMessage = "Send failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Approve()
    {
        _orchestrator.ResolveApproval(true, PendingCommand);
        HasPendingApproval = false;
    }

    [RelayCommand]
    private void Reject()
    {
        _orchestrator.ResolveApproval(false, PendingCommand);
        HasPendingApproval = false;
    }

    [RelayCommand]
    private void ClearSteering() => Steering = "";

    [RelayCommand]
    private void ClearTranscript()
    {
        TranscriptCleared?.Invoke();
        HasTerminalContent = false;
    }

    [RelayCommand]
    private async Task PingLlmAsync()
    {
        _commandBusy++;
        UpdateBusy();
        LlmStatus = "LM Studio: checking...";
        try
        {
            bool ok = await _orchestrator.PingLlmAsync();
            LlmStatus = ok ? "LM Studio: reachable" : "LM Studio: not reachable";
        }
        finally
        {
            _commandBusy--;
            UpdateBusy();
        }
    }

    // --- event handling (already on UI thread) ---

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        ConnectionStatus = connected ? "Connected" : "Disconnected";
        if (!connected)
        {
            CurrentPrompt = "";
            PromptChanged?.Invoke(new MudMessage { Direction = MessageDirection.Incoming, PlainText = "" });
            ResetLiveBuffer(); // don't leave stale partial output if a stream was cut off
        }
    }

    private void OnDecision(AgentDecision decision)
    {
        _applyingFromAgent = true;
        Goal = _orchestrator.Goal;
        _applyingFromAgent = false;

        RefreshSuppressions();
        _ = RefreshMemoryStatsAsync();
    }

    private void OnApprovalRequested(AgentDecision decision)
    {
        PendingCommand = decision.Command;
        PendingReasoning = decision.Reasoning;
        PendingRisk = decision.Risk.ToString();
        HasPendingApproval = true;
    }

    private void AppendReasoning(string reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return;
        ReasoningLog.Insert(0, reasoning.Trim()); // newest at the top
        while (ReasoningLog.Count > MaxLogEntries) ReasoningLog.RemoveAt(ReasoningLog.Count - 1);
    }

    private void RefreshSuppressions()
    {
        Suppressions.Clear();
        foreach (var s in _tracker.GetActiveSuppressions())
            Suppressions.Add(s);
    }

    private async Task RefreshMemoryStatsAsync()
    {
        try
        {
            int lessons = await _memory.CountAsync("lessons");
            int commands = await _memory.CountAsync("command_knowledge");
            int rooms = await _memory.CountAsync("rooms");
            OnUi(() => MemoryStats = $"Lessons {lessons}  |  Commands {commands}  |  Rooms {rooms}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Memory stats refresh failed (store may not be ready yet)");
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}

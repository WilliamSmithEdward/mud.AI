using System.Diagnostics;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;

namespace MudAI.Core.Agent;

/// <summary>A login response to send, and whether it is sensitive (so the UI/echo can mask it).</summary>
public readonly record struct LoginAction(string Command, bool Secret);

/// <summary>
/// Pattern-driven auto-login. Watches incoming MUD text for configured prompts (expect/send
/// steps) and returns the response to send, substituting {username}/{password}. Pure logic:
/// the orchestrator performs the actual send. Default script handles a typical name/password flow.
///
/// Note: <see cref="LoginStep.WhenContains"/> is a raw, case-insensitive substring. To avoid
/// firing on gameplay/banner text, only short lines (likely prompts) are considered, and each
/// <c>Once</c> step fires a single time per connection.
/// </summary>
public sealed class LoginManager
{
    // Login prompts are short; ignore long lines that merely happen to contain a token.
    private const int MaxPromptLength = 200;

    // After this long we stop treating login as "in progress" so the AI loop can't be blocked
    // forever if a scripted step never matches.
    private const int LoginWindowMs = 30_000;

    private readonly List<LoginStep> _steps;
    private readonly int _onceStepCount;
    private readonly HashSet<int> _fired = [];
    private readonly object _gate = new();

    private bool _enabled;
    private string _username = "";
    private string _password = "";
    private long _startedTicks;

    // Credentials/enable flag are written from the UI thread and read on the reader thread, so
    // all access is serialized through _gate.
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public string Username
    {
        get { lock (_gate) return _username; }
        set { lock (_gate) _username = value ?? ""; }
    }

    public string Password
    {
        get { lock (_gate) return _password; }
        set { lock (_gate) _password = value ?? ""; }
    }

    public LoginManager(IOptions<MudAiOptions> options)
    {
        var login = options.Value.Login;
        _enabled = login.AutoLogin;
        _username = login.Username ?? "";
        _password = login.Password ?? "";
        _steps = login.Script.Count > 0 ? login.Script : DefaultScript();
        _onceStepCount = _steps.Count(s => s.Once && !string.IsNullOrEmpty(s.WhenContains));
        _startedTicks = Stopwatch.GetTimestamp();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _fired.Clear();
            _startedTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// True while auto-login is enabled and the script hasn't finished, so the AI loop should hold
    /// off so its sends don't interleave with login. Bounded by a timeout as a safety valve.
    /// </summary>
    public bool LoginInProgress
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled || _fired.Count >= _onceStepCount) return false;
                double elapsedMs = (Stopwatch.GetTimestamp() - _startedTicks) * 1000.0 / Stopwatch.Frequency;
                return elapsedMs < LoginWindowMs;
            }
        }
    }

    /// <summary>Given an incoming line/prompt, returns the next login action to send, or null.</summary>
    public LoginAction? OnLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > MaxPromptLength) return null;

        lock (_gate)
        {
            if (!_enabled) return null;

            for (int i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                if (string.IsNullOrEmpty(step.WhenContains)) continue;
                if (step.Once && _fired.Contains(i)) continue;

                if (line.Contains(step.WhenContains, StringComparison.OrdinalIgnoreCase))
                {
                    if (step.Once) _fired.Add(i);
                    bool secret = step.Send.Contains("{password}", StringComparison.OrdinalIgnoreCase);
                    return new LoginAction(SubstituteLocked(step.Send), secret);
                }
            }
        }

        return null;
    }

    // Caller must hold _gate.
    private string SubstituteLocked(string s) => s
        .Replace("{username}", _username, StringComparison.OrdinalIgnoreCase)
        .Replace("{password}", _password, StringComparison.OrdinalIgnoreCase);

    private static List<LoginStep> DefaultScript() =>
    [
        new LoginStep { WhenContains = "name", Send = "{username}" },
        new LoginStep { WhenContains = "password", Send = "{password}" }
    ];
}

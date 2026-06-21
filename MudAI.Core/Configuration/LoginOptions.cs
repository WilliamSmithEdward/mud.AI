namespace MudAI.Core.Configuration;

/// <summary>Auto-login configuration. The script is an expect/send sequence run on connect.</summary>
public sealed class LoginOptions
{
    public bool AutoLogin { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Custom expect/send steps. If empty, a sensible default (name then password) is used.</summary>
    public List<LoginStep> Script { get; set; } = [];
}

/// <summary>One expect/send step: when an incoming line contains <see cref="WhenContains"/>, send <see cref="Send"/>.</summary>
public sealed class LoginStep
{
    /// <summary>Case-insensitive substring to look for in incoming text.</summary>
    public string WhenContains { get; set; } = "";

    /// <summary>Text to send; supports {username} and {password} placeholders.</summary>
    public string Send { get; set; } = "";

    /// <summary>Fire only once per connection (default true).</summary>
    public bool Once { get; set; } = true;
}

using Microsoft.Extensions.Options;
using MudAI.Core.Agent;
using MudAI.Core.Configuration;
using Xunit;

namespace MudAI.Tests;

public class LoginManagerTests
{
    private static LoginManager NewManager(bool enabled = true) =>
        new(Options.Create(new MudAiOptions
        {
            Login = new LoginOptions { AutoLogin = enabled, Username = "bob", Password = "secret" }
        }));

    [Fact]
    public void DefaultScript_SendsUsernameOnNamePrompt()
    {
        var m = NewManager();
        var action = m.OnLine("By what name do you wish to be known?");

        Assert.NotNull(action);
        Assert.Equal("bob", action!.Value.Command);
        Assert.False(action.Value.Secret);
    }

    [Fact]
    public void DefaultScript_SendsPasswordAndMarksSecret()
    {
        var m = NewManager();
        m.OnLine("Enter your name:");
        var action = m.OnLine("Password:");

        Assert.NotNull(action);
        Assert.Equal("secret", action!.Value.Command);
        Assert.True(action.Value.Secret);
    }

    [Fact]
    public void OnceSteps_FireOnlyOnce()
    {
        var m = NewManager();
        Assert.NotNull(m.OnLine("your name?"));
        Assert.Null(m.OnLine("your name?")); // already fired
    }

    [Fact]
    public void Disabled_ReturnsNull()
    {
        var m = NewManager(enabled: false);
        Assert.Null(m.OnLine("What is your name?"));
    }

    [Fact]
    public void Reset_ReArmsSteps()
    {
        var m = NewManager();
        Assert.NotNull(m.OnLine("name?"));
        m.Reset();
        Assert.NotNull(m.OnLine("name?"));
    }

    [Fact]
    public void LongLine_DoesNotTriggerLogin()
    {
        var m = NewManager();
        var longGameplayLine = new string('x', 250) + " your name is mud";
        Assert.Null(m.OnLine(longGameplayLine));
    }

    [Fact]
    public void LoginInProgress_TrueUntilAllStepsFire()
    {
        var m = NewManager();
        Assert.True(m.LoginInProgress);
        m.OnLine("your name?");
        m.OnLine("password:");
        Assert.False(m.LoginInProgress);
    }

    [Fact]
    public void LoginInProgress_FalseWhenDisabled()
    {
        Assert.False(NewManager(enabled: false).LoginInProgress);
    }

    [Fact]
    public void CustomScript_IsUsedWhenProvided()
    {
        var m = new LoginManager(Options.Create(new MudAiOptions
        {
            Login = new LoginOptions
            {
                AutoLogin = true,
                Username = "hero",
                Script =
                [
                    new LoginStep { WhenContains = "menu", Send = "1" },
                    new LoginStep { WhenContains = "char", Send = "{username}" }
                ]
            }
        }));

        Assert.Equal("1", m.OnLine("Main menu:")!.Value.Command);
        Assert.Equal("hero", m.OnLine("Choose char:")!.Value.Command);
    }
}

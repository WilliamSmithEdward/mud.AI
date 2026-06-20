using MudAI.Core.GameData;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class GmcpTests
{
    [Fact]
    public void Parse_PackageWithJson()
    {
        var msg = GmcpParser.Parse("""Char.Vitals { "hp": 100, "maxhp": 120 }""");

        Assert.Equal("Char.Vitals", msg.Package);
        Assert.True(msg.HasData);
        Assert.Equal(100, msg.Data.GetProperty("hp").GetInt32());
    }

    [Fact]
    public void Parse_PackageWithoutJson()
    {
        var msg = GmcpParser.Parse("Core.Ping");
        Assert.Equal("Core.Ping", msg.Package);
        Assert.False(msg.HasData);
    }

    [Fact]
    public void Parse_InvalidJson_HasNoData()
    {
        var msg = GmcpParser.Parse("Bad.Package {not valid");
        Assert.Equal("Bad.Package", msg.Package);
        Assert.False(msg.HasData);
    }

    [Fact]
    public void ApplyGmcp_CharVitals_SetsHpAndMp()
    {
        var msg = GmcpParser.Parse("""Char.Vitals { "hp": 50, "maxhp": 60, "mp": 10, "maxmp": 20 }""");
        var state = GameStateMapper.ApplyGmcp(new GameState(), msg);

        Assert.Equal(50, state.Hp);
        Assert.Equal(60, state.MaxHp);
        Assert.Equal(10, state.Mp);
        Assert.Equal(20, state.MaxMp);
    }

    [Fact]
    public void ApplyGmcp_AcceptsStringNumbers()
    {
        var msg = GmcpParser.Parse("""Char.Vitals { "hp": "75" }""");
        var state = GameStateMapper.ApplyGmcp(new GameState(), msg);
        Assert.Equal(75, state.Hp);
    }

    [Fact]
    public void ApplyGmcp_RoomInfo_ExitsObjectJoined()
    {
        var msg = GmcpParser.Parse("""Room.Info { "name": "Temple Square", "exits": { "n": 1, "e": 2 } }""");
        var state = GameStateMapper.ApplyGmcp(new GameState(), msg);

        Assert.Equal("Temple Square", state.RoomName);
        Assert.Contains("n", state.Exits);
        Assert.Contains("e", state.Exits);
    }

    [Fact]
    public void ToSummary_IncludesHpAndRoom()
    {
        var state = new GameState { Hp = 100, MaxHp = 120, RoomName = "Temple" };
        var summary = state.ToSummary();
        Assert.Contains("HP 100/120", summary);
        Assert.Contains("Temple", summary);
    }

    [Fact]
    public void EmptyState_HasAnyIsFalse()
    {
        Assert.False(new GameState().HasAny);
    }
}

using System.Text;
using MudAI.Core.GameData;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class MsdpTests
{
    private const byte Var = 1, Val = 2, TableOpen = 3, TableClose = 4;

    private static byte[] Bytes(params object[] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts)
        {
            if (p is byte b) list.Add(b);
            else if (p is string s) list.AddRange(Encoding.ASCII.GetBytes(s));
        }
        return list.ToArray();
    }

    [Fact]
    public void Parse_TopLevelScalars()
    {
        var data = Bytes(Var, "HEALTH", Val, "100", Var, "ROOM_NAME", Val, "Temple");
        var dict = MsdpParser.Parse(data);

        Assert.Equal("100", dict["HEALTH"]);
        Assert.Equal("Temple", dict["ROOM_NAME"]);
    }

    [Fact]
    public void Parse_NestedTable()
    {
        var data = Bytes(Var, "EXITS", Val, TableOpen, Var, "n", Val, "1", Var, "e", Val, "2", TableClose);
        var dict = MsdpParser.Parse(data);

        var exits = Assert.IsType<Dictionary<string, object?>>(dict["EXITS"]);
        Assert.Equal("1", exits["n"]);
        Assert.Equal("2", exits["e"]);
    }

    [Fact]
    public void ApplyMsdp_MapsKnownKeys()
    {
        var data = Bytes(Var, "HEALTH", Val, "80", Var, "MAX_HEALTH", Val, "120", Var, "ROOM_NAME", Val, "Forest");
        var dict = MsdpParser.Parse(data);
        var state = GameStateMapper.ApplyMsdp(new GameState(), dict);

        Assert.Equal(80, state.Hp);
        Assert.Equal(120, state.MaxHp);
        Assert.Equal("Forest", state.RoomName);
    }

    [Fact]
    public void ApplyMsdp_ExitsTableJoined()
    {
        var data = Bytes(Var, "EXITS", Val, TableOpen, Var, "north", Val, "10", TableClose);
        var dict = MsdpParser.Parse(data);
        var state = GameStateMapper.ApplyMsdp(new GameState(), dict);

        Assert.Contains("north", state.Exits);
    }

    [Fact]
    public void Parse_DeeplyNested_DoesNotOverflow()
    {
        // Regression: an adversarial payload of thousands of TABLE_OPEN markers must not recurse
        // without bound (which would crash the process with an uncatchable StackOverflowException).
        var list = new List<byte>();
        for (int i = 0; i < 10_000; i++)
        {
            list.Add(Var);
            list.AddRange(Encoding.ASCII.GetBytes("a"));
            list.Add(Val);
            list.Add(TableOpen);
        }

        var ex = Record.Exception(() => MsdpParser.Parse(list.ToArray()));
        Assert.Null(ex);
    }
}

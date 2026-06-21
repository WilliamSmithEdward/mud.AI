using System.Text;
using System.Text.Json;
using MudAI.Core.Models;

namespace MudAI.Core.GameData;

/// <summary>Merges GMCP/MSDP messages into the running <see cref="GameState"/>.</summary>
public static class GameStateMapper
{
    public static GameState ApplyGmcp(GameState state, GmcpMessage msg)
    {
        if (!msg.HasData || msg.Data.ValueKind != JsonValueKind.Object)
            return state;

        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in msg.Data.EnumerateObject())
            props[p.Name] = p.Value;

        int? ReadInt(params string[] names)
        {
            foreach (var n in names)
                if (props.TryGetValue(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var num)) return num;
                    if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var ps)) return ps;
                }
            return null;
        }

        string? ReadString(params string[] names)
        {
            foreach (var n in names)
                if (props.TryGetValue(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) return v.GetString();
                    if (v.ValueKind == JsonValueKind.Number) return v.ToString();
                }
            return null;
        }

        string? ReadExits()
        {
            if (!props.TryGetValue("exits", out var v)) return null;
            string? result = v.ValueKind switch
            {
                JsonValueKind.Object => string.Join(", ", v.EnumerateObject().Select(e => e.Name)),
                JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(e => e.ToString())),
                JsonValueKind.String => v.GetString(),
                _ => null
            };
            // Don't clobber known exits with an empty set (preserve prior via the `?? state.Exits` fallback).
            return NormalizeExits(result);
        }

        return msg.Package.ToLowerInvariant() switch
        {
            "char.vitals" => state with
            {
                Hp = ReadInt("hp") ?? state.Hp,
                MaxHp = ReadInt("maxhp", "hpmax", "max_hp") ?? state.MaxHp,
                Mp = ReadInt("mp", "mana", "sp") ?? state.Mp,
                MaxMp = ReadInt("maxmp", "maxmana", "maxsp", "mana_max") ?? state.MaxMp,
                Mv = ReadInt("mv", "moves", "movement") ?? state.Mv,
                MaxMv = ReadInt("maxmv", "maxmoves", "maxmovement") ?? state.MaxMv,
                Level = ReadInt("level") ?? state.Level
            },
            "char.status" => state with
            {
                Level = ReadInt("level") ?? state.Level,
                CharName = ReadString("name") ?? state.CharName
            },
            "char.base" => state with
            {
                CharName = ReadString("name") ?? state.CharName
            },
            "room.info" => state with
            {
                RoomName = ReadString("name") ?? state.RoomName,
                Zone = ReadString("zone", "area") ?? state.Zone,
                Exits = ReadExits() ?? state.Exits
            },
            _ => state
        };
    }

    /// <summary>
    /// Canonicalizes an exits string to a single comma-and-space delimiter regardless of how the
    /// server formatted it (comma, space, or bracket separated). Keeps downstream consumers - the
    /// room map and the frontier comma-count heuristic - consistent. Returns null if empty.
    /// </summary>
    private static string? NormalizeExits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = raw.Split(
            [',', ';', ' ', '\t', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? null : string.Join(", ", tokens);
    }

    public static GameState ApplyMsdp(GameState state, IReadOnlyDictionary<string, object?> d)
    {
        string? Str(string key) => d.TryGetValue(key, out var v) ? v as string : null;
        int? Int(string key) => int.TryParse(Str(key), out var i) ? i : null;

        string? Exits()
        {
            if (!d.TryGetValue("EXITS", out var v)) return null;
            string? result = v switch
            {
                Dictionary<string, object?> table => string.Join(", ", table.Keys),
                string s => s,
                _ => null
            };
            return NormalizeExits(result);
        }

        return state with
        {
            Hp = Int("HEALTH") ?? state.Hp,
            MaxHp = Int("MAX_HEALTH") ?? state.MaxHp,
            Mp = Int("MANA") ?? state.Mp,
            MaxMp = Int("MAX_MANA") ?? state.MaxMp,
            Mv = Int("MOVEMENT") ?? state.Mv,
            MaxMv = Int("MAX_MOVEMENT") ?? state.MaxMv,
            Level = Int("LEVEL") ?? state.Level,
            CharName = Str("CHARACTER_NAME") ?? state.CharName,
            RoomName = Str("ROOM_NAME") ?? state.RoomName,
            Zone = Str("AREA_NAME") ?? state.Zone,
            Exits = Exits() ?? state.Exits
        };
    }
}

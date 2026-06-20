using System.Text;

namespace MudAI.Core.GameData;

/// <summary>
/// Parses an MSDP subnegotiation payload into a nested structure. Values are either a
/// <see cref="string"/>, a nested <c>Dictionary&lt;string, object?&gt;</c> (table), or a
/// <c>List&lt;object?&gt;</c> (array).
/// </summary>
public static class MsdpParser
{
    private const byte Var = 1;
    private const byte Val = 2;
    private const byte TableOpen = 3;
    private const byte TableClose = 4;
    private const byte ArrayOpen = 5;
    private const byte ArrayClose = 6;

    // Guards against a malicious server sending deeply-nested tables/arrays, which would otherwise
    // recurse without bound and crash the process with an uncatchable StackOverflowException.
    // Real MUD tables are shallow.
    private const int MaxDepth = 32;

    public static Dictionary<string, object?> Parse(byte[] data)
    {
        int i = 0;
        return ParseTable(data, ref i, 0);
    }

    private static Dictionary<string, object?> ParseTable(byte[] d, ref int i, int depth)
    {
        var table = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (i < d.Length)
        {
            byte b = d[i];
            if (b == TableClose) { i++; break; }
            if (b == Var)
            {
                i++;
                string name = ReadString(d, ref i);
                if (i < d.Length && d[i] == Val)
                {
                    i++;
                    table[name] = ParseValue(d, ref i, depth);
                }
                else
                {
                    table[name] = null;
                }
            }
            else
            {
                i++; // unexpected byte; skip
            }
        }
        return table;
    }

    private static object? ParseValue(byte[] d, ref int i, int depth)
    {
        if (i >= d.Length) return "";
        byte b = d[i];
        if (b == TableOpen)
        {
            i++;
            if (depth >= MaxDepth) { SkipNested(d, ref i); return ""; }
            return ParseTable(d, ref i, depth + 1);
        }
        if (b == ArrayOpen)
        {
            i++;
            if (depth >= MaxDepth) { SkipNested(d, ref i); return ""; }
            return ParseArray(d, ref i, depth + 1);
        }
        return ReadString(d, ref i);
    }

    private static List<object?> ParseArray(byte[] d, ref int i, int depth)
    {
        var list = new List<object?>();
        while (i < d.Length)
        {
            byte b = d[i];
            if (b == ArrayClose) { i++; break; }
            if (b == Val) { i++; list.Add(ParseValue(d, ref i, depth)); }
            else { i++; }
        }
        return list;
    }

    /// <summary>Iteratively consumes a nested table/array whose opener was already read (no recursion).</summary>
    private static void SkipNested(byte[] d, ref int i)
    {
        int level = 1;
        while (i < d.Length && level > 0)
        {
            byte b = d[i++];
            if (b is TableOpen or ArrayOpen) level++;
            else if (b is TableClose or ArrayClose) level--;
        }
    }

    private static string ReadString(byte[] d, ref int i)
    {
        int start = i;
        while (i < d.Length && !IsMarker(d[i])) i++;
        return Encoding.Latin1.GetString(d, start, i - start);
    }

    private static bool IsMarker(byte b) =>
        b is Var or Val or TableOpen or TableClose or ArrayOpen or ArrayClose;
}

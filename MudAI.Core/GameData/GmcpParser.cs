using System.Text.Json;

namespace MudAI.Core.GameData;

/// <summary>A parsed GMCP message: a package name and optional JSON data.</summary>
public readonly record struct GmcpMessage(string Package, JsonElement Data, bool HasData);

/// <summary>
/// Parses a GMCP payload of the form <c>Package.Sub {json}</c> (the JSON part is optional).
/// The returned <see cref="JsonElement"/> is cloned so it stays valid after the document is freed.
/// </summary>
public static class GmcpParser
{
    public static GmcpMessage Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new GmcpMessage("", default, false);

        payload = payload.Trim();

        int space = payload.IndexOf(' ');
        string package = (space < 0 ? payload : payload[..space]).Trim();
        string rest = space < 0 ? "" : payload[(space + 1)..].Trim();

        if (rest.Length == 0)
            return new GmcpMessage(package, default, false);

        try
        {
            using var doc = JsonDocument.Parse(rest);
            return new GmcpMessage(package, doc.RootElement.Clone(), true);
        }
        catch (JsonException)
        {
            return new GmcpMessage(package, default, false);
        }
    }
}

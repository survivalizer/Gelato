using System.Text.Json;

namespace Gelato.Tests.Streams;

internal static class Json
{
    /// <summary>A detached element that outlives the document it was parsed from.</summary>
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

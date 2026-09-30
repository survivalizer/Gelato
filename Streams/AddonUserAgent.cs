namespace Gelato.Streams;

/// <summary>
/// The User-Agent Gelato sends to Stremio addons. AIOStreams includes its parsed stream data
/// (<c>streamData</c>) for User-Agents starting with "AIO" unless its operator overrides
/// that; "Gelato" in the product name lets AIOStreams variants - configs switched on the
/// User-Agent - single out Jellyfin.
/// </summary>
public static class AddonUserAgent
{
    public const string Product = "AIOStreams-Gelato";

    public static string For(Version? version) => $"{Product}/{version ?? new Version(0, 0, 0, 0)}";

    public static void Apply(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            For(typeof(AddonUserAgent).Assembly.GetName().Version)
        );
    }
}

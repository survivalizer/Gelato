using System.Security.Cryptography;
using System.Text;

namespace Gelato.Streams;

/// <summary>A stream placed for sync: its row identity, and whether it is a notice.</summary>
public sealed record SyncEntry(StremioStream Stream, StreamData? Data, bool IsNotice, Guid Guid);

/// <summary>
/// Decides which addon streams become playable versions and which become notices - entries
/// carrying an addon message, error or statistic instead of something to play - and orders
/// them. Mirrors AIOStreams' Jellyfin mode: notices come after every playable version, so a
/// notice is never the default while something playable exists.
/// </summary>
public static class StreamClassifier
{
    /// <summary>GelatoData key marking a stream row as a notice.</summary>
    public const string NoticeKey = "notice";

    private static readonly HashSet<string> NoticeTypes = new(StringComparer.Ordinal)
    {
        "error",
        "statistic",
        "info",
        "external",
        "youtube",
    };

    /// <summary>
    /// Decides by type and locator only. With stream data: type is in NoticeTypes. Without
    /// stream data: not valid and not torrent. Text is checked in PlanSync and notices without
    /// text are dropped.
    /// </summary>
    public static bool IsNotice(StremioStream stream, StreamData? data)
    {
        if (data is not null)
            return data.Type is { } type && NoticeTypes.Contains(type);

        return !stream.IsValid() && !stream.IsTorrent();
    }

    private static bool HasText(StremioStream stream) =>
        !string.IsNullOrWhiteSpace(stream.Name)
        || !string.IsNullOrWhiteSpace(stream.Description)
        || !string.IsNullOrWhiteSpace(stream.Title);

    /// <summary>
    /// Exactly today's acceptance rule: a valid URL, and no torrent while P2P is off.
    /// </summary>
    public static bool IsPlayable(StremioStream stream, bool p2pEnabled) =>
        stream.IsValid() && (p2pEnabled || !stream.IsTorrent());

    /// <summary>
    /// Keyed on type and name, not description: statistics and many errors carry timings or
    /// counts that change every sync, and keying on them would recreate the row each time.
    /// </summary>
    public static Guid NoticeGuid(string? type, string? name, int occurrence)
    {
        var key = $"notice|{type}|{name}|{occurrence}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Orders playables before notices, deduplicates (drops notices with external URL matching
    /// a playable URL and notices without text), and assigns stable notice identities.
    /// </summary>
    public static IReadOnlyList<SyncEntry> PlanSync(
        IReadOnlyList<StremioStream> streams,
        bool p2pEnabled
    )
    {
        var playable = new List<SyncEntry>();
        var notices = new List<(StremioStream Stream, StreamData? Data)>();

        foreach (var stream in streams)
        {
            var data = StreamDataMapper.TryMap(stream.StreamData);
            if (IsNotice(stream, data))
            {
                if (HasText(stream))
                    notices.Add((stream, data));
            }
            else if (IsPlayable(stream, p2pEnabled))
                playable.Add(new SyncEntry(stream, data, false, stream.GetGuid()));
        }

        var playableUrls = playable
            .Select(e => e.Stream.Url)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<(string Type, string Name), int>();
        var planned = new List<SyncEntry>(playable);

        foreach (var (stream, data) in notices)
        {
            // A link that repeats a playable version would only be that version again.
            var link = stream.ExternalUrl;
            if (!string.IsNullOrEmpty(link) && playableUrls.Contains(link))
                continue;

            var type = data?.Type ?? "";
            var name = stream.Name ?? "";
            var occurrence = seen.GetValueOrDefault((type, name));
            seen[(type, name)] = occurrence + 1;

            planned.Add(new SyncEntry(stream, data, true, NoticeGuid(type, name, occurrence)));
        }

        return planned;
    }

    /// <summary>
    /// Only playable versions count as a successful sync. An error-only response must not be
    /// cached as synced, or fixing the addon's config would not show until the cache expired.
    /// </summary>
    public static int PlayableCount(IReadOnlyList<SyncEntry> entries) =>
        entries.Count(e => !e.IsNotice);
}

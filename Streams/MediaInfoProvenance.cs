namespace Gelato.Streams;

public enum MediaInfoWrite
{
    Write,
    Skip,
    StampFfProbe,
}

/// <summary>
/// Where a stream row's media info came from, stored under <see cref="Key"/> in its
/// GelatoData. Before stream data existed ffprobe was the only writer, which is why a row
/// with no provenance but a video stream is treated as probed.
/// </summary>
public static class MediaInfoProvenance
{
    public const string Key = "mediaInfoSource";
    public const string FfProbe = "ffprobe";
    public const string AioStreamsProbed = "aiostreams-probed";
    public const string AioStreamsGuessed = "aiostreams-guessed";

    private static readonly long MinimumRuntimeTicks = TimeSpan.FromMinutes(2).Ticks;

    public static string FromVerdict(bool isTrusted) =>
        isTrusted ? AioStreamsProbed : AioStreamsGuessed;

    public static bool IsTrusted(string? provenance) => provenance is FfProbe or AioStreamsProbed;

    /// <summary>
    /// Today's triggers (no video stream, runtime under two minutes) plus one: data guessed
    /// from a filename is probed so a guess never drives a playback decision. Notices point at
    /// a placeholder clip and are never probed.
    /// </summary>
    public static bool ShouldProbe(
        bool isNotice,
        string? provenance,
        bool hasVideoStream,
        long? runTimeTicks
    ) =>
        !isNotice
        && (
            !hasVideoStream
            || (runTimeTicks ?? 0) < MinimumRuntimeTicks
            || provenance == AioStreamsGuessed
        );

    /// <summary>
    /// A failed probe leaves provenance alone, so a guess is probed again next time.
    /// </summary>
    public static string? AfterProbe(bool succeeded, string? before) =>
        succeeded ? FfProbe : before;

    /// <summary>
    /// Whether a sync may write stream-data media info to a row. <paramref name="hasVideoStream"/>
    /// runs only for legacy rows, once: afterwards the row has provenance, so re-browsing a
    /// title never queries or rewrites unchanged versions.
    /// </summary>
    public static MediaInfoWrite DecideWrite(
        bool isNewRow,
        string? existingProvenance,
        Func<bool> hasVideoStream,
        bool newIsTrusted
    )
    {
        if (isNewRow)
            return MediaInfoWrite.Write;

        if (existingProvenance is null)
            return hasVideoStream() ? MediaInfoWrite.StampFfProbe : MediaInfoWrite.Write;

        if (IsTrusted(existingProvenance))
            return MediaInfoWrite.Skip;

        return newIsTrusted ? MediaInfoWrite.Write : MediaInfoWrite.Skip;
    }
}

using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

/// <summary>What SyncStreams writes to one stream row; null fields are left as they are.</summary>
public sealed record RowMediaInfoPlan(
    MediaInfoWrite Decision,
    IReadOnlyList<MediaStream>? Streams,
    string? Provenance,
    string? Container,
    long? Size,
    long? RunTimeTicks
);

public static class StreamRowPlanner
{
    private static readonly RowMediaInfoPlan Nothing = new(
        MediaInfoWrite.Skip,
        null,
        null,
        null,
        null,
        null
    );

    public static RowMediaInfoPlan Plan(
        bool isNewRow,
        bool isNotice,
        string? existingProvenance,
        Func<bool> hasVideoStream,
        StreamData? data,
        string? url,
        Func<string, string?> toIso6392
    )
    {
        if (isNotice)
        {
            return isNewRow
                ? new RowMediaInfoPlan(
                    MediaInfoWrite.Write,
                    NoticeClip.MediaStreams(),
                    null,
                    NoticeClip.Container,
                    null,
                    null
                )
                : Nothing;
        }

        // Without stream data, or once a row holds trusted media info, nothing changes - and
        // skipping before the build keeps re-browsing a large title cheap.
        if (data is null || (!isNewRow && MediaInfoProvenance.IsTrusted(existingProvenance)))
            return Nothing;

        var info = StreamMediaInfo.Build(data, url, toIso6392);
        var decision = MediaInfoProvenance.DecideWrite(
            isNewRow,
            existingProvenance,
            hasVideoStream,
            info.IsTrusted
        );

        return decision switch
        {
            MediaInfoWrite.Write => new RowMediaInfoPlan(
                MediaInfoWrite.Write,
                info.Streams,
                MediaInfoProvenance.FromVerdict(info.IsTrusted),
                info.Container,
                info.Size,
                info.RunTimeTicks
            ),
            MediaInfoWrite.StampFfProbe => new RowMediaInfoPlan(
                MediaInfoWrite.StampFfProbe,
                null,
                MediaInfoProvenance.FfProbe,
                null,
                null,
                null
            ),
            _ => Nothing,
        };
    }
}

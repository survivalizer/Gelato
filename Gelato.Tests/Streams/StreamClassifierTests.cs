using Gelato.Streams;
using MediaBrowser.Controller.Entities.Movies;

namespace Gelato.Tests.Streams;

/// <summary>
/// Which streams become versions, which become notices, and in what order - mirroring
/// AIOStreams' Jellyfin mode (packages/server/src/routes/jellyfin/resolve.ts). Playable
/// versions keep exactly today's acceptance rule.
/// </summary>
public class StreamClassifierTests
{
    private const string Good = "https://cdn.example.com/dl/movie.mkv";

    private static StremioStream S(
        string? url = null,
        string? name = "Some stream",
        string? description = null,
        string? infoHash = null,
        string? data = null,
        string? externalUrl = null
    ) =>
        new()
        {
            Url = url ?? "",
            Name = name,
            Description = description,
            InfoHash = infoHash,
            ExternalUrl = externalUrl,
            StreamData = data is null ? null : Json.Parse(data),
        };

    private static string Typed(string type) => $$"""{ "type": "{{type}}" }""";

    [Fact]
    public void ValidUrl_IsPlayable()
    {
        var planned = Assert.Single(StreamClassifier.PlanSync([S(url: Good)], true));

        Assert.False(planned.IsNotice);
        Assert.Equal(S(url: Good).GetGuid(), planned.Guid);
    }

    [Fact]
    public void TorrentWithUrl_FollowsTheP2PSetting()
    {
        var torrent = S(url: Good, infoHash: new string('a', 40));

        Assert.True(StreamClassifier.IsPlayable(torrent, p2pEnabled: true));
        Assert.False(StreamClassifier.IsPlayable(torrent, p2pEnabled: false));
    }

    /// <summary>
    /// A torrent-only stream plays through the P2P proxy, so it follows the P2P setting and is
    /// never a notice.
    /// </summary>
    [Fact]
    public void TorrentOnlyStream_FollowsTheP2PSetting()
    {
        var torrent = S(infoHash: new string('a', 40));

        var planned = Assert.Single(StreamClassifier.PlanSync([torrent], true));
        Assert.False(planned.IsNotice);
        Assert.Empty(StreamClassifier.PlanSync([torrent], false));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("statistic")]
    [InlineData("info")]
    [InlineData("external")]
    [InlineData("youtube")]
    public void NoticeTypes_AreNotices_EvenWithAValidUrl(string type)
    {
        var planned = Assert.Single(
            StreamClassifier.PlanSync([S(url: Good, data: Typed(type))], true)
        );

        Assert.True(planned.IsNotice);
    }

    [Fact]
    public void WithoutStreamData_UrlLessStreamWithText_IsANotice()
    {
        var stream = S(name: "[!] AIOStreams", externalUrl: "https://docs.example.com/why");

        Assert.True(StreamClassifier.IsNotice(stream, null));
    }

    [Fact]
    public void WithoutStreamData_RootPathUrl_IsANotice()
    {
        Assert.True(StreamClassifier.IsNotice(S(url: "https://host.example.com/"), null));
    }

    [Fact]
    public void StreamDataOfAPlayableType_WithABadUrl_IsDropped()
    {
        Assert.Empty(StreamClassifier.PlanSync([S(url: "", data: Typed("http"))], true));
    }

    [Fact]
    public void NoticeWithNoText_IsDropped()
    {
        var blank = S(name: null, externalUrl: "https://docs.example.com/why");

        Assert.Empty(StreamClassifier.PlanSync([blank], true));
    }

    [Fact]
    public void PlanSync_PutsEveryPlayableBeforeEveryNotice()
    {
        var planned = StreamClassifier.PlanSync(
            [
                S(name: "n1", data: Typed("error")),
                S(url: Good, name: "p1"),
                S(name: "n2", data: Typed("statistic")),
                S(url: "https://cdn.example.com/other.mkv", name: "p2"),
            ],
            true
        );

        Assert.Equal(new[] { "p1", "p2", "n1", "n2" }, planned.Select(e => e.Stream.Name));
        Assert.Equal(new[] { false, false, true, true }, planned.Select(e => e.IsNotice));
    }

    [Fact]
    public void PlanSync_DropsANoticeThatRepeatsAPlayableUrl()
    {
        var planned = StreamClassifier.PlanSync(
            [S(url: Good, name: "p"), S(name: "same link", externalUrl: Good)],
            true
        );

        Assert.Equal(new[] { "p" }, planned.Select(e => e.Stream.Name));
    }

    [Fact]
    public void NoticeGuid_IgnoresTheDescription()
    {
        var first = StreamClassifier.PlanSync(
            [S(name: "Stats", description: "Found 50 in 1.2s", data: Typed("statistic"))],
            true
        );
        var second = StreamClassifier.PlanSync(
            [S(name: "Stats", description: "Found 51 in 0.9s", data: Typed("statistic"))],
            true
        );

        Assert.Equal(first[0].Guid, second[0].Guid);
    }

    [Fact]
    public void NoticeGuid_KeepsSameNamedNoticesApart()
    {
        var planned = StreamClassifier.PlanSync(
            [
                S(name: "[!] AIOStreams", data: Typed("error")),
                S(name: "[!] AIOStreams", data: Typed("error")),
            ],
            true
        );

        Assert.NotEqual(planned[0].Guid, planned[1].Guid);
    }

    [Fact]
    public void NoticeGuid_IsTheSpecKeyHashed()
    {
        var expected = new Guid(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes("notice|error|[!] AIOStreams|0")
            )
        );

        Assert.Equal(expected, StreamClassifier.NoticeGuid("error", "[!] AIOStreams", 0));
    }

    [Fact]
    public void ErrorOnlyResponse_IsANoticeWithNothingPlayable()
    {
        var planned = StreamClassifier.PlanSync(
            [S(name: "[!] AIOStreams", description: "Invalid credentials", data: Typed("error"))],
            true
        );

        Assert.True(Assert.Single(planned).IsNotice);
        Assert.Equal(0, StreamClassifier.PlayableCount(planned));
    }

    [Fact]
    public void PlanSync_MapsStreamData()
    {
        var planned = StreamClassifier.PlanSync(
            [S(url: Good, data: """{ "type": "debrid", "size": 7 }""")],
            true
        );

        Assert.Equal(7L, planned[0].Data!.Size);
    }

    /// <summary>
    /// A notice type is a notice whatever it carries: with no text it is dropped, never
    /// re-tested as playable - otherwise an addon error with a valid URL would be offered as a
    /// version.
    /// </summary>
    [Fact]
    public void NoticeTypedStreamWithAValidUrlButNoText_IsDropped()
    {
        Assert.Empty(
            StreamClassifier.PlanSync([S(url: Good, name: null, data: Typed("error"))], true)
        );
    }

    private static Movie Row(int? index, bool isNotice = false)
    {
        var row = new Movie();
        if (index is not null)
            row.SetGelatoData("index", index);
        if (isNotice)
            row.SetGelatoData(StreamClassifier.NoticeKey, true);
        return row;
    }

    [Fact]
    public void OrderVersions_PlayableBeatsNoticeAtTheSameIndex()
    {
        var notice = Row(index: 1, isNotice: true);
        var playable = Row(index: 1, isNotice: false);

        var ordered = StreamClassifier.OrderVersions(new[] { notice, playable }).ToList();

        Assert.Equal(new[] { playable, notice }, ordered);
    }

    [Fact]
    public void OrderVersions_NoticesComeAfterPlayables_RegardlessOfIndex()
    {
        var notice = Row(index: 0, isNotice: true);
        var playable = Row(index: 5, isNotice: false);

        var ordered = StreamClassifier.OrderVersions(new[] { notice, playable }).ToList();

        Assert.Equal(new[] { playable, notice }, ordered);
    }

    [Fact]
    public void OrderVersions_RowsWithoutIndex_SortLastWithinTheirGroup()
    {
        var withIndex = Row(index: 0);
        var withoutIndex = Row(index: null);
        var noticeWithIndex = Row(index: 0, isNotice: true);
        var noticeWithoutIndex = Row(index: null, isNotice: true);

        var ordered = StreamClassifier
            .OrderVersions(new[] { noticeWithoutIndex, withoutIndex, noticeWithIndex, withIndex })
            .ToList();

        Assert.Equal(
            new[] { withIndex, withoutIndex, noticeWithIndex, noticeWithoutIndex },
            ordered
        );
    }

    [Fact]
    public void IsNoticeRow_ReadsTheNoticeGelatoDataFlag()
    {
        Assert.True(StreamClassifier.IsNoticeRow(Row(index: 0, isNotice: true)));
        Assert.False(StreamClassifier.IsNoticeRow(Row(index: 0)));
    }
}

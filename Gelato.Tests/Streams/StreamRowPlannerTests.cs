using Gelato.Streams;
using static Gelato.Streams.MediaInfoProvenance;
using static Gelato.Tests.Streams.StreamDataFactory;

namespace Gelato.Tests.Streams;

/// <summary>
/// The per-row decision SyncStreams applies. The two cases that matter most: an existing
/// library's probed rows are never overwritten by a guess, and re-browsing a title with
/// hundreds of unchanged versions neither rewrites nor looks up anything.
/// </summary>
public class StreamRowPlannerTests
{
    private const string Url = "https://cdn.example.com/dl/movie.mkv";

    private static readonly Func<bool> MustNotQuery = () =>
        throw new InvalidOperationException("the media-stream lookup must not run");

    private static RowMediaInfoPlan Plan(
        bool isNew,
        string? provenance,
        Func<bool> hasVideo,
        StreamData? data,
        bool isNotice = false
    ) => StreamRowPlanner.Plan(isNew, isNotice, provenance, hasVideo, data, Url, Iso);

    [Fact]
    public void NewRow_WithProbedData_WritesItAsProbed()
    {
        var plan = Plan(true, null, MustNotQuery, Data(Probed(), size: 10, durationMs: 60_000));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal(AioStreamsProbed, plan.Provenance);
        Assert.NotEmpty(plan.Streams!);
        Assert.Equal("mkv", plan.Container);
        Assert.Equal(10, plan.Size);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, plan.RunTimeTicks);
    }

    [Fact]
    public void NewRow_WithGuessedData_WritesItAsGuessed()
    {
        var plan = Plan(true, null, MustNotQuery, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal(AioStreamsGuessed, plan.Provenance);
    }

    [Fact]
    public void LegacyRowAlreadyProbed_IsStampedNotOverwritten()
    {
        var plan = Plan(false, null, () => true, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.StampFfProbe, plan.Decision);
        Assert.Equal(FfProbe, plan.Provenance);
        Assert.Null(plan.Streams);
    }

    [Fact]
    public void LegacyRowWithoutMediaInfo_GetsStreamData()
    {
        var plan = Plan(false, null, () => false, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.NotNull(plan.Streams);
    }

    [Theory]
    [InlineData(FfProbe)]
    [InlineData(AioStreamsProbed)]
    public void RowsWithProvenance_AreNeitherRewrittenNorQueried(string provenance)
    {
        var plan = Plan(false, provenance, MustNotQuery, Data(Probed()));

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
        Assert.Null(plan.Provenance);
        Assert.Null(plan.Streams);
    }

    [Fact]
    public void GuessedRow_UpgradesOnlyToProbedData()
    {
        var upgrade = Plan(false, AioStreamsGuessed, MustNotQuery, Data(Probed()));
        var same = Plan(false, AioStreamsGuessed, MustNotQuery, Data(Parsed(quality: "addon")));

        Assert.Equal(
            (MediaInfoWrite.Write, AioStreamsProbed),
            (upgrade.Decision, upgrade.Provenance)
        );
        Assert.Equal(MediaInfoWrite.Skip, same.Decision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoStreamData_KeepsTodaysBehaviour(bool isNew)
    {
        var plan = Plan(isNew, null, MustNotQuery, null);

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
        Assert.Null(plan.Provenance);
    }

    [Fact]
    public void NewNotice_GetsThePlaceholderMediaInfo_WithoutProvenance()
    {
        var plan = Plan(true, null, MustNotQuery, null, isNotice: true);

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal("h264", Assert.Single(plan.Streams!).Codec);
        Assert.Equal(NoticeClip.Container, plan.Container);
        Assert.Null(plan.Provenance);
        Assert.Null(plan.RunTimeTicks);
    }

    [Fact]
    public void ExistingNotice_IsLeftAlone()
    {
        var plan = Plan(false, null, MustNotQuery, null, isNotice: true);

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
    }
}

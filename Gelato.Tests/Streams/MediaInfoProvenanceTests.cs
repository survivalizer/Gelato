using Gelato.Streams;
using static Gelato.Streams.MediaInfoProvenance;

namespace Gelato.Tests.Streams;

/// <summary>
/// Where a row's media info came from decides whether it is trusted, whether playback probes
/// it, and whether a sync may overwrite it. The one rule every case protects: a guess never
/// replaces real data.
/// </summary>
public class MediaInfoProvenanceTests
{
    private static readonly long Long = TimeSpan.FromMinutes(90).Ticks;
    private static readonly long Short = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void FromVerdict_NamesTheSource()
    {
        Assert.Equal(AioStreamsProbed, FromVerdict(true));
        Assert.Equal(AioStreamsGuessed, FromVerdict(false));
    }

    [Theory]
    [InlineData(FfProbe, true)]
    [InlineData(AioStreamsProbed, true)]
    [InlineData(AioStreamsGuessed, false)]
    [InlineData(null, false)]
    [InlineData("something-new", false)]
    public void IsTrusted_OnlyForRealData(string? provenance, bool trusted)
    {
        Assert.Equal(trusted, IsTrusted(provenance));
    }

    // Rows of the spec's probe decision table.
    [Theory]
    [InlineData(true, null, false, null, false)] // notice: never
    [InlineData(false, null, true, true, false)] // legacy, video, long: no
    [InlineData(false, null, false, true, true)] // legacy, no video: yes
    [InlineData(false, null, true, false, true)] // legacy, short: yes
    [InlineData(false, AioStreamsGuessed, true, true, true)] // guessed: yes
    [InlineData(false, AioStreamsProbed, true, true, false)] // probed by AIOStreams: no
    [InlineData(false, AioStreamsProbed, true, false, true)] // ...unless short
    [InlineData(false, FfProbe, true, true, false)] // probed by Gelato: no
    [InlineData(false, FfProbe, true, false, true)] // ...unless short
    public void ShouldProbe_FollowsTheDecisionTable(
        bool isNotice,
        string? provenance,
        bool hasVideo,
        bool? longRuntime,
        bool expected
    )
    {
        long? runtime = longRuntime switch
        {
            true => Long,
            false => Short,
            null => null,
        };

        Assert.Equal(expected, ShouldProbe(isNotice, provenance, hasVideo, runtime));
    }

    [Fact]
    public void AfterProbe_BecomesFfProbe_OnlyOnSuccess()
    {
        Assert.Equal(FfProbe, AfterProbe(true, AioStreamsGuessed));
        Assert.Equal(FfProbe, AfterProbe(true, null));
        Assert.Equal(AioStreamsGuessed, AfterProbe(false, AioStreamsGuessed));
        Assert.Null(AfterProbe(false, null));
    }

    private static Func<bool> Video(bool has) => () => has;

    private static readonly Func<bool> MustNotQuery = () =>
        throw new InvalidOperationException("the media-stream lookup must not run");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecideWrite_NewRow_Writes_WithoutQuerying(bool trusted)
    {
        Assert.Equal(MediaInfoWrite.Write, DecideWrite(true, null, MustNotQuery, trusted));
    }

    [Fact]
    public void DecideWrite_LegacyRowAlreadyProbed_IsStamped()
    {
        Assert.Equal(MediaInfoWrite.StampFfProbe, DecideWrite(false, null, Video(true), true));
    }

    [Fact]
    public void DecideWrite_LegacyRowWithoutMediaInfo_Writes()
    {
        Assert.Equal(MediaInfoWrite.Write, DecideWrite(false, null, Video(false), false));
    }

    [Theory]
    [InlineData(FfProbe)]
    [InlineData(AioStreamsProbed)]
    public void DecideWrite_TrustedRow_IsLeftAlone_WithoutQuerying(string provenance)
    {
        Assert.Equal(MediaInfoWrite.Skip, DecideWrite(false, provenance, MustNotQuery, true));
    }

    [Fact]
    public void DecideWrite_GuessedRow_UpgradesOnlyToTrustedData()
    {
        Assert.Equal(
            MediaInfoWrite.Write,
            DecideWrite(false, AioStreamsGuessed, MustNotQuery, true)
        );
        Assert.Equal(
            MediaInfoWrite.Skip,
            DecideWrite(false, AioStreamsGuessed, MustNotQuery, false)
        );
    }
}

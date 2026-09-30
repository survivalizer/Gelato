using Gelato.Streams;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using static Gelato.Streams.MediaInfoProvenance;

namespace Gelato.Tests.Streams;

/// <summary>
/// The probe decision reads the stream row's own provenance and notice flag. These tests
/// prove it reads the keys SyncStreams writes.
/// </summary>
public class ProbeGateTests
{
    private static Movie Row(string? provenance = null, bool notice = false)
    {
        var row = new Movie();
        if (provenance is not null)
            row.SetGelatoData(Key, provenance);
        if (notice)
            row.SetGelatoData(StreamClassifier.NoticeKey, true);
        return row;
    }

    private static MediaSourceInfo Source(bool hasVideo = true, long? minutes = 90) =>
        new()
        {
            MediaStreams = hasVideo
                ? new List<MediaStream> { new() { Type = MediaStreamType.Video } }
                : new List<MediaStream>(),
            RunTimeTicks = minutes is { } m ? TimeSpan.FromMinutes(m).Ticks : null,
        };

    [Fact]
    public void GuessedRow_IsProbed()
    {
        Assert.True(ProbeGate.ShouldProbe(Row(AioStreamsGuessed), Source()));
    }

    [Theory]
    [InlineData(AioStreamsProbed)]
    [InlineData(FfProbe)]
    public void TrustedRow_IsNotProbed(string provenance)
    {
        Assert.False(ProbeGate.ShouldProbe(Row(provenance), Source()));
    }

    [Fact]
    public void LegacyRowWithoutVideo_IsProbed_AsToday()
    {
        Assert.True(ProbeGate.ShouldProbe(Row(), Source(hasVideo: false)));
    }

    [Fact]
    public void Notice_IsNeverProbed()
    {
        Assert.False(ProbeGate.ShouldProbe(Row(notice: true), Source(false, null)));
    }

    [Fact]
    public void SuccessfulProbe_MarksTheRowAsProbed()
    {
        var row = Row(AioStreamsGuessed);

        ProbeGate.RecordProbe(row, succeeded: true);

        Assert.Equal(FfProbe, row.GelatoData<string>(Key));
    }

    [Fact]
    public void FailedProbe_LeavesTheGuessToBeProbedAgain()
    {
        var row = Row(AioStreamsGuessed);

        ProbeGate.RecordProbe(row, succeeded: false);

        Assert.Equal(AioStreamsGuessed, row.GelatoData<string>(Key));
    }
}

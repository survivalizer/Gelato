using System.Text.Json;
using Gelato.Streams;
using MediaBrowser.Model.Entities;
using Xunit.Abstractions;

namespace Gelato.Tests.Streams;

/// <summary>
/// Real AIOStreams responses (scrubbed by scripts/capture-aiostreams-fixture.sh) through the
/// whole pipeline: parse, classify, order, build. It also reports how often AIOStreams' data
/// is probe quality - the spec's open question.
/// </summary>
public class AioStreamsFixtureTests(ITestOutputHelper output)
{
    private static List<StremioStream> Load(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Streams", "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Capture the fixture first (plan Task 8): {path}");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer
            .Deserialize<StremioStreamsResponse>(File.ReadAllText(path), opts)!
            .Streams;
    }

    [Fact]
    public void RealResponse_CarriesStreamData()
    {
        Assert.Contains(
            Load("aiostreams-movie.json"),
            s => StreamDataMapper.TryMap(s.StreamData) is not null
        );
    }

    [Fact]
    public void RealResponse_PlansWithNoticesLast()
    {
        var planned = StreamClassifier.PlanSync(Load("aiostreams-movie.json"), p2pEnabled: true);

        var firstNotice = planned.ToList().FindIndex(e => e.IsNotice);
        if (firstNotice >= 0)
            Assert.All(planned.Skip(firstNotice), e => Assert.True(e.IsNotice));
        Assert.True(StreamClassifier.PlayableCount(planned) > 0);
    }

    [Fact]
    public void RealResponse_BuildsMediaInfoForEveryPlayableVersion()
    {
        var playable = StreamClassifier
            .PlanSync(Load("aiostreams-movie.json"), p2pEnabled: true)
            .Where(e => !e.IsNotice && e.Data is not null)
            .ToList();

        var built = playable
            .Select(e => StreamMediaInfo.Build(e.Data!, e.Stream.Url, _ => null))
            .ToList();

        Assert.All(built, b => Assert.Equal(MediaStreamType.Video, b.Streams[0].Type));

        var trusted = built.Count(b => b.IsTrusted);
        output.WriteLine(
            $"{built.Count} playable versions with stream data; {trusted} trusted "
                + $"({(built.Count == 0 ? 0 : 100.0 * trusted / built.Count):F0}%)"
        );
    }

    [Fact]
    public void ReconfigureErrorResponse_IsASingleUnplayableNotice()
    {
        var planned = StreamClassifier.PlanSync(
            Load("aiostreams-reconfigure-error.json"),
            p2pEnabled: true
        );

        var entry = Assert.Single(planned);
        Assert.True(entry.IsNotice);
        Assert.Equal(0, StreamClassifier.PlayableCount(planned));
    }
}

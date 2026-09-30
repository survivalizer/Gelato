using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

/// <summary>Applies the provenance rules to the stream row a playback request resolves to.</summary>
public static class ProbeGate
{
    public static bool ShouldProbe(BaseItem owner, MediaSourceInfo selected) =>
        MediaInfoProvenance.ShouldProbe(
            owner.GelatoData<bool?>(StreamClassifier.NoticeKey) == true,
            owner.GelatoData<string>(MediaInfoProvenance.Key),
            selected.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) ?? false,
            selected.RunTimeTicks
        );

    public static void RecordProbe(BaseItem owner, bool succeeded)
    {
        var before = owner.GelatoData<string>(MediaInfoProvenance.Key);
        var after = MediaInfoProvenance.AfterProbe(succeeded, before);

        if (after is not null && after != before)
            owner.SetGelatoData(MediaInfoProvenance.Key, after);
    }
}

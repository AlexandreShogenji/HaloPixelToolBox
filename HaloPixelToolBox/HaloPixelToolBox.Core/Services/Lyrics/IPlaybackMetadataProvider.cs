using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public interface IPlaybackMetadataProvider
{
    Task<LyricsPlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

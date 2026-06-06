using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public interface ILyricsLiveLineProvider
{
    LyricsProviderKind ProviderKind { get; }

    Task<LyricsTrack?> ReadCurrentLineAsync(CancellationToken cancellationToken = default);
}

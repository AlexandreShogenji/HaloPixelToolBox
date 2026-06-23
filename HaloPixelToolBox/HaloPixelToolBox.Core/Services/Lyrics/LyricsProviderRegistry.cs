using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LyricsProviderRegistry
{
    private readonly Dictionary<LyricsProviderKind, ILyricsProvider> providers = new();

    public LyricsProviderRegistry()
        : this(new SpotifyMediaSessionPlaybackProvider())
    {
    }

    public LyricsProviderRegistry(IPlaybackMetadataProvider spotifyPlaybackProvider)
    {
        Register(new NetEaseCloudMusicLiveLineProvider());
        Register(new QQMusicLiveLineProvider(new QQMusicMediaSessionPlaybackProvider()));
        Register(new SpotifyLyricsProvider(spotifyPlaybackProvider));
        Register(new LocalFileLyricsProvider());
        Register(new PlaceholderLyricsProvider(LyricsProviderKind.Custom));
    }

    public void Register(ILyricsProvider provider) => providers[provider.ProviderKind] = provider;

    public ILyricsProvider GetProvider(LyricsProviderKind kind) => providers[kind];
}

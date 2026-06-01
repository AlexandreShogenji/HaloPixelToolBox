using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LyricsProviderRegistry
{
    private readonly Dictionary<LyricsProviderKind, ILyricsProvider> providers = new();

    public LyricsProviderRegistry()
    {
        Register(new PlaceholderLyricsProvider(LyricsProviderKind.NetEaseCloudMusic));
        Register(new PlaceholderLyricsProvider(LyricsProviderKind.QQMusic));
        Register(new PlaceholderLyricsProvider(LyricsProviderKind.LocalFile));
        Register(new PlaceholderLyricsProvider(LyricsProviderKind.Custom));
    }

    public void Register(ILyricsProvider provider) => providers[provider.ProviderKind] = provider;

    public ILyricsProvider GetProvider(LyricsProviderKind kind) => providers[kind];
}

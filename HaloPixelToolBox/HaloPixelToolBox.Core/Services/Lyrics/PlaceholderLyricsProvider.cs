using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class PlaceholderLyricsProvider : ILyricsProvider
{
    public LyricsProviderKind ProviderKind { get; }

    public PlaceholderLyricsProvider(LyricsProviderKind providerKind)
    {
        ProviderKind = providerKind;
    }

    public Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        // TODO: 后续在这里接入具体平台歌词接口、签名、Cookie、客户端版本适配。
        return Task.FromResult<LyricsTrack?>(new LyricsTrack
        {
            Title = string.IsNullOrWhiteSpace(query.Keyword) ? $"{ProviderKind} 歌词占位" : query.Keyword,
            Artist = "未接入平台",
            Lines =
            {
                new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3), Text = "歌词接口占位，等待平台适配" },
                new() { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(6), Text = "后续可接入网易云、QQ音乐等来源" }
            }
        });
    }
}

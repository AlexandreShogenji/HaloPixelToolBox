using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

/// <summary>
/// 多平台歌词 Provider 扩展点。网易云、QQ 音乐等平台的抓取与版本适配在实现类中完成。
/// </summary>
public interface ILyricsProvider
{
    LyricsProviderKind ProviderKind { get; }

    Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default);
}

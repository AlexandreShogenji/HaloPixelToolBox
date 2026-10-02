using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services.Lyrics;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BilibiliMusicSubtitleSource
{
    private readonly BrowserMusicLyricsService lyricsService;

    public BilibiliMusicSubtitleSource(BrowserMusicLyricsService lyricsService)
    {
        this.lyricsService = lyricsService;
    }

    public async Task<BrowserSubtitleSourceResult> LoadAsync(
        LyricsQuery query,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title) && string.IsNullOrWhiteSpace(query.Keyword))
        {
            reportStatus("B 站音乐模式尚未填写歌名，无法查询同步歌词");
            return BrowserSubtitleSourceResult.AsrStreaming();
        }

        reportStatus($"正在查询同步歌词：歌名={query.Title}；歌手={query.Artist}；关键词={query.Keyword}".Trim());
        var track = await lyricsService.SearchAsync(query, cancellationToken);
        if (track is null || !track.IsSynced || track.Lines.Count == 0)
        {
            reportStatus($"未命中同步歌词：歌名={query.Title}；歌手={query.Artist}；关键词={query.Keyword}".Trim());
            return BrowserSubtitleSourceResult.AsrStreaming();
        }

        track.SourceName = $"B 站音乐 + {track.SourceName}";
        track.Provider = LyricsProviderKind.Custom;
        if (string.IsNullOrWhiteSpace(track.Title))
            track.Title = query.Title;
        if (string.IsNullOrWhiteSpace(track.Artist))
            track.Artist = query.Artist;

        var displayTitle = string.IsNullOrWhiteSpace(query.Title) ? track.Title : query.Title;
        var displayArtist = string.IsNullOrWhiteSpace(query.Artist) ? track.Artist : query.Artist;
        reportStatus($"已命中 {track.SourceName} 同步歌词：{displayTitle} {displayArtist}".Trim());

        return BrowserSubtitleSourceResult.Timeline(
            track.SourceName,
            track.Lines.ToList(),
            displayTitle,
            displayArtist);
    }
}

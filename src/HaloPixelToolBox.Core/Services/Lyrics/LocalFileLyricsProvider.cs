using HaloPixelToolBox.Core.Models.Subtitles;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public class LocalFileLyricsProvider : ILyricsProvider
{
    private readonly LrcLyricsParser parser = new();

    public LyricsProviderKind ProviderKind => LyricsProviderKind.LocalFile;

    public Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = ResolveFilePath(query);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("请选择本地 LRC 歌词文件");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("未找到本地歌词文件", filePath);

        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("本地歌词目前支持 .lrc 和 .txt 文件");

        var track = parser.ParseFile(filePath);
        track.Provider = ProviderKind;
        if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(query.Keyword))
            track.Title = query.Keyword;

        return Task.FromResult<LyricsTrack?>(track);
    }

    private static string? ResolveFilePath(LyricsQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.FilePath))
            return query.FilePath;

        if (!string.IsNullOrWhiteSpace(query.SourceUrl))
        {
            if (Uri.TryCreate(query.SourceUrl, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;

            if (File.Exists(query.SourceUrl))
                return query.SourceUrl;
        }

        return File.Exists(query.Keyword) ? query.Keyword : null;
    }
}

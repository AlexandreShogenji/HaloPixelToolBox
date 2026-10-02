using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Net;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public interface IBrowserMusicMetadataResolver
{
    BrowserMusicPlatform Platform { get; }

    bool CanResolve(BrowserVideoPlaybackSnapshot snapshot);

    BrowserMusicTrackMetadata Resolve(BrowserVideoPlaybackSnapshot snapshot);
}

public sealed class BrowserMusicMetadataResolver
{
    private readonly IReadOnlyList<IBrowserMusicMetadataResolver> resolvers;

    public BrowserMusicMetadataResolver()
        : this([new BilibiliBrowserMusicMetadataResolver()])
    {
    }

    public BrowserMusicMetadataResolver(IReadOnlyList<IBrowserMusicMetadataResolver> resolvers)
    {
        this.resolvers = resolvers;
    }

    public BrowserMusicTrackMetadata Resolve(BrowserVideoPlaybackSnapshot snapshot)
    {
        var resolver = resolvers.FirstOrDefault(resolver => resolver.CanResolve(snapshot));
        return resolver?.Resolve(snapshot) ?? new BrowserMusicTrackMetadata
        {
            Platform = BrowserMusicPlatform.Unknown,
            SourceTitle = CleanBrowserTitle(snapshot.Title),
            SourceUrl = snapshot.Url,
            Title = CleanBrowserTitle(snapshot.Title),
            Duration = snapshot.Duration
        };
    }

    private static string CleanBrowserTitle(string value)
    {
        return Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty), @"\s+", " ").Trim();
    }
}

public sealed partial class BilibiliBrowserMusicMetadataResolver : IBrowserMusicMetadataResolver
{
    private const int MaxArtistNameLength = 48;

    private static readonly string[] BilibiliTitleSuffixes =
    [
        "_哔哩哔哩_bilibili",
        "-哔哩哔哩_Bilibili",
        "- 哔哩哔哩_bilibili",
        " - 哔哩哔哩",
        " 哔哩哔哩 bilibili"
    ];

    private static readonly string[] ArtistTitleSeparators =
    [
        " - ",
        " — ",
        " – ",
        " | ",
        "｜",
        "：",
        ":"
    ];

    private static readonly string[] TitleArtistSeparators =
    [
        " ／ ",
        "／",
        " / ",
        "/"
    ];

    public BrowserMusicPlatform Platform => BrowserMusicPlatform.Bilibili;

    public bool CanResolve(BrowserVideoPlaybackSnapshot snapshot)
    {
        return BilibiliVideoUrlHelper.IsBilibiliVideoLike(snapshot.Url);
    }

    public BrowserMusicTrackMetadata Resolve(BrowserVideoPlaybackSnapshot snapshot)
    {
        var sourceTitle = CleanBilibiliTitle(snapshot.Title);
        var (title, artist) = GuessTitleAndArtist(sourceTitle);
        var pageMusicTitle = CleanSongTitle(snapshot.MusicTitle);
        var pageMusicArtist = CleanArtistName(snapshot.MusicArtist);
        var pageMusicAlbum = NormalizeSpaces(WebUtility.HtmlDecode(snapshot.MusicAlbum ?? string.Empty));
        var hasPageMusicTitle = !string.IsNullOrWhiteSpace(pageMusicTitle);
        var hasPageMusicArtist = !string.IsNullOrWhiteSpace(pageMusicArtist);
        var hasPageMusicAlbum = !string.IsNullOrWhiteSpace(pageMusicAlbum);
        if (!string.IsNullOrWhiteSpace(pageMusicTitle))
            title = pageMusicTitle;
        if (!string.IsNullOrWhiteSpace(pageMusicArtist))
            artist = pageMusicArtist;

        return new BrowserMusicTrackMetadata
        {
            Platform = BrowserMusicPlatform.Bilibili,
            SourceTitle = sourceTitle,
            SourceUrl = snapshot.Url,
            Title = title,
            Artist = artist,
            Album = pageMusicAlbum,
            Duration = snapshot.Duration,
            PlatformTrackId = BilibiliVideoUrlHelper.ExtractBvid(snapshot.Url),
            HasPageMusicTitle = hasPageMusicTitle,
            HasPageMusicArtist = hasPageMusicArtist,
            HasPageMusicAlbum = hasPageMusicAlbum
        };
    }

    private static string CleanBilibiliTitle(string value)
    {
        var title = Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty), @"\s+", " ").Trim();
        foreach (var suffix in BilibiliTitleSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length].Trim();
                break;
            }
        }

        title = BilibiliSuffixRegex().Replace(title, string.Empty);
        return title.Trim();
    }

    private static (string Title, string Artist) GuessTitleAndArtist(string sourceTitle)
    {
        var normalized = StripLeadingTags(sourceTitle);
        var quotedTitle = QuotedTitleRegex().Match(normalized);
        if (quotedTitle.Success)
        {
            var title = CleanSongTitle(quotedTitle.Groups["title"].Value);
            var artist = CleanArtistName(normalized[..quotedTitle.Index]);
            if (IsNoisyArtistTag(artist))
                artist = CleanArtistName(normalized[(quotedTitle.Index + quotedTitle.Length)..]);
            if (IsNoisyArtistTag(artist))
                artist = string.Empty;
            return (string.IsNullOrWhiteSpace(title) ? normalized : title, artist);
        }

        var leadingArtistTitle = Regex.Match(
            sourceTitle,
            @"^\s*[【\[\(（](?<artist>[^】\]\)）]{1,80})[】\]\)）]\s*(?<title>[^【\[\(（]{1,120})");
        if (leadingArtistTitle.Success)
        {
            var title = CleanSongTitle(leadingArtistTitle.Groups["title"].Value);
            var artist = CleanArtistName(SimplifyArtistAlias(leadingArtistTitle.Groups["artist"].Value));
            if (!string.IsNullOrWhiteSpace(title) && !IsNoisyArtistTag(artist))
                return (title, artist);
        }

        var titleArtist = GuessTitleThenArtist(normalized);
        if (!string.IsNullOrWhiteSpace(titleArtist.Title) && !string.IsNullOrWhiteSpace(titleArtist.Artist))
            return titleArtist;

        foreach (var separator in ArtistTitleSeparators)
        {
            var separatorIndex = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex >= normalized.Length - separator.Length)
                continue;

            var left = CleanArtistName(normalized[..separatorIndex]);
            var right = CleanSongTitle(normalized[(separatorIndex + separator.Length)..]);
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || IsNoisyArtistTag(left))
                continue;

            return (right, left);
        }

        return (CleanSongTitle(normalized), string.Empty);
    }

    private static (string Title, string Artist) GuessTitleThenArtist(string normalizedTitle)
    {
        foreach (var separator in TitleArtistSeparators)
        {
            var separatorIndex = normalizedTitle.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex >= normalizedTitle.Length - separator.Length)
                continue;

            var title = CleanSongTitle(normalizedTitle[..separatorIndex]);
            var artist = CleanArtistName(normalizedTitle[(separatorIndex + separator.Length)..]);
            if (string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(artist)
                || IsNoisyArtistTag(artist))
            {
                continue;
            }

            return (title, artist);
        }

        return (string.Empty, string.Empty);
    }

    private static string StripLeadingTags(string value)
    {
        var result = value.Trim();
        while (true)
        {
            var updated = LeadingTagRegex().Replace(result, string.Empty, 1).Trim();
            if (string.IsNullOrWhiteSpace(updated))
                return result;

            if (updated.Length == result.Length)
                return result;

            result = updated;
        }
    }

    private static string CleanSongTitle(string value)
    {
        var rawValue = value ?? string.Empty;
        var discoveredSong = BilibiliDiscoveredSongRegex().Match(rawValue);
        if (discoveredSong.Success)
            rawValue = discoveredSong.Groups["title"].Value;
        else
        {
            var quotedSong = QuotedTitleRegex().Match(rawValue);
            if (quotedSong.Success)
                rawValue = quotedSong.Groups["title"].Value;
        }

        var result = StripLeadingTags(rawValue);
        result = SongTitleNoiseRegex().Replace(result, string.Empty);
        return TrimDecorativeSongTitlePunctuation(NormalizeSpaces(result));
    }

    private static string CleanArtistName(string value)
    {
        var result = ArtistNoiseRegex().Replace(value, string.Empty);
        result = TrailingArtistCreditRegex().Replace(result, string.Empty);
        result = NormalizeSpaces(result).Trim('-', '–', '—', '|', '｜', '/', '／', ':', '：', ' ');
        return IsNoisyArtistTag(result) ? string.Empty : result;
    }

    private static string SimplifyArtistAlias(string value)
    {
        var normalized = NormalizeSpaces(value);
        foreach (var separator in new[] { " / ", "/", "／", "|", "｜", "、", "," })
        {
            var index = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
                return normalized[..index].Trim();
        }

        return normalized;
    }

    private static bool IsNoisyArtistTag(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = NormalizeSpaces(value);
        if (normalized.Length > MaxArtistNameLength)
            return true;

        return NoisyArtistTagRegex().IsMatch(normalized);
    }

    private static string NormalizeSpaces(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string TrimDecorativeSongTitlePunctuation(string value)
    {
        var result = value.Trim();
        while (result.Length > 1 && result.EndsWith('。'))
            result = result[..^1].Trim();

        return result;
    }

    [GeneratedRegex(@"\s*[-_]\s*哔哩哔哩.*$", RegexOptions.IgnoreCase)]
    private static partial Regex BilibiliSuffixRegex();

    [GeneratedRegex(@"^[\s【\[][^\]】]{1,30}[\]】]\s*")]
    private static partial Regex LeadingTagRegex();

    [GeneratedRegex(@"[《「『""“](?<title>[^》」』""”]{1,80})[》」』""”]")]
    private static partial Regex QuotedTitleRegex();

    [GeneratedRegex(@"发现\s*[《「『""“]\s*(?<title>[^》」』""”]{1,120})\s*[》」』""”]")]
    private static partial Regex BilibiliDiscoveredSongRegex();

    [GeneratedRegex(@"\b(official|music\s*video|mv|live|lyrics?|audio)\b|官方|完整版|中日字幕|中文字幕|动态歌词|歌词", RegexOptions.IgnoreCase)]
    private static partial Regex SongTitleNoiseRegex();

    [GeneratedRegex(@"演唱|歌手|作词|作曲|翻唱|cover|feat\.?|ft\.?", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistNoiseRegex();

    [GeneratedRegex(@"\b(4k|8k|60\s*fps|120\s*fps|fps|hi[-\s]?res|hdr|uhd|dolby|live|mv|pv|official|lyrics?|audio)\b|字幕|中字|中日|翻译|KTV|官方|完整版|投屏|合集|剪辑|现场|直拍|饭拍|舞台|四周年|周年|第[一二三四五六七八九十0-9]+天|第一天|第二天|第三天|DAY\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex NoisyArtistTagRegex();

    [GeneratedRegex(@"\s*[【\[\(（][^】\]\)）]{1,40}[】\]\)）]\s*$")]
    private static partial Regex TrailingArtistCreditRegex();
}

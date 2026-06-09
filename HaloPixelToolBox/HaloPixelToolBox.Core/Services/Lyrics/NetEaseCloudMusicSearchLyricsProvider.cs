using HaloPixelToolBox.Core.Models.Subtitles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class NetEaseCloudMusicSearchLyricsProvider
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LrcLyricsParser parser = new();

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        var candidates = await SearchCandidatesAsync(query, cancellationToken);
        foreach (var candidate in candidates
                     .Where(candidate => IsReasonableCandidate(query, candidate))
                     .OrderByDescending(candidate => ScoreCandidate(query, candidate))
                     .Take(8))
        {
            var lyric = await TryGetLyricAsync(candidate.Id, cancellationToken);
            if (string.IsNullOrWhiteSpace(lyric) || !lyric.Contains('['))
                continue;

            var track = parser.Parse(lyric, candidate.Name);
            if (track.Lines.Count == 0 || !track.IsSynced)
                continue;

            track.Provider = LyricsProviderKind.Custom;
            track.SourceName = "网易云音乐";
            track.Title = candidate.Name;
            track.Artist = string.Join("/", candidate.Artists.Select(artist => artist.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            track.Album = candidate.Album?.Name ?? string.Empty;
            track.Duration = candidate.Duration > 0 ? TimeSpan.FromMilliseconds(candidate.Duration) : track.Duration;
            track.Confidence = ScoreCandidate(query, candidate);
            track.RawSource = lyric;
            return track;
        }

        return null;
    }

    private static async Task<IReadOnlyList<NetEaseSong>> SearchCandidatesAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var songs = new List<NetEaseSong>();
        foreach (var keyword in BuildSearchKeywords(query))
        {
            var uri = new Uri($"https://music.163.com/api/search/get/web?csrf_token=&type=1&offset=0&total=true&limit=10&s={Uri.EscapeDataString(keyword)}");
            using var response = await HttpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                continue;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<NetEaseSearchResponse>(json, JsonOptions);
            if (result?.Result?.Songs is { Count: > 0 } resultSongs)
                songs.AddRange(resultSongs);
        }

        return songs
            .DistinctBy(song => song.Id)
            .ToList();
    }

    private static IEnumerable<string> BuildSearchKeywords(LyricsQuery query)
    {
        var title = query.Title.Trim();
        var artist = query.Artist.Trim();
        var keyword = query.Keyword.Trim();

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            yield return $"{title} {artist}";

        if (!string.IsNullOrWhiteSpace(keyword))
            yield return keyword;

        if (!string.IsNullOrWhiteSpace(title))
            yield return title;
    }

    private static async Task<string?> TryGetLyricAsync(long songId, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1");
        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<NetEaseLyricResponse>(json, JsonOptions)?.Lrc?.Lyric;
    }

    private static bool IsReasonableCandidate(LyricsQuery query, NetEaseSong candidate)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
            return true;

        return TextMatchScore(query.Title, candidate.Name) >= 0.45
            || TextMatchScore(SimplifyTitle(query.Title), candidate.Name) >= 0.45;
    }

    private static double ScoreCandidate(LyricsQuery query, NetEaseSong candidate)
    {
        var titleScore = Math.Max(
            TextMatchScore(query.Title, candidate.Name),
            TextMatchScore(SimplifyTitle(query.Title), candidate.Name));
        var artistScore = candidate.Artists
            .Select(artist => TextMatchScore(query.Artist, artist.Name))
            .DefaultIfEmpty(0)
            .Max();

        var score = titleScore * 0.5 + artistScore * 0.2;
        if (query.Duration is { } duration && candidate.Duration > 0)
        {
            var diff = Math.Abs(duration.TotalMilliseconds - candidate.Duration) / 1000;
            score += Math.Max(0, 0.3 - diff / 120);
        }

        return Math.Clamp(score, 0, 1);
    }

    private static double TextMatchScore(string? queryText, string? candidateText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        var query = NormalizeText(queryText);
        var candidate = NormalizeText(candidateText);
        if (query.Length == 0 || candidate.Length == 0)
            return 0;

        if (query.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (query.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.75;

        return 0;
    }

    private static string NormalizeText(string value)
    {
        return new string(value
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            .ToArray())
            .Trim();
    }

    private static string SimplifyTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var markers = new[] { " - ", "(", "（", "[", "【" };
        var end = value.Length;
        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0)
                end = Math.Min(end, index);
        }

        return value[..end].Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 HaloPixelToolBox/1.2.7");
        httpClient.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return httpClient;
    }

    private sealed class NetEaseSearchResponse
    {
        [JsonPropertyName("result")]
        public NetEaseSearchResult? Result { get; init; }
    }

    private sealed class NetEaseSearchResult
    {
        [JsonPropertyName("songs")]
        public List<NetEaseSong> Songs { get; init; } = [];
    }

    private sealed class NetEaseSong
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("duration")]
        public double Duration { get; init; }

        [JsonPropertyName("artists")]
        public List<NetEaseArtist> Artists { get; init; } = [];

        [JsonPropertyName("album")]
        public NetEaseAlbum? Album { get; init; }
    }

    private sealed class NetEaseArtist
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class NetEaseAlbum
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class NetEaseLyricResponse
    {
        [JsonPropertyName("lrc")]
        public NetEaseLyric? Lrc { get; init; }
    }

    private sealed class NetEaseLyric
    {
        [JsonPropertyName("lyric")]
        public string Lyric { get; init; } = string.Empty;
    }
}

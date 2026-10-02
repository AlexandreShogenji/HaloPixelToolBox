using HaloPixelToolBox.Core.Models.Subtitles;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class LrcLibLyricsProvider
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LrcLyricsParser parser = new();

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Title) && string.IsNullOrWhiteSpace(query.Keyword))
            return null;

        var lyric = await TryGetExactAsync(query, cancellationToken);
        lyric ??= await TrySearchBestAsync(query, cancellationToken);
        if (lyric is null)
            return null;

        var rawLyrics = query.PreferSyncedLyrics && !string.IsNullOrWhiteSpace(lyric.SyncedLyrics)
            ? lyric.SyncedLyrics
            : lyric.PlainLyrics;
        if (string.IsNullOrWhiteSpace(rawLyrics))
            return null;

        var track = parser.Parse(rawLyrics, lyric.TrackName);
        track.Provider = LyricsProviderKind.Custom;
        track.SourceName = "LRCLIB";
        track.Title = lyric.TrackName;
        track.Artist = lyric.ArtistName;
        track.Album = lyric.AlbumName;
        track.Duration = TimeSpan.FromSeconds(Math.Max(1, lyric.Duration));
        track.IsSynced = !string.IsNullOrWhiteSpace(lyric.SyncedLyrics) && rawLyrics == lyric.SyncedLyrics;
        track.RawSource = rawLyrics;
        track.Confidence = ScoreLyric(query, lyric);
        return track;
    }

    private static async Task<LrcLibLyric?> TryGetExactAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || string.IsNullOrWhiteSpace(query.Artist))
            return null;

        var parameters = new Dictionary<string, string>
        {
            ["track_name"] = query.Title,
            ["artist_name"] = query.Artist
        };

        if (!string.IsNullOrWhiteSpace(query.Album))
            parameters["album_name"] = query.Album;

        if (query.Duration is { } duration)
            parameters["duration"] = Math.Round(duration.TotalSeconds).ToString("0");

        var json = await TryGetJsonAsync(BuildUri("/api/get", parameters), cancellationToken, throwOnUnexpectedStatus: false);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<LrcLibLyric>(json, JsonOptions);
    }

    private static async Task<LrcLibLyric?> TrySearchBestAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var lyrics = new List<LrcLibLyric>();
        foreach (var parameters in BuildSearchParameterAttempts(query))
        {
            var json = await TryGetJsonAsync(BuildUri("/api/search", parameters), cancellationToken, throwOnUnexpectedStatus: false);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            lyrics.AddRange(JsonSerializer.Deserialize<List<LrcLibLyric>>(json, JsonOptions) ?? []);
        }

        return lyrics
            .DistinctBy(lyric => $"{NormalizeText(lyric.TrackName)}|{NormalizeText(lyric.ArtistName)}|{Math.Round(lyric.Duration)}")
            .Where(lyric => !query.PreferSyncedLyrics || !string.IsNullOrWhiteSpace(lyric.SyncedLyrics))
            .OrderByDescending(lyric => ScoreLyric(query, lyric))
            .FirstOrDefault();
    }

    private static IEnumerable<Dictionary<string, string>> BuildSearchParameterAttempts(LyricsQuery query)
    {
        var title = query.Title.Trim();
        var simpleTitle = SimplifyTitle(title);
        var artist = query.Artist.Trim();
        var album = query.Album.Trim();
        var keyword = query.Keyword.Trim();

        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist))
        {
            var structured = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(title))
                structured["track_name"] = title;
            if (!string.IsNullOrWhiteSpace(artist))
                structured["artist_name"] = artist;
            if (!string.IsNullOrWhiteSpace(album))
                structured["album_name"] = album;
            yield return structured;
        }

        if (!string.IsNullOrWhiteSpace(keyword))
            yield return new Dictionary<string, string> { ["q"] = keyword };

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            yield return new Dictionary<string, string> { ["q"] = $"{title} {artist}" };

        if (!string.IsNullOrWhiteSpace(title))
        {
            yield return new Dictionary<string, string> { ["q"] = title };
            yield return new Dictionary<string, string> { ["track_name"] = title };
        }

        if (!simpleTitle.Equals(title, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(simpleTitle))
        {
            yield return new Dictionary<string, string> { ["q"] = simpleTitle };
            yield return new Dictionary<string, string> { ["track_name"] = simpleTitle };
        }
    }

    private static async Task<string?> TryGetJsonAsync(Uri uri, CancellationToken cancellationToken, bool throwOnUnexpectedStatus)
    {
        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode && !throwOnUnexpectedStatus)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static double ScoreLyric(LyricsQuery query, LrcLibLyric lyric)
    {
        var score = 0d;
        var titleScore = TextMatchScore(query.Title, lyric.TrackName);
        var simpleTitleScore = TextMatchScore(SimplifyTitle(query.Title), lyric.TrackName);
        score += Math.Max(titleScore, simpleTitleScore) * 0.45;
        score += TextMatchScore(query.Artist, lyric.ArtistName) * 0.2;
        if (TextEquals(query.Album, lyric.AlbumName))
            score += 0.1;
        if (!string.IsNullOrWhiteSpace(lyric.SyncedLyrics))
            score += 0.2;
        if (query.Duration is { } duration)
        {
            var diff = Math.Abs(duration.TotalSeconds - lyric.Duration);
            score += Math.Max(0, 0.2 - diff / 90);
        }

        return Math.Clamp(score, 0, 1);
    }

    private static double TextMatchScore(string? queryText, string? lyricText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(lyricText))
            return 0;

        var query = NormalizeText(queryText);
        var lyric = NormalizeText(lyricText);
        if (query.Length == 0 || lyric.Length == 0)
            return 0;

        if (query.Equals(lyric, StringComparison.OrdinalIgnoreCase))
            return 1;

        if (query.Contains(lyric, StringComparison.OrdinalIgnoreCase) || lyric.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.75;

        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lyricTokens = lyric.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.Length == 0 || lyricTokens.Length == 0)
            return 0;

        var matchedTokens = queryTokens.Count(token => lyricTokens.Any(candidate => candidate.Equals(token, StringComparison.OrdinalIgnoreCase)));
        return matchedTokens == 0 ? 0 : (double)matchedTokens / queryTokens.Length * 0.5;
    }

    private static bool TextEquals(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && NormalizeText(left).Equals(NormalizeText(right), StringComparison.OrdinalIgnoreCase);
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

    private static Uri BuildUri(string path, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"https://lrclib.net{path}?{query}");
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HaloPixelToolBox/1.2.7");
        return httpClient;
    }

    private sealed class LrcLibLyric
    {
        [JsonPropertyName("trackName")]
        public string TrackName { get; init; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; init; } = string.Empty;

        [JsonPropertyName("albumName")]
        public string AlbumName { get; init; } = string.Empty;

        [JsonPropertyName("duration")]
        public double Duration { get; init; }

        [JsonPropertyName("plainLyrics")]
        public string PlainLyrics { get; init; } = string.Empty;

        [JsonPropertyName("syncedLyrics")]
        public string SyncedLyrics { get; init; } = string.Empty;
    }
}

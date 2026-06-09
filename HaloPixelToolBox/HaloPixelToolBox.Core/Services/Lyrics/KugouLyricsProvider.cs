using HaloPixelToolBox.Core.Models.Subtitles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloPixelToolBox.Core.Services.Lyrics;

public sealed class KugouLyricsProvider
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LrcLyricsParser parser = new();

    public async Task<LyricsTrack?> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        foreach (var keyword in BuildSearchKeywords(query))
        {
            var candidates = await SearchCandidatesAsync(keyword, query, cancellationToken);
            foreach (var candidate in candidates
                         .OrderByDescending(candidate => ScoreCandidate(query, candidate))
                         .Take(5))
            {
                var lyric = await TryDownloadLyricAsync(candidate, cancellationToken);
                if (string.IsNullOrWhiteSpace(lyric) || !lyric.Contains('['))
                    continue;

                var track = parser.Parse(lyric, candidate.Song);
                if (track.Lines.Count == 0 || !track.IsSynced)
                    continue;

                track.Provider = LyricsProviderKind.Custom;
                track.SourceName = "酷狗音乐";
                track.Title = string.IsNullOrWhiteSpace(track.Title) ? candidate.Song : track.Title;
                track.Duration = candidate.Duration > 0 ? TimeSpan.FromMilliseconds(candidate.Duration) : track.Duration;
                track.Confidence = ScoreCandidate(query, candidate);
                track.RawSource = lyric;
                return track;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<KugouLyricCandidate>> SearchCandidatesAsync(string keyword, LyricsQuery query, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["ver"] = "1",
            ["man"] = "yes",
            ["client"] = "pc",
            ["keyword"] = keyword,
            ["duration"] = query.Duration is { } duration ? Math.Round(duration.TotalMilliseconds).ToString("0") : "0",
            ["hash"] = string.Empty
        };
        using var response = await HttpClient.GetAsync(BuildUri("https://lyrics.kugou.com/search", parameters), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<KugouSearchResponse>(json, JsonOptions)?.Candidates ?? [];
    }

    private static async Task<string?> TryDownloadLyricAsync(KugouLyricCandidate candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.AccessKey))
            return null;

        var parameters = new Dictionary<string, string>
        {
            ["ver"] = "1",
            ["client"] = "pc",
            ["id"] = candidate.Id,
            ["accesskey"] = candidate.AccessKey,
            ["fmt"] = "lrc",
            ["charset"] = "utf8"
        };
        using var response = await HttpClient.GetAsync(BuildUri("https://lyrics.kugou.com/download", parameters), cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var download = JsonSerializer.Deserialize<KugouDownloadResponse>(json, JsonOptions);
        if (string.IsNullOrWhiteSpace(download?.Content))
            return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(download.Content));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IEnumerable<string> BuildSearchKeywords(LyricsQuery query)
    {
        var title = query.Title.Trim();
        var artist = query.Artist.Trim();
        var keyword = query.Keyword.Trim();

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            yield return $"{title} {artist}";

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            yield return $"{artist} - {title}";

        if (!string.IsNullOrWhiteSpace(keyword))
            yield return keyword;

        if (!string.IsNullOrWhiteSpace(title))
            yield return title;
    }

    private static double ScoreCandidate(LyricsQuery query, KugouLyricCandidate candidate)
    {
        var score = Math.Max(TextMatchScore(query.Title, candidate.Song), TextMatchScore(SimplifyTitle(query.Title), candidate.Song)) * 0.55;
        if (candidate.Score > 0)
            score += Math.Min(candidate.Score / 100d, 1) * 0.15;

        if (query.Duration is { } duration && candidate.Duration > 0)
        {
            var diff = Math.Abs(duration.TotalMilliseconds - candidate.Duration) / 1000;
            score += Math.Max(0, 0.3 - diff / 90);
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

    private static Uri BuildUri(string baseUri, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{baseUri}?{query}");
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 HaloPixelToolBox/1.2.7");
        return httpClient;
    }

    private sealed class KugouSearchResponse
    {
        [JsonPropertyName("candidates")]
        public List<KugouLyricCandidate> Candidates { get; init; } = [];
    }

    private sealed class KugouLyricCandidate
    {
        [JsonPropertyName("id")]
        public JsonElement IdElement { get; init; }

        [JsonIgnore]
        public string Id => ReadString(IdElement);

        [JsonPropertyName("accesskey")]
        public string AccessKey { get; init; } = string.Empty;

        [JsonPropertyName("song")]
        public string Song { get; init; } = string.Empty;

        [JsonPropertyName("duration")]
        public JsonElement DurationElement { get; init; }

        [JsonIgnore]
        public double Duration => ReadDouble(DurationElement);

        [JsonPropertyName("score")]
        public JsonElement ScoreElement { get; init; }

        [JsonIgnore]
        public double Score => ReadDouble(ScoreElement);
    }

    private static string ReadString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            _ => string.Empty
        };
    }

    private static double ReadDouble(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(element.GetString(), out var value) => value,
            _ => 0
        };
    }

    private sealed class KugouDownloadResponse
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }
}

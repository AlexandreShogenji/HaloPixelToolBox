using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Core.Services.Translation;

public class BilibiliCcSubtitleCapture : IBrowserSubtitleCapture
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    ];

    public async IAsyncEnumerable<SubtitleCue> CaptureAsync(
        BrowserTranslationConfiguration configuration,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cues = await FetchCuesAsync(configuration, cancellationToken);
        if (cues.Count == 0)
            yield break;

        var timelineStart = DateTimeOffset.Now;
        foreach (var cue in cues)
        {
            var wait = timelineStart + cue.Start - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            yield return cue;
        }
    }

    public async Task<IReadOnlyList<SubtitleCue>> FetchCuesAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var bvid = BilibiliVideoUrlHelper.ExtractBvid(configuration.BilibiliVideoUrl);
        if (string.IsNullOrWhiteSpace(bvid))
            return [];

        var (imgKey, subKey) = await FetchWbiKeysAsync(cancellationToken);
        var videoInfo = await FetchJsonAsync(
            $"https://api.bilibili.com/x/web-interface/wbi/view?{EncodeWbi(new Dictionary<string, string> { ["bvid"] = bvid }, imgKey, subKey)}",
            cancellationToken);

        if (!videoInfo.RootElement.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        var cid = data.TryGetProperty("cid", out var cidElement) ? cidElement.GetInt64() : 0;
        if (cid <= 0 && data.TryGetProperty("pages", out var pages) && pages.GetArrayLength() > 0)
            cid = pages[0].GetProperty("cid").GetInt64();

        if (cid <= 0)
            return [];

        var playerParams = new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(CultureInfo.InvariantCulture),
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1",
            ["dm_img_list"] = "[]",
            ["dm_img_str"] = "V2ViR0wgMS4wIChPcGVuR0wgRVMgMi4wIENocm9taXVtKQ",
            ["dm_cover_img_str"] = "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgUlRYIDQwNjAgTGFwdG9wIEdQVSAoMHgwMDAwMjhFMCkgRGlyZWN0M0QxMSB2c181XzAgcHNfNV8wLCBEM0QxMSlHb29nbGUgSW5jLiAoTlZJRElBKQ",
            ["dm_img_inter"] = "{\"ds\":[],\"wh\":[5231,6067,75],\"of\":[475,950,475]}"
        };

        var playerData = await FetchJsonAsync(
            $"https://api.bilibili.com/x/player/wbi/v2?{EncodeWbi(playerParams, imgKey, subKey)}",
            cancellationToken);

        if (!playerData.RootElement.TryGetProperty("data", out var playerRoot)
            || !playerRoot.TryGetProperty("subtitle", out var subtitleRoot)
            || !subtitleRoot.TryGetProperty("subtitles", out var subtitles)
            || subtitles.GetArrayLength() == 0)
        {
            return [];
        }

        var selected = SelectSubtitle(subtitles, configuration.SourceLanguage);
        if (!selected.TryGetProperty("subtitle_url", out var subtitleUrlElement))
            return [];

        var subtitleUrl = NormalizeSubtitleUrl(subtitleUrlElement.GetString());
        if (string.IsNullOrWhiteSpace(subtitleUrl))
            return [];

        var subtitleJson = await FetchJsonAsync(subtitleUrl, cancellationToken);
        if (!subtitleJson.RootElement.TryGetProperty("body", out var body))
            return [];

        var cues = new List<SubtitleCue>();
        foreach (var item in body.EnumerateArray())
        {
            var text = item.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var from = item.TryGetProperty("from", out var fromElement) ? fromElement.GetDouble() : 0;
            var to = item.TryGetProperty("to", out var toElement) ? toElement.GetDouble() : from + 3;
            cues.Add(new SubtitleCue
            {
                Start = TimeSpan.FromSeconds(Math.Max(0, from)),
                End = TimeSpan.FromSeconds(Math.Max(from, to)),
                Text = text.Trim()
            });
        }

        return cues;
    }

    private static async Task<(string ImgKey, string SubKey)> FetchWbiKeysAsync(CancellationToken cancellationToken)
    {
        var nav = await FetchJsonAsync("https://api.bilibili.com/x/web-interface/nav", cancellationToken);
        var wbi = nav.RootElement.GetProperty("data").GetProperty("wbi_img");
        var imgKey = ExtractKeyFromUrl(wbi.GetProperty("img_url").GetString());
        var subKey = ExtractKeyFromUrl(wbi.GetProperty("sub_url").GetString());
        return (imgKey, subKey);
    }

    private static JsonElement SelectSubtitle(JsonElement subtitles, string sourceLanguage)
    {
        var normalizedSource = NormalizeLanguage(sourceLanguage);
        if (!string.IsNullOrWhiteSpace(normalizedSource) && normalizedSource != "auto")
        {
            foreach (var subtitle in subtitles.EnumerateArray())
            {
                var lan = subtitle.TryGetProperty("lan", out var lanElement) ? NormalizeLanguage(lanElement.GetString()) : string.Empty;
                if (lan == normalizedSource)
                    return subtitle;
            }
        }

        return subtitles[0];
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        var lower = language.Trim().ToLowerInvariant();
        if (lower.StartsWith("zh", StringComparison.Ordinal))
            return "zh";
        if (lower.StartsWith("ja", StringComparison.Ordinal) || lower.StartsWith("jp", StringComparison.Ordinal))
            return "ja";
        if (lower.StartsWith("en", StringComparison.Ordinal))
            return "en";
        return lower;
    }

    private static string? NormalizeSubtitleUrl(string? subtitleUrl)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl))
            return null;
        if (subtitleUrl.StartsWith("//", StringComparison.Ordinal))
            return "https:" + subtitleUrl;
        return subtitleUrl;
    }

    private static string ExtractKeyFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var fileName = url.Split('/').LastOrDefault() ?? string.Empty;
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string EncodeWbi(Dictionary<string, string> parameters, string imgKey, string subKey)
    {
        var mixinKey = GetMixinKey(imgKey + subKey);
        parameters["wts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var sorted = parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, SanitizeWbiValue(pair.Value)))
            .ToList();

        var query = string.Join("&", sorted.Select(pair => $"{UrlEncode(pair.Key)}={UrlEncode(pair.Value)}"));
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey))).ToLowerInvariant();
        return $"{query}&w_rid={signature}";
    }

    private static string GetMixinKey(string source)
    {
        var builder = new StringBuilder();
        foreach (var index in MixinKeyEncTab)
        {
            if (index < source.Length)
                builder.Append(source[index]);
        }

        return builder.ToString()[..Math.Min(32, builder.Length)];
    }

    private static string SanitizeWbiValue(string value)
        => value.Replace("!", string.Empty)
            .Replace("'", string.Empty)
            .Replace("(", string.Empty)
            .Replace(")", string.Empty)
            .Replace("*", string.Empty);

    private static string UrlEncode(string value) => WebUtility.UrlEncode(value).Replace("+", "%20", StringComparison.Ordinal);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        return client;
    }
}

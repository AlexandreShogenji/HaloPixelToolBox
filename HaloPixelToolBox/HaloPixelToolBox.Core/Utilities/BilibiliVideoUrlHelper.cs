using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Core.Utilities;

public static class BilibiliVideoUrlHelper
{
    private static readonly Regex BvidRegex = new(@"BV[A-Za-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ExtractBvid(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var trimmed = input.Trim();
        var queryBvid = TryExtractQueryValue(trimmed, "bvid");
        if (!string.IsNullOrWhiteSpace(queryBvid))
        {
            var queryMatch = BvidRegex.Match(queryBvid);
            if (queryMatch.Success)
                return queryMatch.Value;
        }

        var match = BvidRegex.Match(Uri.UnescapeDataString(trimmed));
        return match.Success ? match.Value : string.Empty;
    }

    public static string NormalizeVideoUrl(string input)
    {
        var bvid = ExtractBvid(input);
        return string.IsNullOrWhiteSpace(bvid)
            ? string.Empty
            : $"https://www.bilibili.com/video/{bvid}/";
    }

    public static bool IsBilibiliVideoLike(string input)
    {
        return !string.IsNullOrWhiteSpace(input)
               && input.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(ExtractBvid(input));
    }

    private static string TryExtractQueryValue(string input, string key)
    {
        var value = TryExtractQueryValueFromUri(input, key);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var queryIndex = input.IndexOf('?', StringComparison.Ordinal);
        var query = queryIndex >= 0 ? input[(queryIndex + 1)..] : input;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
                continue;

            var name = Uri.UnescapeDataString(pair[..separatorIndex]);
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
        }

        return string.Empty;
    }

    private static string TryExtractQueryValueFromUri(string input, string key)
    {
        try
        {
            var uriInput = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                           || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? input
                : "https://" + input.TrimStart('/');

            var uri = new Uri(uriInput);
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex <= 0)
                    continue;

                var name = Uri.UnescapeDataString(pair[..separatorIndex]);
                if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            }
        }
        catch
        {
        }

        return string.Empty;
    }
}

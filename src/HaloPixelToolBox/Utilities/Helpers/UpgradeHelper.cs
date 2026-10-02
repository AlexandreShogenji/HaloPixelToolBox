using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.System;

namespace HaloPixelToolBox.Utilities.Helpers;

public static class UpgradeHelper
{
    public const string RepositoryUrl = "https://github.com/AlexandreShogenji/HaloPixelToolBox";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/AlexandreShogenji/HaloPixelToolBox/releases/latest";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                LatestReleaseApiUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, cancellationToken: cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName) || string.IsNullOrWhiteSpace(release.HtmlUrl))
                return null;

            var latestVersion = ParseVersion(release.TagName);
            if (latestVersion is null)
                return null;

            var installerUrl = release.Assets?
                .FirstOrDefault(asset => asset.Name.EndsWith("-installer-win-x64.exe", StringComparison.OrdinalIgnoreCase))?
                .BrowserDownloadUrl;

            return new GitHubReleaseInfo(
                release.TagName.Trim(),
                string.IsNullOrWhiteSpace(release.Body) ? "此版本未提供更新说明。" : release.Body.Trim(),
                string.IsNullOrWhiteSpace(installerUrl) ? release.HtmlUrl : installerUrl,
                NormalizeVersion(Version) >= NormalizeVersion(latestVersion));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR]检查更新时发生错误：{ex.Message}");
            return null;
        }
    }

    public static async Task<bool> OpenDownloadPageAsync(string? downloadUrl)
    {
        var target = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
            ? uri
            : new Uri($"{RepositoryUrl}/releases/latest");
        return await Launcher.LaunchUriAsync(target);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HaloPixelToolBox", Version.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version? ParseVersion(string tagName)
    {
        var normalizedTag = tagName.Trim().TrimStart('v', 'V');
        var suffixIndex = normalizedTag.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            normalizedTag = normalizedTag[..suffixIndex];
        return System.Version.TryParse(normalizedTag, out var version) ? version : null;
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubReleaseAsset>? Assets { get; init; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

public sealed record GitHubReleaseInfo(
    string LatestVersion,
    string ReleaseNotes,
    string DownloadUrl,
    bool IsLatest);

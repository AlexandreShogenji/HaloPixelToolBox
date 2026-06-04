using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BrowserVideoPlaybackStateReader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly int[] DevToolsPorts = [9222, 9223, 9224, 9225];

    public Task<BrowserVideoPlaybackSnapshot?> GetCurrentBilibiliSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentBilibiliSnapshotAsync("chrome", cancellationToken);
    }

    public async Task<BrowserVideoPlaybackSnapshot?> GetCurrentBilibiliSnapshotAsync(string browserProcessName, CancellationToken cancellationToken = default)
    {
        return (await ProbeCurrentBilibiliSnapshotAsync(browserProcessName, cancellationToken)).Snapshot;
    }

    public async Task<BrowserVideoPlaybackProbeResult> ProbeCurrentBilibiliSnapshotAsync(string browserProcessName, CancellationToken cancellationToken = default)
    {
        var observedTabs = new List<BrowserVideoPlaybackTabInfo>();
        var hasDevToolsConnection = false;
        var hasBilibiliDevToolsTab = false;

        foreach (var port in DevToolsPorts)
        {
            var tabs = await TryGetTabsAsync(port, cancellationToken);
            if (tabs.Count > 0)
                hasDevToolsConnection = true;

            foreach (var tab in tabs)
            {
                var isBilibiliVideo = IsBilibiliVideoTab(tab);
                hasBilibiliDevToolsTab |= isBilibiliVideo;
                observedTabs.Add(new BrowserVideoPlaybackTabInfo
                {
                    Port = port,
                    Type = tab.Type,
                    Title = tab.Title,
                    Url = tab.Url,
                    IsBilibiliVideo = isBilibiliVideo
                });
            }

            foreach (var tab in tabs.Where(IsBilibiliVideoTab).OrderByDescending(tab => tab.Url.Contains("/video/", StringComparison.OrdinalIgnoreCase)))
            {
                var snapshot = await TryReadSnapshotAsync(tab, port, cancellationToken);
                if (snapshot?.Position is { } position)
                {
                    return new BrowserVideoPlaybackProbeResult
                    {
                        Snapshot = snapshot,
                        Tabs = observedTabs,
                        HasDevToolsConnection = true,
                        HasBilibiliDevToolsTab = true,
                        Message = $"CDP:{port} 已读取播放进度 {FormatTime(position)}"
                    };
                }

                if (snapshot is not null)
                {
                    return new BrowserVideoPlaybackProbeResult
                    {
                        Snapshot = snapshot,
                        Tabs = observedTabs,
                        HasDevToolsConnection = true,
                        HasBilibiliDevToolsTab = true,
                        Message = $"CDP:{port} 找到 B 站页，但尚未读到有效 video.currentTime"
                    };
                }
            }
        }

        var addressBarSnapshot = TryReadAddressBarSnapshot(browserProcessName);
        if (addressBarSnapshot is not null)
        {
            var message = hasDevToolsConnection
                ? "已从地址栏捕获 B 站 URL，但 CDP 标签页中未发现该视频页"
                : "已从地址栏捕获 B 站 URL，但未连接到 Chrome CDP 端口";

            return new BrowserVideoPlaybackProbeResult
            {
                Snapshot = addressBarSnapshot,
                Tabs = observedTabs,
                HasDevToolsConnection = hasDevToolsConnection,
                HasBilibiliDevToolsTab = hasBilibiliDevToolsTab,
                Message = message
            };
        }

        return new BrowserVideoPlaybackProbeResult
        {
            Tabs = observedTabs,
            HasDevToolsConnection = hasDevToolsConnection,
            HasBilibiliDevToolsTab = hasBilibiliDevToolsTab,
            Message = BuildNoSnapshotMessage(hasDevToolsConnection, observedTabs)
        };
    }

    private static async Task<IReadOnlyList<DevToolsTab>> TryGetTabsAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var json = await HttpClient.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
            return JsonSerializer.Deserialize<List<DevToolsTab>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsBilibiliVideoTab(DevToolsTab tab)
    {
        return tab.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(tab.WebSocketDebuggerUrl)
               && BilibiliVideoUrlHelper.IsBilibiliVideoLike(tab.Url);
    }

    private static BrowserVideoPlaybackSnapshot? TryReadAddressBarSnapshot(string browserProcessName)
    {
        var processName = string.IsNullOrWhiteSpace(browserProcessName) ? "chrome" : browserProcessName.Trim();
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            processName = processName[..^4];

        try
        {
            foreach (var process in Process.GetProcessesByName(processName)
                         .Where(process => process.MainWindowHandle != IntPtr.Zero)
                         .OrderByDescending(process => process.MainWindowTitle.Contains("bilibili", StringComparison.OrdinalIgnoreCase)))
            {
                var window = AutomationElement.FromHandle(process.MainWindowHandle);
                var url = TryReadBilibiliUrlFromWindow(window);
                if (url is null)
                    continue;

                return new BrowserVideoPlaybackSnapshot
                {
                    Url = url,
                    Title = TrimBrowserTitle(process.MainWindowTitle),
                    IsPaused = true,
                    PlaybackRate = 1,
                    Source = $"{processName} address bar"
                };
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryReadBilibiliUrlFromWindow(AutomationElement window)
    {
        var editControls = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        foreach (AutomationElement editControl in editControls)
        {
            try
            {
                if (editControl.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern valuePattern)
                    continue;

                var url = BilibiliVideoUrlHelper.NormalizeVideoUrl(valuePattern.Current.Value);
                if (url is not null)
                {
                    if (string.IsNullOrWhiteSpace(url))
                        continue;
                    return url;
                }
            }
            catch
            {
                // Browser controls can briefly refuse pattern access while rendering.
            }
        }

        return null;
    }

    private static async Task<BrowserVideoPlaybackSnapshot?> TryReadSnapshotAsync(DevToolsTab tab, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(tab.WebSocketDebuggerUrl), cancellationToken);

            var command = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = """
                    (() => {
                      const videos = Array.from(document.querySelectorAll('video'));
                      const states = videos.map((video, index) => {
                        const rect = video.getBoundingClientRect ? video.getBoundingClientRect() : { width: 0, height: 0 };
                        const area = Math.max(0, rect.width || 0) * Math.max(0, rect.height || 0);
                        const duration = Number.isFinite(video.duration) ? video.duration : null;
                        const currentTime = Number.isFinite(video.currentTime) ? video.currentTime : null;
                        const hasTimeline = duration !== null && currentTime !== null && duration > 0;
                        const visible = area > 0 && getComputedStyle(video).visibility !== 'hidden' && getComputedStyle(video).display !== 'none';
                        const score =
                          (hasTimeline ? 100000 : 0) +
                          (!video.paused ? 10000 : 0) +
                          (visible ? 1000 : 0) +
                          Math.min(area, 999999) / 1000 +
                          (video.readyState || 0);
                        return {
                          index,
                          score,
                          currentTime,
                          duration,
                          paused: video.paused,
                          ended: video.ended,
                          loop: video.loop,
                          playbackRate: video.playbackRate || 1,
                          readyState: video.readyState || 0,
                          area
                        };
                      }).sort((a, b) => b.score - a.score);
                      const selected = states[0] || null;
                      return JSON.stringify({
                        url: location.href,
                        title: document.title,
                        videoCount: videos.length,
                        selectedVideoIndex: selected ? selected.index : null,
                        currentTime: selected ? selected.currentTime : null,
                        duration: selected ? selected.duration : null,
                        paused: selected ? selected.paused : true,
                        ended: selected ? selected.ended : false,
                        loop: selected ? selected.loop : false,
                        playbackRate: selected ? selected.playbackRate : 1
                      });
                    })()
                    """,
                    returnByValue = true
                }
            });

            await SendJsonAsync(webSocket, command, cancellationToken);
            var response = await ReceiveJsonAsync(webSocket, cancellationToken);
            var payload = ExtractRuntimeValue(response);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            var browserState = JsonSerializer.Deserialize<BrowserRuntimeVideoState>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var normalizedUrl = BilibiliVideoUrlHelper.NormalizeVideoUrl(browserState?.Url ?? string.Empty);
            if (browserState is null || string.IsNullOrWhiteSpace(normalizedUrl))
                return null;

            return new BrowserVideoPlaybackSnapshot
            {
                Url = normalizedUrl,
                Title = browserState.Title,
                Position = browserState.CurrentTime is null ? null : TimeSpan.FromSeconds(Math.Max(0, browserState.CurrentTime.Value)),
                Duration = browserState.Duration is null ? null : TimeSpan.FromSeconds(Math.Max(0, browserState.Duration.Value)),
                IsPaused = browserState.Paused,
                IsEnded = browserState.Ended,
                IsLooping = browserState.Loop,
                PlaybackRate = browserState.PlaybackRate <= 0 ? 1 : browserState.PlaybackRate,
                Source = $"Chrome DevTools Protocol:{port}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket webSocket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveJsonAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return builder.ToString();
    }

    private static string? ExtractRuntimeValue(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("result", out var innerResult)
            || !innerResult.TryGetProperty("value", out var value))
        {
            return null;
        }

        return value.GetString();
    }

    private static string BuildNoSnapshotMessage(bool hasDevToolsConnection, IReadOnlyList<BrowserVideoPlaybackTabInfo> tabs)
    {
        if (!hasDevToolsConnection)
            return "未连接到 Chrome CDP 端口 9222-9225，也未从地址栏捕获到 B 站 URL";

        var pageTabs = tabs.Where(tab => tab.Type.Equals("page", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        if (pageTabs.Count == 0)
            return "CDP 已连接，但没有可用页面标签";

        var titles = string.Join(" / ", pageTabs.Select(tab => string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title));
        return $"CDP 已连接，但未发现 B 站视频页；当前页面：{titles}";
    }

    private static string TrimBrowserTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return title
            .Replace(" - Google Chrome", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" - Microsoft Edge", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private sealed class DevToolsTab
    {
        public string Type { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string WebSocketDebuggerUrl { get; set; } = string.Empty;
    }

    private sealed class BrowserRuntimeVideoState
    {
        public string Url { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public double? CurrentTime { get; set; }

        public double? Duration { get; set; }

        public bool Paused { get; set; }

        public bool Ended { get; set; }

        public bool Loop { get; set; }

        public double PlaybackRate { get; set; } = 1;
    }
}

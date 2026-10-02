namespace HaloPixelToolBox.Core.Models.Subtitles;

public sealed class BrowserVideoPlaybackProbeResult
{
    public BrowserVideoPlaybackSnapshot? Snapshot { get; init; }

    public IReadOnlyList<BrowserVideoPlaybackTabInfo> Tabs { get; init; } = [];

    public string Message { get; init; } = string.Empty;

    public bool HasDevToolsConnection { get; init; }

    public bool HasBilibiliDevToolsTab { get; init; }

    public bool HasReliablePosition => Snapshot?.HasReliablePosition == true;
}

public sealed class BrowserVideoPlaybackTabInfo
{
    public int Port { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public bool IsBilibiliVideo { get; init; }
}

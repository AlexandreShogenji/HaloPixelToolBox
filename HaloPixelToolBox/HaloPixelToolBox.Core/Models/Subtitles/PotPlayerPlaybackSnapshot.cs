namespace HaloPixelToolBox.Core.Models.Subtitles;

/// <summary>
/// PotPlayer 播放快照。优先来自 Windows 媒体会话；失败时退回进程窗口状态。
/// </summary>
public sealed class PotPlayerPlaybackSnapshot
{
    public PotPlayerPlaybackState State { get; init; } = PotPlayerPlaybackState.NotRunning;

    public TimeSpan? Position { get; init; }

    public TimeSpan? Duration { get; init; }

    public string Source { get; init; } = string.Empty;

    public bool HasReliablePosition => Position is not null;

    public static PotPlayerPlaybackSnapshot NotRunning { get; } = new()
    {
        State = PotPlayerPlaybackState.NotRunning
    };
}

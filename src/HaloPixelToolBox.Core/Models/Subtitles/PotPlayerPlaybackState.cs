namespace HaloPixelToolBox.Core.Models.Subtitles;

/// <summary>
/// PotPlayer 播放状态。部分皮肤不会暴露窗口标题，因此 RunningUnknown 表示已检测到进程但无法可靠判断播放/暂停。
/// </summary>
public enum PotPlayerPlaybackState
{
    NotRunning,
    Playing,
    Paused,
    RunningUnknown
}

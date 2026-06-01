namespace HaloPixelToolBox.Core.Models.Display;

/// <summary>
/// 字幕屏内容来源，用于协调时钟刷新与其他显示内容，避免后台刷新覆盖用户主动发送的字幕。
/// </summary>
public enum DisplayContentKind
{
    Custom,
    Clock,
    Lyrics,
    VideoSubtitle,
    BrowserTranslation,
    Scene,
    System
}

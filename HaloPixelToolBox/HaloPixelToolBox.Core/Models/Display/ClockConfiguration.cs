namespace HaloPixelToolBox.Core.Models.Display;

/// <summary>
/// 屏幕时钟配置。
/// </summary>
public class ClockConfiguration
{
    public ClockTemplateDefinition Template { get; set; } = new();
    public string FontFamily { get; set; } = "默认字体";
    public int X { get; set; }
    public int Y { get; set; }
    public int RefreshIntervalMilliseconds { get; set; } = 1000;
    public bool ShowDate { get; set; }
}

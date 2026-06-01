namespace HaloPixelToolBox.Core.Models.Display;

/// <summary>
/// 字幕颜色配置。当前设备文本协议未公开颜色字段，先作为上层样式参数保留。
/// </summary>
public record HaloPixelColor(byte Red, byte Green, byte Blue)
{
    public static HaloPixelColor White { get; } = new(255, 255, 255);
    public static HaloPixelColor RedColor { get; } = new(255, 64, 64);
    public static HaloPixelColor GreenColor { get; } = new(64, 255, 128);
    public static HaloPixelColor BlueColor { get; } = new(64, 160, 255);

    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

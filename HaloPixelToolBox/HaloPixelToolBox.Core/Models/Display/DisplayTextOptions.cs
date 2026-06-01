using HaloPixelToolBox.Core.Models;

namespace HaloPixelToolBox.Core.Models.Display;

/// <summary>
/// 增强自定义字幕参数。协议暂不支持的字段由上层保留，后续可在 HidPacketBuilder 中映射。
/// </summary>
public class DisplayTextOptions
{
    public string Text { get; set; } = string.Empty;
    public HaloPixelTextLayout Layout { get; set; } = HaloPixelTextLayout.Center;
    public TextScrollDirection ScrollDirection { get; set; } = TextScrollDirection.None;
    public int Speed { get; set; } = 5;
    public bool Blink { get; set; }
    public HaloPixelColor Color { get; set; } = HaloPixelColor.White;
    public bool MultiLine { get; set; }
    public DateTimeOffset? SendAt { get; set; }
}

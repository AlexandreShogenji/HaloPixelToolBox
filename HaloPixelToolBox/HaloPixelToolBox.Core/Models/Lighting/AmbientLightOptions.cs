using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Models.Lighting;

public sealed class AmbientLightOptions
{
    public bool IsEnabled { get; set; } = true;

    public AmbientLightEffect Effect { get; set; } = AmbientLightEffect.Static;

    public HaloPixelColor Color { get; set; } = HaloPixelColor.White;

    public AmbientLightBrightness Brightness { get; set; } = AmbientLightBrightness.High;

    public byte Speed { get; set; } = 10;
}

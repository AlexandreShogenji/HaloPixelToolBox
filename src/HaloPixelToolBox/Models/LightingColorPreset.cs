using System.Text.Json.Serialization;
using Windows.UI;

namespace HaloPixelToolBox.Models;

/// <summary>
/// A named pair of ambient-light and pixel-screen colors.
/// </summary>
public sealed record LightingColorPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "未命名配色";

    public int AmbientRed { get; init; }

    public int AmbientGreen { get; init; }

    public int AmbientBlue { get; init; }

    public int PixelRed { get; init; }

    public int PixelGreen { get; init; }

    public int PixelBlue { get; init; }

    [JsonIgnore]
    public string AmbientHex => ToHex(AmbientRed, AmbientGreen, AmbientBlue);

    [JsonIgnore]
    public string PixelHex => ToHex(PixelRed, PixelGreen, PixelBlue);

    [JsonIgnore]
    public Color AmbientColor => ToColor(AmbientRed, AmbientGreen, AmbientBlue);

    [JsonIgnore]
    public Color PixelColor => ToColor(PixelRed, PixelGreen, PixelBlue);

    public LightingColorPreset WithName(string name) => this with { Name = name };

    public LightingColorPreset WithCurrentColors(
        int ambientRed,
        int ambientGreen,
        int ambientBlue,
        int pixelRed,
        int pixelGreen,
        int pixelBlue)
        => this with
        {
            AmbientRed = ambientRed,
            AmbientGreen = ambientGreen,
            AmbientBlue = ambientBlue,
            PixelRed = pixelRed,
            PixelGreen = pixelGreen,
            PixelBlue = pixelBlue
        };

    private static Color ToColor(int red, int green, int blue)
        => Color.FromArgb(255, ClampByte(red), ClampByte(green), ClampByte(blue));

    private static string ToHex(int red, int green, int blue)
        => $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}

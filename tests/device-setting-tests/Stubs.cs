using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;

namespace Windows.UI
{
    public readonly record struct Color(byte A, byte R, byte G, byte B)
    {
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
    }
}

namespace HaloPixelToolBox.Profiles.CrossVersionProfiles
{
    public static class DisplayFeatureProfile
    {
        public static bool LightsTurnedOffByAutomation { get; set; }
        public static bool AmbientLightEnabled { get; set; } = true;
        public static bool PixelScreenEnabled { get; set; } = true;
        public static int AmbientLightEffectIndex { get; set; }
        public static int AmbientLightBrightnessIndex { get; set; }
        public static double AmbientLightSpeed { get; set; } = 5;
        public static int AmbientLightRed { get; set; }
        public static int AmbientLightGreen { get; set; }
        public static int AmbientLightBlue { get; set; }
        public static int PixelScreenRed { get; set; }
        public static int PixelScreenGreen { get; set; }
        public static int PixelScreenBlue { get; set; }
    }
}

namespace HaloPixelToolBox.Core.Services.Lighting
{
    public sealed class HaloPixelLightingService
    {
        public static Func<Task>? BeforeAmbientWrite { get; set; }
        public static bool WriteSucceeds { get; set; } = true;
        public static List<HaloPixelColor> AmbientWrites { get; } = [];
        public static List<HaloPixelColor> PixelWrites { get; } = [];
        public async Task<bool> SetAmbientLightAsync(AmbientLightOptions options, CancellationToken cancellationToken)
        {
            if (BeforeAmbientWrite is { } before) await before();
            cancellationToken.ThrowIfCancellationRequested();
            AmbientWrites.Add(options.Color);
            return WriteSucceeds;
        }
        public Task<(bool Success, bool Enabled, HaloPixelColor Color)> GetPixelScreenStateAsync(CancellationToken token)
            => Task.FromResult((WriteSucceeds, true, HaloPixelColor.White));
        public Task<bool> SetPixelScreenColorAsync(HaloPixelColor color, CancellationToken token)
        {
            PixelWrites.Add(color);
            return Task.FromResult(WriteSucceeds);
        }
        public Task<bool> SetPixelScreenEnabledAsync(HaloPixelColor color, bool enabled, CancellationToken token) => Task.FromResult(true);
        public Task<bool> SetAmbientLightEnabledAsync(bool enabled, CancellationToken token) => Task.FromResult(true);
        public static void SetPreviewState(AmbientLightOptions options, bool enabled, HaloPixelColor color) { }
    }
}

using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services.Lighting;

public sealed class HaloPixelLightingService
{
    private static readonly object PreviewStateLock = new();
    private static HaloPixelLightingPreviewState previewState = new(
        true,
        AmbientLightEffect.Static,
        AmbientLightBrightness.High,
        10,
        new HaloPixelColor(45, 0, 179),
        new HaloPixelColor(0, 85, 170));

    public static event EventHandler? PreviewColorsChanged;

    public static event EventHandler? PreviewStateChanged;

    public static HaloPixelLightingPreviewState PreviewState
    {
        get
        {
            lock (PreviewStateLock)
                return previewState;
        }
    }

    public static HaloPixelColor AmbientPreviewColor => PreviewState.AmbientColor;

    public static HaloPixelColor PixelScreenPreviewColor => PreviewState.PixelScreenColor;

    public HaloPixelDevice Device { get; }

    public HaloPixelLightingService() : this(new HaloPixelDevice())
    {
    }

    public HaloPixelLightingService(HaloPixelDevice device)
    {
        Device = device;
    }

    public bool EnsureDeviceReady() => Device.CurrentDevice is not null || Device.Initialize();

    public static void SetPreviewColors(HaloPixelColor ambientColor, HaloPixelColor pixelScreenColor)
    {
        ArgumentNullException.ThrowIfNull(ambientColor);
        ArgumentNullException.ThrowIfNull(pixelScreenColor);

        UpdatePreviewState(current => current with
        {
            AmbientColor = ambientColor,
            PixelScreenColor = pixelScreenColor
        });
    }

    public static void SetPreviewState(AmbientLightOptions options, HaloPixelColor pixelScreenColor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pixelScreenColor);

        var normalizedEffect = Enum.IsDefined(options.Effect)
            ? options.Effect
            : AmbientLightEffect.Static;
        var normalizedBrightness = Enum.IsDefined(options.Brightness)
            ? options.Brightness
            : AmbientLightBrightness.High;

        UpdatePreviewState(_ => new HaloPixelLightingPreviewState(
            options.IsEnabled,
            normalizedEffect,
            normalizedBrightness,
            Math.Clamp(options.Speed, (byte)1, (byte)10),
            options.Color,
            pixelScreenColor));
    }

    private static void UpdatePreviewState(
        Func<HaloPixelLightingPreviewState, HaloPixelLightingPreviewState> update)
    {
        var changed = false;
        lock (PreviewStateLock)
        {
            var next = update(previewState);
            if (next == previewState)
                return;

            previewState = next;
            changed = true;
        }

        if (!changed)
            return;

        PreviewColorsChanged?.Invoke(null, EventArgs.Empty);
        PreviewStateChanged?.Invoke(null, EventArgs.Empty);
    }

    public async Task<bool> SetPixelScreenColorAsync(HaloPixelColor color, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EnsureDeviceReady())
                return false;

            Device.SetPixelScreenColor(color);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetPixelScreenEnabledAsync(
        HaloPixelColor color,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureDeviceReady() && Device.SetPixelScreenEnabled(color, enabled);
        }, cancellationToken);
    }

    public async Task<bool> SetAmbientLightAsync(AmbientLightOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EnsureDeviceReady())
                return false;

            if (!options.IsEnabled)
                return true;

            Device.SetAmbientLight(options);
            return true;
        }, cancellationToken);
    }

    public async Task<bool> SetAmbientLightEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureDeviceReady() && Device.SetAmbientLightEnabled(enabled);
        }, cancellationToken);
    }
}

public readonly record struct HaloPixelLightingPreviewState(
    bool IsEnabled,
    AmbientLightEffect Effect,
    AmbientLightBrightness Brightness,
    byte Speed,
    HaloPixelColor AmbientColor,
    HaloPixelColor PixelScreenColor);

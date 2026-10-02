using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.Services;

/// <summary>
/// Serializes lighting changes and keeps the device, persisted profile and UI preview in sync.
/// </summary>
public sealed class LightingControlCoordinator
{
    internal static SemaphoreSlim SharedMutationGate { get; } = new(1, 1);

    private readonly HaloPixelLightingService lightingService = new();
    private readonly Action? desiredLightingStateChanged;

    public LightingControlCoordinator(Action? desiredLightingStateChanged = null)
    {
        this.desiredLightingStateChanged = desiredLightingStateChanged;
    }

    /// <summary>
    /// Raised before an external caller or the automation service waits to mutate lighting. UI
    /// send queues use this signal to invalidate snapshots that must not overwrite that mutation.
    /// </summary>
    internal static event EventHandler<LightingMutationStartingEventArgs>? ExternalMutationStarting;

    internal static void NotifyExternalMutationStarting(LightingMutationScope scope = LightingMutationScope.All)
    {
        var handlers = ExternalMutationStarting;
        if (handlers is null)
            return;

        foreach (EventHandler<LightingMutationStartingEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(null, new LightingMutationStartingEventArgs(scope));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WARN]灯光外部变更通知失败：{exception.Message}");
            }
        }
    }

    public async Task<DeviceCommandResult<AmbientLightEffectChange>> SetAmbientEffectAsync(
        string mode,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        AmbientLightEffectSelection selection;
        try
        {
            selection = AmbientLightEffectResolver.Parse(mode, reference);
        }
        catch (ArgumentException exception)
        {
            return DeviceCommandResult<AmbientLightEffectChange>.Rejected(
                DeviceCommandStatus.InvalidArgument, exception.Message);
        }

        var entered = false;
        var desiredStateChanged = false;
        try
        {
            await SharedMutationGate.WaitAsync(cancellationToken);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            NotifyExternalMutationStarting(LightingMutationScope.Ambient);
            var options = BuildAmbientOptions(ReadAmbientSpeed());
            var previousEffect = options.Effect;
            options.Effect = AmbientLightEffectResolver.Select(selection, previousEffect);
            var automationIsForcingOff = DisplayFeatureProfile.LightsTurnedOffByAutomation;
            var effectiveNow = options.IsEnabled && !automationIsForcingOff;
            if (effectiveNow && !await lightingService.SetAmbientLightAsync(options, cancellationToken))
            {
                return DeviceCommandResult<AmbientLightEffectChange>.Rejected(
                    DeviceCommandStatus.NotConfirmed, "灯效写入设备失败，本地设置未更改");
            }

            DisplayFeatureProfile.AmbientLightEffectIndex = (int)options.Effect - 1;
            desiredStateChanged = options.Effect != previousEffect || effectiveNow;
            PublishPreviewFromProfile();
            var selected = AmbientLightEffectResolver.GetItem(options.Effect);
            var visibilityNote = !options.IsEnabled
                ? "；氛围灯当前关闭，将在下次开启时生效"
                : automationIsForcingOff
                    ? "；当前正在自动关灯，已保存灯效，将在自动关灯结束后恢复"
                    : string.Empty;
            return DeviceCommandResult<AmbientLightEffectChange>.Succeeded(
                $"氛围灯效已切换为“{selected.Name}”{visibilityNote}",
                new AmbientLightEffectChange(
                    previousEffect, options.Effect, selected.Position, selected.Key, selected.Name,
                    previousEffect != options.Effect, effectiveNow,
                    effectiveNow ? "writeSent" : "localProfile"));
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<AmbientLightEffectChange>.Rejected(
                DeviceCommandStatus.Cancelled, "氛围灯效切换已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult<AmbientLightEffectChange>.Rejected(
                DeviceCommandStatus.Failed, $"氛围灯效切换失败：{exception.Message}");
        }
        finally
        {
            if (entered)
                SharedMutationGate.Release();
            if (desiredStateChanged)
                NotifyDesiredLightingStateChanged();
        }
    }

    public async Task<DeviceCommandResult<AmbientLightSpeedChange>> SetAmbientSpeedAsync(
        int? targetSpeed,
        int? delta,
        CancellationToken cancellationToken = default)
    {
        if ((targetSpeed is null) == (delta is null))
        {
            return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "targetSpeed 与 delta 必须且只能提供一个");
        }

        if (targetSpeed is < 1 or > 10)
        {
            return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "灯效速度必须是 1 到 10 之间的整数；1 最慢，10 最快");
        }

        if (delta == 0 || delta is < -10 or > 10)
        {
            return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "相对灯速调节必须是 -10 到 10 之间的非零整数");
        }

        var desiredStateChanged = false;
        await SharedMutationGate.WaitAsync(cancellationToken);
        try
        {
            NotifyExternalMutationStarting(LightingMutationScope.Ambient);
            var previousSpeed = ReadAmbientSpeed();
            var requestedSpeed = targetSpeed ?? previousSpeed + delta!.Value;
            var nextSpeed = Math.Clamp(requestedSpeed, 1, 10);
            var bounded = requestedSpeed != nextSpeed;
            var options = BuildAmbientOptions(nextSpeed);
            var effectiveNow = options.IsEnabled && options.Effect != AmbientLightEffect.Static;

            if (options.IsEnabled && !await lightingService.SetAmbientLightAsync(options, cancellationToken))
            {
                return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    "灯效速度写入设备失败，本地设置未更改");
            }

            DisplayFeatureProfile.AmbientLightSpeed = nextSpeed;
            desiredStateChanged = nextSpeed != previousSpeed || options.IsEnabled;
            PublishPreviewFromProfile();
            var visibilityNote = options.IsEnabled
                ? options.Effect == AmbientLightEffect.Static ? "；当前是纯色静光，速度不会产生肉眼可见变化" : string.Empty
                : "；氛围灯当前关闭，将在下次开启时生效";
            return DeviceCommandResult<AmbientLightSpeedChange>.Succeeded(
                $"灯效速度已从 {previousSpeed}/10 调整为 {nextSpeed}/10{visibilityNote}",
                new AmbientLightSpeedChange(
                    previousSpeed,
                    nextSpeed,
                    nextSpeed != previousSpeed,
                    bounded,
                    effectiveNow,
                    options.Effect,
                    options.IsEnabled ? "writeSent" : "localProfile"));
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.Cancelled,
                "灯效速度调节已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult<AmbientLightSpeedChange>.Rejected(
                DeviceCommandStatus.Failed,
                $"灯效速度调节失败：{exception.Message}");
        }
        finally
        {
            SharedMutationGate.Release();
            if (desiredStateChanged)
                NotifyDesiredLightingStateChanged();
        }
    }

    public async Task<DeviceCommandResult> SetAmbientLightEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var desiredStateChanged = false;
        await SharedMutationGate.WaitAsync(cancellationToken);
        try
        {
            NotifyExternalMutationStarting(LightingMutationScope.Ambient);
            if (!await lightingService.SetAmbientLightEnabledAsync(enabled, cancellationToken))
            {
                return DeviceCommandResult.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    "氛围灯开关未得到设备确认");
            }

            DisplayFeatureProfile.AmbientLightEnabled = enabled;
            desiredStateChanged = true;
            PublishPreviewFromProfile();
            if (enabled)
            {
                var options = BuildAmbientOptions(ReadAmbientSpeed());
                if (!await lightingService.SetAmbientLightAsync(options, cancellationToken))
                {
                    return DeviceCommandResult.Rejected(
                        DeviceCommandStatus.NotConfirmed,
                        "氛围灯已开启，但保存的灯效参数未能完整恢复");
                }
            }
            return DeviceCommandResult.Succeeded(enabled ? "氛围灯已开启" : "氛围灯已关闭");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "氛围灯开关操作已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Failed, $"氛围灯开关操作失败：{exception.Message}");
        }
        finally
        {
            SharedMutationGate.Release();
            if (desiredStateChanged)
                NotifyDesiredLightingStateChanged();
        }
    }

    public async Task<DeviceCommandResult> SetPixelScreenEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var desiredStateChanged = false;
        await SharedMutationGate.WaitAsync(cancellationToken);
        try
        {
            NotifyExternalMutationStarting(LightingMutationScope.Pixel);
            var color = BuildPixelColor();
            if (!await lightingService.SetPixelScreenEnabledAsync(color, enabled, cancellationToken))
            {
                return DeviceCommandResult.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    "像素屏开关未得到设备确认");
            }

            DisplayFeatureProfile.PixelScreenEnabled = enabled;
            desiredStateChanged = true;
            PublishPreviewFromProfile();
            return DeviceCommandResult.Succeeded(enabled ? "像素屏已开启" : "像素屏已关闭");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Cancelled, "像素屏开关操作已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(DeviceCommandStatus.Failed, $"像素屏开关操作失败：{exception.Message}");
        }
        finally
        {
            SharedMutationGate.Release();
            if (desiredStateChanged)
                NotifyDesiredLightingStateChanged();
        }
    }

    public async Task<DeviceCommandResult> ApplyPresetAsync(
        LightingColorPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var desiredStateChanged = false;
        await SharedMutationGate.WaitAsync(cancellationToken);
        try
        {
            NotifyExternalMutationStarting(LightingMutationScope.All);
            var ambientColor = new HaloPixelColor(
                (byte)preset.AmbientRed,
                (byte)preset.AmbientGreen,
                (byte)preset.AmbientBlue);
            var pixelColor = new HaloPixelColor(
                (byte)preset.PixelRed,
                (byte)preset.PixelGreen,
                (byte)preset.PixelBlue);
            var options = BuildAmbientOptions(ReadAmbientSpeed(), ambientColor);

            var ambientApplied = await lightingService.SetAmbientLightAsync(options, cancellationToken);
            if (ambientApplied)
            {
                PersistAmbientColor(ambientColor);
                desiredStateChanged = true;
                PublishPreviewFromProfile();
            }

            var pixelState = await lightingService.GetPixelScreenStateAsync(cancellationToken);
            var pixelApplied = pixelState.Success
                && (!pixelState.Enabled || await lightingService.SetPixelScreenColorAsync(pixelColor, cancellationToken));
            if (pixelApplied)
            {
                PersistPixelColor(pixelColor);
                desiredStateChanged = true;
                PublishPreviewFromProfile();
            }

            if (ambientApplied && pixelApplied)
            {
                var pixelNote = pixelState.Enabled ? string.Empty : "；像素屏当前关闭，颜色已保存供下次开启";
                return DeviceCommandResult.Succeeded(
                    $"已应用灯光配置“{preset.Name}”（氛围灯 {preset.AmbientHex}，像素屏 {preset.PixelHex}）{pixelNote}");
            }

            if (ambientApplied)
            {
                return DeviceCommandResult.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    $"灯光配置“{preset.Name}”仅完成氛围灯部分；像素屏操作未得到设备确认");
            }

            if (pixelApplied)
            {
                return DeviceCommandResult.Rejected(
                    DeviceCommandStatus.NotConfirmed,
                    $"灯光配置“{preset.Name}”仅完成像素屏部分；氛围灯操作未得到设备确认");
            }

            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.NotConfirmed,
                $"灯光配置“{preset.Name}”未通过设备操作确认");
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.Cancelled,
                desiredStateChanged ? "灯光配置应用已取消；取消前已完成的部分已保存" : "灯光配置应用已取消");
        }
        catch (Exception exception)
        {
            return DeviceCommandResult.Rejected(
                DeviceCommandStatus.Failed,
                desiredStateChanged
                    ? $"灯光配置应用部分完成，后续操作失败：{exception.Message}"
                    : $"灯光配置应用失败：{exception.Message}");
        }
        finally
        {
            SharedMutationGate.Release();
            if (desiredStateChanged)
                NotifyDesiredLightingStateChanged();
        }
    }

    private static AmbientLightOptions BuildAmbientOptions(int speed, HaloPixelColor? color = null)
    {
        var effect = (AmbientLightEffect)(Math.Clamp(DisplayFeatureProfile.AmbientLightEffectIndex, 0, 5) + 1);
        var brightness = (AmbientLightBrightness)(Math.Clamp(DisplayFeatureProfile.AmbientLightBrightnessIndex, 0, 2) + 1);
        return new AmbientLightOptions
        {
            IsEnabled = DisplayFeatureProfile.AmbientLightEnabled,
            Effect = effect,
            Brightness = brightness,
            Speed = (byte)Math.Clamp(speed, 1, 10),
            Color = color ?? new HaloPixelColor(
                (byte)Math.Clamp(DisplayFeatureProfile.AmbientLightRed, 0, 255),
                (byte)Math.Clamp(DisplayFeatureProfile.AmbientLightGreen, 0, 255),
                (byte)Math.Clamp(DisplayFeatureProfile.AmbientLightBlue, 0, 255))
        };
    }

    private static int ReadAmbientSpeed()
    {
        var speed = DisplayFeatureProfile.AmbientLightSpeed;
        return double.IsFinite(speed) ? Math.Clamp((int)Math.Round(speed), 1, 10) : 10;
    }

    private static HaloPixelColor BuildPixelColor()
        => new(
            (byte)Math.Clamp(DisplayFeatureProfile.PixelScreenRed, 0, 255),
            (byte)Math.Clamp(DisplayFeatureProfile.PixelScreenGreen, 0, 255),
            (byte)Math.Clamp(DisplayFeatureProfile.PixelScreenBlue, 0, 255));

    private static void PersistAmbientColor(HaloPixelColor color)
    {
        DisplayFeatureProfile.AmbientLightRed = color.Red;
        DisplayFeatureProfile.AmbientLightGreen = color.Green;
        DisplayFeatureProfile.AmbientLightBlue = color.Blue;
    }

    private static void PersistPixelColor(HaloPixelColor color)
    {
        DisplayFeatureProfile.PixelScreenRed = color.Red;
        DisplayFeatureProfile.PixelScreenGreen = color.Green;
        DisplayFeatureProfile.PixelScreenBlue = color.Blue;
    }

    private static void PublishPreviewFromProfile()
    {
        HaloPixelLightingService.SetPreviewState(
            BuildAmbientOptions(ReadAmbientSpeed()),
            DisplayFeatureProfile.PixelScreenEnabled,
            BuildPixelColor());
    }

    private void NotifyDesiredLightingStateChanged()
    {
        if (desiredLightingStateChanged is null)
            return;

        try
        {
            desiredLightingStateChanged();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WARN]灯光自动化状态刷新失败：{exception.Message}");
        }
    }
}

public sealed record AmbientLightSpeedChange(
    int PreviousSpeed,
    int Speed,
    bool Changed,
    bool Bounded,
    bool EffectiveNow,
    AmbientLightEffect Effect,
    string Verification);

public sealed record AmbientLightEffectChange(
    AmbientLightEffect PreviousEffect,
    AmbientLightEffect Effect,
    int Position,
    string Key,
    string Name,
    bool Changed,
    bool EffectiveNow,
    string Verification);

[Flags]
internal enum LightingMutationScope
{
    Ambient = 1,
    Pixel = 2,
    All = Ambient | Pixel
}

internal sealed class LightingMutationStartingEventArgs(LightingMutationScope scope) : EventArgs
{
    public LightingMutationScope Scope { get; } = scope;
}

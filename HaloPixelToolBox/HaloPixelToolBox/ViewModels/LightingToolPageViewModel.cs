using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;
using Windows.UI;

namespace HaloPixelToolBox.ViewModels;

public partial class LightingToolPageViewModel : ViewModelBase
{
    private readonly HaloPixelLightingService lightingService = new();
    private readonly LightingColorPresetStore colorPresetStore = new();
    private readonly SemaphoreSlim lightingSendGate = new(1, 1);
    private CancellationTokenSource? ambientSendThrottle;
    private CancellationTokenSource? pixelSendThrottle;
    private int ambientPowerUpdateVersion;
    private int pixelPowerUpdateVersion;
    private bool isRestoringPowerState;
    private bool isBatchUpdatingColors;

    public List<string> EffectNames { get; } =
    [
        "氛围呼吸",
        "幻彩潮汐",
        "纯色静光",
        "炫彩涟漪",
        "流光逐影",
        "动态光影"
    ];

    public List<string> BrightnessNames { get; } = ["低", "中", "高"];

    public ObservableCollection<LightingColorPreset> ColorPresets { get; } = [];

    [ObservableProperty]
    private bool ambientEnabled = DisplayFeatureProfile.AmbientLightEnabled;

    [ObservableProperty]
    private int ambientEffectIndex = Math.Clamp(DisplayFeatureProfile.AmbientLightEffectIndex, 0, 5);

    [ObservableProperty]
    private int ambientBrightnessIndex = Math.Clamp(DisplayFeatureProfile.AmbientLightBrightnessIndex, 0, 2);

    [ObservableProperty]
    private double ambientSpeed = ReadAmbientSpeed();

    [ObservableProperty]
    private int ambientRed = Math.Clamp(DisplayFeatureProfile.AmbientLightRed, 0, 255);

    [ObservableProperty]
    private int ambientGreen = Math.Clamp(DisplayFeatureProfile.AmbientLightGreen, 0, 255);

    [ObservableProperty]
    private int ambientBlue = Math.Clamp(DisplayFeatureProfile.AmbientLightBlue, 0, 255);

    [ObservableProperty]
    private int pixelRed = Math.Clamp(DisplayFeatureProfile.PixelScreenRed, 0, 255);

    [ObservableProperty]
    private int pixelGreen = Math.Clamp(DisplayFeatureProfile.PixelScreenGreen, 0, 255);

    [ObservableProperty]
    private int pixelBlue = Math.Clamp(DisplayFeatureProfile.PixelScreenBlue, 0, 255);

    [ObservableProperty]
    private bool pixelScreenEnabled = DisplayFeatureProfile.PixelScreenEnabled;

    [ObservableProperty]
    private bool syncAmbientWithPixel = DisplayFeatureProfile.SyncAmbientWithPixel;

    [ObservableProperty]
    private LightingColorPreset? selectedColorPreset;

    [ObservableProperty]
    private string selectedColorPresetName = string.Empty;

    [ObservableProperty]
    private string statusMessage = "调节灯光参数会自动发送到设备";

    public string AmbientHex => ToHex(AmbientRed, AmbientGreen, AmbientBlue);

    public string PixelHex => ToHex(PixelRed, PixelGreen, PixelBlue);

    public Color AmbientPickerColor => Color.FromArgb(255, ClampByte(AmbientRed), ClampByte(AmbientGreen), ClampByte(AmbientBlue));

    public Color PixelPickerColor => Color.FromArgb(255, ClampByte(PixelRed), ClampByte(PixelGreen), ClampByte(PixelBlue));

    public string ColorPresetSummary => HasColorPresets ? $"已保存 {ColorPresets.Count} 个配色方案" : "尚未保存配色方案";

    public bool HasColorPresets => ColorPresets.Count > 0;

    public bool HasSelectedColorPreset => SelectedColorPreset is not null;

    public LightingToolPageViewModel()
    {
        LoadColorPresets();
        PublishPreviewState();
    }

    public void SetAmbientColor(Color color)
    {
        SetAmbientColorChannels(new HaloPixelColor(color.R, color.G, color.B), true);
    }

    public void SetPixelColor(Color color)
    {
        SetPixelColorChannels(new HaloPixelColor(color.R, color.G, color.B), true);
    }

    public bool SaveCurrentColorPreset(string name, out string error)
    {
        if (ColorPresets.Count >= LightingColorPresetStore.MaximumPresetCount)
        {
            error = $"最多可保存 {LightingColorPresetStore.MaximumPresetCount} 个配色方案";
            return false;
        }

        if (!TryNormalizePresetName(name, null, out var normalizedName, out error))
            return false;

        var preset = new LightingColorPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            AmbientRed = AmbientRed,
            AmbientGreen = AmbientGreen,
            AmbientBlue = AmbientBlue,
            PixelRed = PixelRed,
            PixelGreen = PixelGreen,
            PixelBlue = PixelBlue
        };

        var updatedPresets = ColorPresets.Append(preset).ToList();
        if (!TryPersistColorPresets(updatedPresets, out error))
            return false;

        ColorPresets.Add(preset);
        SelectColorPreset(preset);
        NotifyColorPresetStateChanged();
        StatusMessage = $"已保存配色方案“{preset.Name}”";
        return true;
    }

    public void SelectColorPreset(LightingColorPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        SelectedColorPreset = ColorPresets.FirstOrDefault(item => string.Equals(item.Id, preset.Id, StringComparison.Ordinal))
            ?? preset;
    }

    public async Task ApplyColorPresetAsync(LightingColorPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        SelectColorPreset(preset);
        CancelPendingColorSends();

        var ambientColor = BuildColor(preset.AmbientRed, preset.AmbientGreen, preset.AmbientBlue);
        var pixelColor = BuildColor(preset.PixelRed, preset.PixelGreen, preset.PixelBlue);
        SetAllColorChannels(ambientColor, pixelColor);

        var ambientOptions = BuildAmbientOptions();
        try
        {
            await lightingSendGate.WaitAsync();
            try
            {
                var ambientResult = await lightingService.SetAmbientLightAsync(ambientOptions);
                var pixelResult = !PixelScreenEnabled || await lightingService.SetPixelScreenColorAsync(pixelColor);
                if (ambientResult && pixelResult)
                {
                    StatusMessage = !PixelScreenEnabled
                        ? $"配色方案“{preset.Name}”已载入；像素屏开启时应用新颜色"
                        : SyncAmbientWithPixel
                        ? $"配色方案“{preset.Name}”已应用，本次保留两种独立颜色"
                        : $"配色方案“{preset.Name}”已应用";
                }
                else
                {
                    StatusMessage = "未检测到花再 Halo PixelBar；配色已载入当前界面";
                }
            }
            finally
            {
                lightingSendGate.Release();
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"配色方案发送失败：{exception.Message}";
        }
    }

    public bool RenameSelectedColorPreset(out string error)
    {
        if (SelectedColorPreset is null)
        {
            error = "请先选择一个配色方案";
            return false;
        }

        if (!TryNormalizePresetName(SelectedColorPresetName, SelectedColorPreset.Id, out var normalizedName, out error))
            return false;

        var replacement = SelectedColorPreset.WithName(normalizedName);
        if (!TryReplaceSelectedPreset(replacement, out error))
            return false;

        StatusMessage = $"配色方案已重命名为“{replacement.Name}”";
        return true;
    }

    public bool OverwriteSelectedColorPreset(out string error)
    {
        if (SelectedColorPreset is null)
        {
            error = "请先选择一个配色方案";
            return false;
        }

        var replacement = SelectedColorPreset.WithCurrentColors(
            AmbientRed,
            AmbientGreen,
            AmbientBlue,
            PixelRed,
            PixelGreen,
            PixelBlue);
        if (!TryReplaceSelectedPreset(replacement, out error))
            return false;

        StatusMessage = $"已用当前颜色更新配色方案“{replacement.Name}”";
        return true;
    }

    public bool DeleteSelectedColorPreset()
    {
        if (SelectedColorPreset is null)
            return false;

        var index = FindPresetIndex(SelectedColorPreset.Id);
        if (index < 0)
            return false;

        var deletedName = SelectedColorPreset.Name;
        var updatedPresets = ColorPresets.Where((_, itemIndex) => itemIndex != index).ToList();
        if (!TryPersistColorPresets(updatedPresets, out var error))
        {
            StatusMessage = error;
            return false;
        }

        ColorPresets.RemoveAt(index);
        SelectedColorPreset = ColorPresets.Count == 0 ? null : ColorPresets[Math.Min(index, ColorPresets.Count - 1)];
        NotifyColorPresetStateChanged();
        StatusMessage = $"已删除配色方案“{deletedName}”";
        return true;
    }

    partial void OnAmbientEnabledChanged(bool value)
    {
        if (isRestoringPowerState)
            return;

        PublishPreviewState();
        CancelPendingSend(ref ambientSendThrottle);
        var version = Interlocked.Increment(ref ambientPowerUpdateVersion);
        _ = SendAmbientPowerNowAsync(value, version);
    }

    partial void OnPixelScreenEnabledChanged(bool value)
    {
        if (isRestoringPowerState)
            return;

        CancelPendingSend(ref pixelSendThrottle);
        var version = Interlocked.Increment(ref pixelPowerUpdateVersion);
        _ = SendPixelPowerNowAsync(value, version);
    }

    partial void OnAmbientEffectIndexChanged(int value)
    {
        DisplayFeatureProfile.AmbientLightEffectIndex = Math.Clamp(value, 0, EffectNames.Count - 1);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync();
    }

    partial void OnAmbientBrightnessIndexChanged(int value)
    {
        DisplayFeatureProfile.AmbientLightBrightnessIndex = Math.Clamp(value, 0, BrightnessNames.Count - 1);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync();
    }

    partial void OnAmbientSpeedChanged(double value)
    {
        DisplayFeatureProfile.AmbientLightSpeed = double.IsFinite(value) ? Math.Clamp(value, 1, 10) : 10;
        PublishPreviewState();
        QueueAmbientLightSend();
    }

    partial void OnSyncAmbientWithPixelChanged(bool value)
    {
        DisplayFeatureProfile.SyncAmbientWithPixel = value;
        if (!value)
            return;

        SetAmbientColorChannels(BuildPixelColor(), false);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync();
    }

    partial void OnAmbientRedChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnAmbientGreenChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnAmbientBlueChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnPixelRedChanged(int value) => OnPixelColorComponentChanged();

    partial void OnPixelGreenChanged(int value) => OnPixelColorComponentChanged();

    partial void OnPixelBlueChanged(int value) => OnPixelColorComponentChanged();

    partial void OnSelectedColorPresetChanged(LightingColorPreset? value)
    {
        SelectedColorPresetName = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedColorPreset));
    }

    private void OnAmbientColorComponentChanged()
    {
        if (isBatchUpdatingColors)
            return;

        RefreshAmbientColorState();
        PublishPreviewState();
        QueueAmbientLightSend();
    }

    private void OnPixelColorComponentChanged()
    {
        if (isBatchUpdatingColors)
            return;

        RefreshPixelColorState();
        PublishPreviewState();
        QueuePixelColorSend();
    }

    private void RefreshAmbientColorState()
    {
        OnPropertyChanged(nameof(AmbientHex));
        OnPropertyChanged(nameof(AmbientPickerColor));
        DisplayFeatureProfile.AmbientLightRed = Math.Clamp(AmbientRed, 0, 255);
        DisplayFeatureProfile.AmbientLightGreen = Math.Clamp(AmbientGreen, 0, 255);
        DisplayFeatureProfile.AmbientLightBlue = Math.Clamp(AmbientBlue, 0, 255);
    }

    private void RefreshPixelColorState()
    {
        OnPropertyChanged(nameof(PixelHex));
        OnPropertyChanged(nameof(PixelPickerColor));
        DisplayFeatureProfile.PixelScreenRed = Math.Clamp(PixelRed, 0, 255);
        DisplayFeatureProfile.PixelScreenGreen = Math.Clamp(PixelGreen, 0, 255);
        DisplayFeatureProfile.PixelScreenBlue = Math.Clamp(PixelBlue, 0, 255);
    }

    private void SetAmbientColorChannels(HaloPixelColor color, bool queueSend)
    {
        if (AmbientRed == color.Red && AmbientGreen == color.Green && AmbientBlue == color.Blue)
            return;

        isBatchUpdatingColors = true;
        try
        {
            AmbientRed = color.Red;
            AmbientGreen = color.Green;
            AmbientBlue = color.Blue;
        }
        finally
        {
            isBatchUpdatingColors = false;
        }

        RefreshAmbientColorState();
        PublishPreviewState();
        if (queueSend)
            QueueAmbientLightSend();
    }

    private void SetPixelColorChannels(HaloPixelColor color, bool queueSend)
    {
        if (PixelRed == color.Red && PixelGreen == color.Green && PixelBlue == color.Blue)
            return;

        isBatchUpdatingColors = true;
        try
        {
            PixelRed = color.Red;
            PixelGreen = color.Green;
            PixelBlue = color.Blue;
        }
        finally
        {
            isBatchUpdatingColors = false;
        }

        RefreshPixelColorState();
        PublishPreviewState();
        if (queueSend)
            QueuePixelColorSend();
    }

    private void SetAllColorChannels(HaloPixelColor ambientColor, HaloPixelColor pixelColor)
    {
        isBatchUpdatingColors = true;
        try
        {
            AmbientRed = ambientColor.Red;
            AmbientGreen = ambientColor.Green;
            AmbientBlue = ambientColor.Blue;
            PixelRed = pixelColor.Red;
            PixelGreen = pixelColor.Green;
            PixelBlue = pixelColor.Blue;
        }
        finally
        {
            isBatchUpdatingColors = false;
        }

        RefreshAmbientColorState();
        RefreshPixelColorState();
        PublishPreviewState();
    }

    private void PublishPreviewState()
        => HaloPixelLightingService.SetPreviewState(BuildAmbientOptions(), BuildPixelColor());

    private void QueueAmbientLightSend()
    {
        CancelPendingSend(ref ambientSendThrottle);
        var current = new CancellationTokenSource();
        ambientSendThrottle = current;
        _ = SendAmbientLightAfterDelayAsync(current);
    }

    private void QueuePixelColorSend()
    {
        CancelPendingSend(ref pixelSendThrottle);
        var current = new CancellationTokenSource();
        pixelSendThrottle = current;
        _ = SendPixelColorAfterDelayAsync(current);
    }

    private async Task SendAmbientLightAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(160, source.Token);
            await SendAmbientLightNowAsync(source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(ambientSendThrottle, source))
                ambientSendThrottle = null;
            source.Dispose();
        }
    }

    private async Task SendPixelColorAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(160, source.Token);
            await SendPixelColorNowAsync(source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(pixelSendThrottle, source))
                pixelSendThrottle = null;
            source.Dispose();
        }
    }

    private async Task SendAmbientLightNowAsync(CancellationToken cancellationToken = default)
    {
        var options = BuildAmbientOptions();
        try
        {
            await lightingSendGate.WaitAsync(cancellationToken);
            try
            {
                var result = await lightingService.SetAmbientLightAsync(options, cancellationToken);
                StatusMessage = result
                    ? (options.IsEnabled ? "氛围灯设置已生效" : "氛围灯已关闭")
                    : "未检测到花再 Halo PixelBar";
            }
            finally
            {
                lightingSendGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"氛围灯设置发送失败：{exception.Message}";
        }
    }

    private async Task SendAmbientPowerNowAsync(
        bool enabled,
        int version,
        CancellationToken cancellationToken = default)
    {
        var options = BuildAmbientOptions();
        try
        {
            await lightingSendGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref ambientPowerUpdateVersion))
                    return;

                var result = await lightingService.SetAmbientLightEnabledAsync(enabled, cancellationToken);
                if (result && enabled)
                    result = await lightingService.SetAmbientLightAsync(options, cancellationToken);

                if (version != Volatile.Read(ref ambientPowerUpdateVersion) || AmbientEnabled != enabled)
                    return;

                if (result)
                {
                    DisplayFeatureProfile.AmbientLightEnabled = enabled;
                    StatusMessage = enabled ? "氛围灯已开启并恢复当前设置" : "氛围灯已关闭";
                    return;
                }

                RestoreAmbientPowerState();
                StatusMessage = "氛围灯开关未得到设备状态确认";
            }
            finally
            {
                lightingSendGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref ambientPowerUpdateVersion) && AmbientEnabled == enabled)
            {
                RestoreAmbientPowerState();
                StatusMessage = $"氛围灯开关发送失败：{exception.Message}";
            }
        }
    }

    private async Task SendPixelColorNowAsync(CancellationToken cancellationToken = default)
    {
        var pixelColor = BuildPixelColor();
        var syncAmbient = SyncAmbientWithPixel;
        if (syncAmbient)
            SetAmbientColorChannels(pixelColor, false);

        var ambientOptions = syncAmbient ? BuildAmbientOptions() : null;
        try
        {
            await lightingSendGate.WaitAsync(cancellationToken);
            try
            {
                var pixelResult = !PixelScreenEnabled
                    || await lightingService.SetPixelScreenColorAsync(pixelColor, cancellationToken);
                if (!pixelResult)
                {
                    StatusMessage = "未检测到花再 Halo PixelBar";
                    return;
                }

                if (ambientOptions is not null)
                {
                    var ambientResult = await lightingService.SetAmbientLightAsync(ambientOptions, cancellationToken);
                    StatusMessage = !PixelScreenEnabled
                        ? (ambientResult
                            ? "像素屏已关闭；颜色已保存并同步氛围灯"
                            : "像素屏已关闭；颜色已保存，但未能同步氛围灯")
                        : ambientResult
                        ? "像素屏颜色已生效，并同步氛围灯颜色"
                        : "像素屏颜色已生效，但未能同步氛围灯颜色";
                    return;
                }

                StatusMessage = PixelScreenEnabled ? "像素屏颜色已生效" : "像素屏已关闭；颜色将在开启时应用";
            }
            finally
            {
                lightingSendGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusMessage = $"像素屏颜色发送失败：{exception.Message}";
        }
    }

    private async Task SendPixelPowerNowAsync(
        bool enabled,
        int version,
        CancellationToken cancellationToken = default)
    {
        var pixelColor = BuildPixelColor();
        try
        {
            await lightingSendGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref pixelPowerUpdateVersion))
                    return;

                var result = await lightingService.SetPixelScreenEnabledAsync(pixelColor, enabled, cancellationToken);
                if (version != Volatile.Read(ref pixelPowerUpdateVersion) || PixelScreenEnabled != enabled)
                    return;

                if (result)
                {
                    DisplayFeatureProfile.PixelScreenEnabled = enabled;
                    StatusMessage = enabled ? "像素屏已开启并恢复原场景" : "像素屏已关闭";
                    return;
                }

                RestorePixelScreenPowerState();
                StatusMessage = "像素屏开关未得到设备状态确认";
            }
            finally
            {
                lightingSendGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref pixelPowerUpdateVersion) && PixelScreenEnabled == enabled)
            {
                RestorePixelScreenPowerState();
                StatusMessage = $"像素屏开关发送失败：{exception.Message}";
            }
        }
    }

    private void RestoreAmbientPowerState()
    {
        isRestoringPowerState = true;
        try
        {
            AmbientEnabled = DisplayFeatureProfile.AmbientLightEnabled;
        }
        finally
        {
            isRestoringPowerState = false;
        }
        PublishPreviewState();
    }

    private void RestorePixelScreenPowerState()
    {
        isRestoringPowerState = true;
        try
        {
            PixelScreenEnabled = DisplayFeatureProfile.PixelScreenEnabled;
        }
        finally
        {
            isRestoringPowerState = false;
        }
    }

    private void CancelPendingColorSends()
    {
        CancelPendingSend(ref ambientSendThrottle);
        CancelPendingSend(ref pixelSendThrottle);
    }

    private static void CancelPendingSend(ref CancellationTokenSource? source)
    {
        var pendingSend = source;
        source = null;
        if (pendingSend is null)
            return;

        try
        {
            pendingSend.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LoadColorPresets()
    {
        try
        {
            foreach (var preset in colorPresetStore.Load())
                ColorPresets.Add(preset);

            if (ColorPresets.Count > 0)
                SelectedColorPreset = ColorPresets[0];
        }
        catch (Exception exception)
        {
            StatusMessage = $"配色方案加载失败：{exception.Message}";
        }

        NotifyColorPresetStateChanged();
    }

    private bool TryNormalizePresetName(string? name, string? ignoredPresetId, out string normalizedName, out string error)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0)
        {
            error = "请输入配色方案名称";
            return false;
        }

        if (normalizedName.Length > 32)
        {
            error = "配色方案名称不能超过 32 个字符";
            return false;
        }

        var candidateName = normalizedName;
        var isDuplicate = ColorPresets.Any(preset =>
            !string.Equals(preset.Id, ignoredPresetId, StringComparison.Ordinal)
            && string.Equals(preset.Name, candidateName, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            error = "已存在同名配色方案";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryReplaceSelectedPreset(LightingColorPreset replacement, out string error)
    {
        var index = FindPresetIndex(replacement.Id);
        if (index < 0)
        {
            error = "未找到要修改的配色方案";
            return false;
        }

        var updatedPresets = ColorPresets.ToList();
        updatedPresets[index] = replacement;
        if (!TryPersistColorPresets(updatedPresets, out error))
            return false;

        ColorPresets[index] = replacement;
        SelectedColorPreset = replacement;
        NotifyColorPresetStateChanged();
        return true;
    }

    private bool TryPersistColorPresets(IEnumerable<LightingColorPreset> presets, out string error)
    {
        if (colorPresetStore.TrySave(presets))
        {
            error = string.Empty;
            return true;
        }

        error = colorPresetStore.LastError is null
            ? "配色方案保存失败"
            : $"配色方案保存失败：{colorPresetStore.LastError.Message}";
        StatusMessage = error;
        return false;
    }

    private int FindPresetIndex(string id)
    {
        for (var index = 0; index < ColorPresets.Count; index++)
        {
            if (string.Equals(ColorPresets[index].Id, id, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private void NotifyColorPresetStateChanged()
    {
        OnPropertyChanged(nameof(ColorPresetSummary));
        OnPropertyChanged(nameof(HasColorPresets));
        OnPropertyChanged(nameof(HasSelectedColorPreset));
    }

    private AmbientLightOptions BuildAmbientOptions()
    {
        var effect = (AmbientLightEffect)(Math.Clamp(AmbientEffectIndex, 0, EffectNames.Count - 1) + 1);
        var brightness = (AmbientLightBrightness)(Math.Clamp(AmbientBrightnessIndex, 0, BrightnessNames.Count - 1) + 1);
        return new AmbientLightOptions
        {
            IsEnabled = AmbientEnabled,
            Effect = effect,
            Brightness = brightness,
            Speed = (byte)Math.Clamp((int)Math.Round(AmbientSpeed), 1, 10),
            Color = BuildColor(AmbientRed, AmbientGreen, AmbientBlue)
        };
    }

    private HaloPixelColor BuildPixelColor() => BuildColor(PixelRed, PixelGreen, PixelBlue);

    private static HaloPixelColor BuildColor(int red, int green, int blue)
        => new(ClampByte(red), ClampByte(green), ClampByte(blue));

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static double ReadAmbientSpeed()
    {
        var value = DisplayFeatureProfile.AmbientLightSpeed;
        return double.IsFinite(value) ? Math.Clamp(value, 1, 10) : 10;
    }

    private static string ToHex(int red, int green, int blue)
        => $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";
}

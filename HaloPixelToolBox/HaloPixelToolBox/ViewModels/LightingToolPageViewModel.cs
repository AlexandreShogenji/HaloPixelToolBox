using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;
using Microsoft.UI.Dispatching;
using Windows.UI;

namespace HaloPixelToolBox.ViewModels;

public partial class LightingToolPageViewModel : ViewModelBase
{
    private readonly HaloPixelLightingService lightingService = new();
    private readonly LightingColorPresetStore colorPresetStore = new();
    private readonly DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? ambientSendThrottle;
    private CancellationTokenSource? pixelSendThrottle;
    private int ambientWriteVersion;
    private int pixelWriteVersion;
    private int ambientPowerUpdateVersion;
    private int pixelPowerUpdateVersion;
    private int ambientPowerUpdatePending;
    private int pixelPowerUpdatePending;
    private bool isRestoringPowerState;
    private bool isBatchUpdatingColors;
    private bool isApplyingSharedLightingState;
    private bool isInitializingAutomationSettings = true;

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

    [ObservableProperty]
    private bool turnLightsOffWhenDisplayOff = DisplayFeatureProfile.TurnLightsOffWhenDisplayOff;

    [ObservableProperty]
    private bool scheduledLightsOffEnabled = DisplayFeatureProfile.ScheduledLightsOffEnabled;

    [ObservableProperty]
    private TimeSpan scheduledLightsOffStartTime = TimeSpan.FromMinutes(
        Math.Clamp(DisplayFeatureProfile.ScheduledLightsOffStartMinutes, 0, (24 * 60) - 1));

    [ObservableProperty]
    private TimeSpan scheduledLightsOffEndTime = TimeSpan.FromMinutes(
        Math.Clamp(DisplayFeatureProfile.ScheduledLightsOffEndMinutes, 0, (24 * 60) - 1));

    [ObservableProperty]
    private string automationStatus = App.LightingAutomation.CurrentStatus;

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

    public void CompleteAutomationSettingsInitialization()
    {
        try
        {
            TurnLightsOffWhenDisplayOff = DisplayFeatureProfile.TurnLightsOffWhenDisplayOff;
            ScheduledLightsOffEnabled = DisplayFeatureProfile.ScheduledLightsOffEnabled;
            ScheduledLightsOffStartTime = TimeSpan.FromMinutes(
                Math.Clamp(DisplayFeatureProfile.ScheduledLightsOffStartMinutes, 0, (24 * 60) - 1));
            ScheduledLightsOffEndTime = TimeSpan.FromMinutes(
                Math.Clamp(DisplayFeatureProfile.ScheduledLightsOffEndMinutes, 0, (24 * 60) - 1));

            // Two-way controls can publish their defaults while InitializeComponent is wiring
            // bindings. Notify explicitly so the persisted values win even when a property did
            // not otherwise change and therefore did not raise PropertyChanged.
            OnPropertyChanged(nameof(TurnLightsOffWhenDisplayOff));
            OnPropertyChanged(nameof(ScheduledLightsOffEnabled));
            OnPropertyChanged(nameof(ScheduledLightsOffStartTime));
            OnPropertyChanged(nameof(ScheduledLightsOffEndTime));
        }
        finally
        {
            isInitializingAutomationSettings = false;
        }
    }

    public void AttachAutomationStatus()
    {
        App.LightingAutomation.StatusChanged -= LightingAutomation_StatusChanged;
        App.LightingAutomation.StatusChanged += LightingAutomation_StatusChanged;
        HaloPixelLightingService.PreviewStateChanged -= LightingService_PreviewStateChanged;
        HaloPixelLightingService.PreviewStateChanged += LightingService_PreviewStateChanged;
        LightingControlCoordinator.ExternalMutationStarting -= LightingControl_ExternalMutationStarting;
        LightingControlCoordinator.ExternalMutationStarting += LightingControl_ExternalMutationStarting;
        AutomationStatus = App.LightingAutomation.CurrentStatus;
    }

    public void DetachAutomationStatus()
    {
        Interlocked.Increment(ref ambientWriteVersion);
        Interlocked.Increment(ref pixelWriteVersion);
        Interlocked.Increment(ref ambientPowerUpdateVersion);
        Interlocked.Increment(ref pixelPowerUpdateVersion);
        Volatile.Write(ref ambientPowerUpdatePending, 0);
        Volatile.Write(ref pixelPowerUpdatePending, 0);
        CancelPendingColorSends();
        App.LightingAutomation.StatusChanged -= LightingAutomation_StatusChanged;
        HaloPixelLightingService.PreviewStateChanged -= LightingService_PreviewStateChanged;
        LightingControlCoordinator.ExternalMutationStarting -= LightingControl_ExternalMutationStarting;
    }

    private void LightingAutomation_StatusChanged(object? sender, EventArgs e)
    {
        dispatcherQueue.TryEnqueue(() => AutomationStatus = App.LightingAutomation.CurrentStatus);
    }

    private void LightingService_PreviewStateChanged(object? sender, EventArgs e)
    {
        var state = HaloPixelLightingService.PreviewState;
        dispatcherQueue.TryEnqueue(() => ApplySharedLightingState(state));
    }

    private void LightingControl_ExternalMutationStarting(object? sender, LightingMutationStartingEventArgs e)
    {
        if ((e.Scope & LightingMutationScope.Ambient) != 0)
        {
            Interlocked.Increment(ref ambientWriteVersion);
            Interlocked.Increment(ref ambientPowerUpdateVersion);
            Volatile.Write(ref ambientPowerUpdatePending, 0);
            CancelPendingSend(ref ambientSendThrottle);
        }

        if ((e.Scope & LightingMutationScope.Pixel) != 0)
        {
            Interlocked.Increment(ref pixelWriteVersion);
            Interlocked.Increment(ref pixelPowerUpdateVersion);
            Volatile.Write(ref pixelPowerUpdatePending, 0);
            CancelPendingSend(ref pixelSendThrottle);
        }
    }

    private void ApplySharedLightingState(HaloPixelLightingPreviewState state)
    {
        // Preview notifications can be queued from worker threads. Ignore an older snapshot if a
        // newer UI or external change was published before this dispatcher callback ran.
        if (HaloPixelLightingService.PreviewState != state)
            return;

        var effectIndex = Math.Clamp((int)state.Effect - 1, 0, EffectNames.Count - 1);
        var brightnessIndex = Math.Clamp((int)state.Brightness - 1, 0, BrightnessNames.Count - 1);
        if (AmbientEnabled == state.IsEnabled
            && AmbientEffectIndex == effectIndex
            && AmbientBrightnessIndex == brightnessIndex
            && Math.Abs(AmbientSpeed - state.Speed) < 0.1
            && AmbientRed == state.AmbientColor.Red
            && AmbientGreen == state.AmbientColor.Green
            && AmbientBlue == state.AmbientColor.Blue
            && PixelScreenEnabled == state.PixelScreenEnabled
            && PixelRed == state.PixelScreenColor.Red
            && PixelGreen == state.PixelScreenColor.Green
            && PixelBlue == state.PixelScreenColor.Blue)
        {
            return;
        }

        isApplyingSharedLightingState = true;
        try
        {
            var applyAmbientPower = Volatile.Read(ref ambientPowerUpdatePending) == 0;
            var applyPixelPower = Volatile.Read(ref pixelPowerUpdatePending) == 0;
            if (applyAmbientPower)
                AmbientEnabled = state.IsEnabled;
            AmbientEffectIndex = effectIndex;
            AmbientBrightnessIndex = brightnessIndex;
            AmbientSpeed = state.Speed;
            AmbientRed = state.AmbientColor.Red;
            AmbientGreen = state.AmbientColor.Green;
            AmbientBlue = state.AmbientColor.Blue;
            if (applyPixelPower)
                PixelScreenEnabled = state.PixelScreenEnabled;
            PixelRed = state.PixelScreenColor.Red;
            PixelGreen = state.PixelScreenColor.Green;
            PixelBlue = state.PixelScreenColor.Blue;

            if (applyAmbientPower)
                DisplayFeatureProfile.AmbientLightEnabled = state.IsEnabled;
            DisplayFeatureProfile.AmbientLightEffectIndex = effectIndex;
            DisplayFeatureProfile.AmbientLightBrightnessIndex = brightnessIndex;
            DisplayFeatureProfile.AmbientLightSpeed = state.Speed;
            if (applyPixelPower)
                DisplayFeatureProfile.PixelScreenEnabled = state.PixelScreenEnabled;
            RefreshAmbientColorState();
            RefreshPixelColorState();
        }
        finally
        {
            isApplyingSharedLightingState = false;
        }
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

        var ambientVersion = Interlocked.Increment(ref ambientWriteVersion);
        var pixelVersion = Interlocked.Increment(ref pixelWriteVersion);
        SelectColorPreset(preset);
        CancelPendingColorSends();

        var ambientColor = BuildColor(preset.AmbientRed, preset.AmbientGreen, preset.AmbientBlue);
        var pixelColor = BuildColor(preset.PixelRed, preset.PixelGreen, preset.PixelBlue);
        SetAllColorChannels(ambientColor, pixelColor);

        try
        {
            await LightingControlCoordinator.SharedMutationGate.WaitAsync();
            try
            {
                if (ambientVersion != Volatile.Read(ref ambientWriteVersion)
                    || pixelVersion != Volatile.Read(ref pixelWriteVersion))
                    return;

                var ambientOptions = BuildAmbientOptionsFromProfile();
                var ambientResult = await lightingService.SetAmbientLightAsync(ambientOptions);
                var committedPixelColor = BuildPixelColorFromProfile();
                var pixelResult = !DisplayFeatureProfile.PixelScreenEnabled
                    || await lightingService.SetPixelScreenColorAsync(committedPixelColor);
                if (ambientResult || pixelResult)
                {
                    PublishPreviewStateFromProfile();
                    App.LightingAutomation.NotifyDesiredLightingStateChanged();
                }

                if (ambientVersion != Volatile.Read(ref ambientWriteVersion)
                    || pixelVersion != Volatile.Read(ref pixelWriteVersion))
                    return;

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
                LightingControlCoordinator.SharedMutationGate.Release();
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
        if (isRestoringPowerState || isApplyingSharedLightingState)
            return;

        Interlocked.Increment(ref ambientWriteVersion);
        Volatile.Write(ref ambientPowerUpdatePending, 1);
        PublishPreviewState();
        CancelPendingSend(ref ambientSendThrottle);
        var version = Interlocked.Increment(ref ambientPowerUpdateVersion);
        _ = SendAmbientPowerNowAsync(value, version);
    }

    partial void OnPixelScreenEnabledChanged(bool value)
    {
        if (isRestoringPowerState || isApplyingSharedLightingState)
            return;

        Interlocked.Increment(ref pixelWriteVersion);
        Volatile.Write(ref pixelPowerUpdatePending, 1);
        PublishPreviewState();
        CancelPendingSend(ref pixelSendThrottle);
        var version = Interlocked.Increment(ref pixelPowerUpdateVersion);
        _ = SendPixelPowerNowAsync(value, version);
    }

    partial void OnTurnLightsOffWhenDisplayOffChanged(bool value)
    {
        if (isInitializingAutomationSettings)
            return;

        DisplayFeatureProfile.TurnLightsOffWhenDisplayOff = value;
        App.LightingAutomation.UpdateSettings();
    }

    partial void OnScheduledLightsOffEnabledChanged(bool value)
    {
        if (isInitializingAutomationSettings)
            return;

        DisplayFeatureProfile.ScheduledLightsOffEnabled = value;
        App.LightingAutomation.UpdateSettings();
    }

    partial void OnScheduledLightsOffStartTimeChanged(TimeSpan value)
    {
        if (isInitializingAutomationSettings)
            return;

        DisplayFeatureProfile.ScheduledLightsOffStartMinutes = NormalizeTimePickerMinutes(value);
        App.LightingAutomation.UpdateSettings();
    }

    partial void OnScheduledLightsOffEndTimeChanged(TimeSpan value)
    {
        if (isInitializingAutomationSettings)
            return;

        DisplayFeatureProfile.ScheduledLightsOffEndMinutes = NormalizeTimePickerMinutes(value);
        App.LightingAutomation.UpdateSettings();
    }

    partial void OnAmbientEffectIndexChanged(int value)
    {
        if (isApplyingSharedLightingState)
            return;

        var version = Interlocked.Increment(ref ambientWriteVersion);
        DisplayFeatureProfile.AmbientLightEffectIndex = Math.Clamp(value, 0, EffectNames.Count - 1);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync(version);
    }

    partial void OnAmbientBrightnessIndexChanged(int value)
    {
        if (isApplyingSharedLightingState)
            return;

        var version = Interlocked.Increment(ref ambientWriteVersion);
        DisplayFeatureProfile.AmbientLightBrightnessIndex = Math.Clamp(value, 0, BrightnessNames.Count - 1);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync(version);
    }

    partial void OnAmbientSpeedChanged(double value)
    {
        if (isApplyingSharedLightingState)
            return;

        var version = Interlocked.Increment(ref ambientWriteVersion);
        DisplayFeatureProfile.AmbientLightSpeed = double.IsFinite(value) ? Math.Clamp(value, 1, 10) : 10;
        PublishPreviewState();
        QueueAmbientLightSend(version);
    }

    partial void OnSyncAmbientWithPixelChanged(bool value)
    {
        DisplayFeatureProfile.SyncAmbientWithPixel = value;
        if (!value)
            return;

        var version = Interlocked.Increment(ref ambientWriteVersion);
        SetAmbientColorChannels(BuildPixelColor(), false);
        PublishPreviewState();
        _ = SendAmbientLightNowAsync(version);
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
        if (isBatchUpdatingColors || isApplyingSharedLightingState)
            return;

        var version = Interlocked.Increment(ref ambientWriteVersion);
        RefreshAmbientColorState();
        PublishPreviewState();
        QueueAmbientLightSend(version);
    }

    private void OnPixelColorComponentChanged()
    {
        if (isBatchUpdatingColors || isApplyingSharedLightingState)
            return;

        var version = Interlocked.Increment(ref pixelWriteVersion);
        RefreshPixelColorState();
        PublishPreviewState();
        QueuePixelColorSend(version);
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

        var version = queueSend ? Interlocked.Increment(ref ambientWriteVersion) : 0;
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
            QueueAmbientLightSend(version);
    }

    private void SetPixelColorChannels(HaloPixelColor color, bool queueSend)
    {
        if (PixelRed == color.Red && PixelGreen == color.Green && PixelBlue == color.Blue)
            return;

        var version = queueSend ? Interlocked.Increment(ref pixelWriteVersion) : 0;
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
            QueuePixelColorSend(version);
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
        => HaloPixelLightingService.SetPreviewState(
            BuildAmbientOptions(),
            PixelScreenEnabled,
            BuildPixelColor());

    private void QueueAmbientLightSend(int version)
    {
        var current = new CancellationTokenSource();
        CancelPendingSend(Interlocked.Exchange(ref ambientSendThrottle, current));
        _ = SendAmbientLightAfterDelayAsync(current, version);
    }

    private void QueuePixelColorSend(int version)
    {
        var current = new CancellationTokenSource();
        CancelPendingSend(Interlocked.Exchange(ref pixelSendThrottle, current));
        _ = SendPixelColorAfterDelayAsync(current, version);
    }

    private async Task SendAmbientLightAfterDelayAsync(CancellationTokenSource source, int version)
    {
        try
        {
            await Task.Delay(160, source.Token);
            await SendAmbientLightNowAsync(version, source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref ambientSendThrottle, null, source);
            source.Dispose();
        }
    }

    private async Task SendPixelColorAfterDelayAsync(CancellationTokenSource source, int version)
    {
        try
        {
            await Task.Delay(160, source.Token);
            await SendPixelColorNowAsync(version, source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref pixelSendThrottle, null, source);
            source.Dispose();
        }
    }

    private async Task SendAmbientLightNowAsync(int version, CancellationToken cancellationToken = default)
    {
        try
        {
            await LightingControlCoordinator.SharedMutationGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref ambientWriteVersion))
                    return;

                var options = BuildAmbientOptionsFromProfile();
                var result = await lightingService.SetAmbientLightAsync(options, cancellationToken);
                if (result)
                {
                    PublishPreviewStateFromProfile();
                    App.LightingAutomation.NotifyDesiredLightingStateChanged();
                }

                if (version != Volatile.Read(ref ambientWriteVersion))
                    return;

                StatusMessage = result
                    ? (options.IsEnabled ? "氛围灯设置已生效" : "氛围灯已关闭")
                    : "未检测到花再 Halo PixelBar";
            }
            finally
            {
                LightingControlCoordinator.SharedMutationGate.Release();
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
        try
        {
            await LightingControlCoordinator.SharedMutationGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref ambientPowerUpdateVersion))
                    return;

                var options = BuildAmbientOptionsFromProfile();
                options.IsEnabled = enabled;
                var result = await lightingService.SetAmbientLightEnabledAsync(enabled, cancellationToken);
                if (result && enabled)
                    result = await lightingService.SetAmbientLightAsync(options, cancellationToken);

                if (result)
                {
                    var isCurrent = version == Volatile.Read(ref ambientPowerUpdateVersion)
                        && AmbientEnabled == enabled;
                    DisplayFeatureProfile.AmbientLightEnabled = enabled;
                    if (isCurrent)
                        Volatile.Write(ref ambientPowerUpdatePending, 0);
                    PublishPreviewStateFromProfile();
                    App.LightingAutomation.NotifyDesiredLightingStateChanged();
                }

                if (version != Volatile.Read(ref ambientPowerUpdateVersion) || AmbientEnabled != enabled)
                    return;

                if (result)
                {
                    StatusMessage = enabled ? "氛围灯已开启并恢复当前设置" : "氛围灯已关闭";
                    return;
                }

                Volatile.Write(ref ambientPowerUpdatePending, 0);
                RestoreAmbientPowerState();
                StatusMessage = "氛围灯开关未得到设备状态确认";
            }
            finally
            {
                LightingControlCoordinator.SharedMutationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref ambientPowerUpdateVersion) && AmbientEnabled == enabled)
            {
                Volatile.Write(ref ambientPowerUpdatePending, 0);
                RestoreAmbientPowerState();
                StatusMessage = $"氛围灯开关发送失败：{exception.Message}";
            }
        }
    }

    private async Task SendPixelColorNowAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version != Volatile.Read(ref pixelWriteVersion))
            return;

        var syncAmbient = SyncAmbientWithPixel;
        try
        {
            await LightingControlCoordinator.SharedMutationGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref pixelWriteVersion))
                    return;

                var pixelColor = BuildPixelColorFromProfile();
                AmbientLightOptions? ambientOptions = null;
                if (syncAmbient)
                {
                    SetAmbientColorChannels(pixelColor, false);
                    ambientOptions = BuildAmbientOptionsFromProfile();
                }

                var pixelResult = !DisplayFeatureProfile.PixelScreenEnabled
                    || await lightingService.SetPixelScreenColorAsync(pixelColor, cancellationToken);
                if (pixelResult)
                    App.LightingAutomation.NotifyDesiredLightingStateChanged();

                if (version != Volatile.Read(ref pixelWriteVersion))
                    return;

                if (!pixelResult)
                {
                    StatusMessage = "未检测到花再 Halo PixelBar";
                    return;
                }

                if (ambientOptions is not null)
                {
                    var ambientResult = await lightingService.SetAmbientLightAsync(ambientOptions, cancellationToken);
                    if (version != Volatile.Read(ref pixelWriteVersion))
                        return;

                    if (ambientResult)
                    {
                        PublishPreviewStateFromProfile();
                        App.LightingAutomation.NotifyDesiredLightingStateChanged();
                    }

                    StatusMessage = !PixelScreenEnabled
                        ? (ambientResult
                            ? "像素屏已关闭；颜色已保存并同步氛围灯"
                            : "像素屏已关闭；颜色已保存，但未能同步氛围灯")
                        : ambientResult
                        ? "像素屏颜色已生效，并同步氛围灯颜色"
                        : "像素屏颜色已生效，但未能同步氛围灯颜色";
                    return;
                }

                PublishPreviewStateFromProfile();
                StatusMessage = PixelScreenEnabled ? "像素屏颜色已生效" : "像素屏已关闭；颜色将在开启时应用";
            }
            finally
            {
                LightingControlCoordinator.SharedMutationGate.Release();
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
        try
        {
            await LightingControlCoordinator.SharedMutationGate.WaitAsync(cancellationToken);
            try
            {
                if (version != Volatile.Read(ref pixelPowerUpdateVersion))
                    return;

                var pixelColor = BuildPixelColorFromProfile();
                var result = await lightingService.SetPixelScreenEnabledAsync(pixelColor, enabled, cancellationToken);
                if (result)
                {
                    var isCurrent = version == Volatile.Read(ref pixelPowerUpdateVersion)
                        && PixelScreenEnabled == enabled;
                    DisplayFeatureProfile.PixelScreenEnabled = enabled;
                    if (isCurrent)
                        Volatile.Write(ref pixelPowerUpdatePending, 0);
                    PublishPreviewStateFromProfile();
                    App.LightingAutomation.NotifyDesiredLightingStateChanged();
                }

                if (version != Volatile.Read(ref pixelPowerUpdateVersion) || PixelScreenEnabled != enabled)
                    return;

                if (result)
                {
                    StatusMessage = enabled ? "像素屏已开启并恢复原场景" : "像素屏已关闭";
                    return;
                }

                Volatile.Write(ref pixelPowerUpdatePending, 0);
                RestorePixelScreenPowerState();
                StatusMessage = "像素屏开关未得到设备状态确认";
            }
            finally
            {
                LightingControlCoordinator.SharedMutationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Volatile.Read(ref pixelPowerUpdateVersion) && PixelScreenEnabled == enabled)
            {
                Volatile.Write(ref pixelPowerUpdatePending, 0);
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
        PublishPreviewState();
    }

    private void CancelPendingColorSends()
    {
        CancelPendingSend(ref ambientSendThrottle);
        CancelPendingSend(ref pixelSendThrottle);
    }

    private static void CancelPendingSend(ref CancellationTokenSource? source)
        => CancelPendingSend(Interlocked.Exchange(ref source, null));

    private static void CancelPendingSend(CancellationTokenSource? pendingSend)
    {
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

    private static int NormalizeTimePickerMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes);
        return Math.Clamp(minutes, 0, (24 * 60) - 1);
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

    private static AmbientLightOptions BuildAmbientOptionsFromProfile()
    {
        var effect = (AmbientLightEffect)(Math.Clamp(DisplayFeatureProfile.AmbientLightEffectIndex, 0, 5) + 1);
        var brightness = (AmbientLightBrightness)(Math.Clamp(DisplayFeatureProfile.AmbientLightBrightnessIndex, 0, 2) + 1);
        return new AmbientLightOptions
        {
            IsEnabled = DisplayFeatureProfile.AmbientLightEnabled,
            Effect = effect,
            Brightness = brightness,
            Speed = (byte)Math.Clamp((int)Math.Round(DisplayFeatureProfile.AmbientLightSpeed), 1, 10),
            Color = BuildColor(
                DisplayFeatureProfile.AmbientLightRed,
                DisplayFeatureProfile.AmbientLightGreen,
                DisplayFeatureProfile.AmbientLightBlue)
        };
    }

    private static HaloPixelColor BuildPixelColorFromProfile()
        => BuildColor(
            DisplayFeatureProfile.PixelScreenRed,
            DisplayFeatureProfile.PixelScreenGreen,
            DisplayFeatureProfile.PixelScreenBlue);

    private static void PublishPreviewStateFromProfile()
        => HaloPixelLightingService.SetPreviewState(
            BuildAmbientOptionsFromProfile(),
            DisplayFeatureProfile.PixelScreenEnabled,
            BuildPixelColorFromProfile());

    private static string ToHex(int red, int green, int blue)
        => $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";
}

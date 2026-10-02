using System.Runtime.InteropServices;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using Forms = System.Windows.Forms;

namespace HaloPixelToolBox.Services;

public sealed class LightingAutomationService : IDisposable
{
    private static readonly TimeSpan SchedulePollInterval = TimeSpan.FromSeconds(30);
    private readonly HaloPixelLightingService lightingService = new();
    private readonly SemaphoreSlim evaluationGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private ConsoleDisplayStateMonitor? displayMonitor;
    private Task? scheduleMonitorTask;
    private volatile bool displayIsOff;
    private bool displayMonitoringAvailable;
    private bool ambientForcedOff;
    private bool pixelForcedOff;
    private int forceReapply;
    private int evaluationRequested;
    private int evaluationQueued;
    private bool disposed;
    private string currentStatus = "自动关灯服务尚未启动";
    private DateTimeOffset? lastEvaluatedAt;

    public event EventHandler? StatusChanged;

    public string CurrentStatus => currentStatus;

    public LightingAutomationConfiguration CurrentConfiguration => new(
        DisplayFeatureProfile.TurnLightsOffWhenDisplayOff,
        DisplayFeatureProfile.ScheduledLightsOffEnabled,
        NormalizeMinutes(DisplayFeatureProfile.ScheduledLightsOffStartMinutes),
        NormalizeMinutes(DisplayFeatureProfile.ScheduledLightsOffEndMinutes));

    public LightingAutomationState CurrentState => BuildState();

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (displayMonitor is not null)
            return;

        if (DisplayFeatureProfile.LightsTurnedOffByAutomation)
        {
            ambientForcedOff = true;
            pixelForcedOff = true;
            // The persisted flag only describes the previous process. Re-issue the
            // actual device writes on startup instead of assuming the hardware state.
            Interlocked.Exchange(ref forceReapply, 1);
        }

        displayMonitor = new ConsoleDisplayStateMonitor();
        displayMonitor.DisplayStateChanged += DisplayMonitor_DisplayStateChanged;
        try
        {
            displayMonitor.Start();
            displayMonitoringAvailable = true;
        }
        catch (Exception exception)
        {
            displayMonitor.DisplayStateChanged -= DisplayMonitor_DisplayStateChanged;
            displayMonitor.Dispose();
            displayMonitor = null;
            SetStatus($"无法监听电脑熄屏状态：{exception.Message}");
        }
        scheduleMonitorTask = MonitorScheduleAsync(shutdown.Token);
        RequestEvaluation();
    }

    public void UpdateSettings() => RequestEvaluation();

    public async Task<DeviceCommandResult<LightingAutomationState>> ConfigureAsync(
        LightingAutomationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (configuration.StartMinutes is < 0 or >= 24 * 60
            || configuration.EndMinutes is < 0 or >= 24 * 60)
        {
            return DeviceCommandResult<LightingAutomationState>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "定时关灯时间必须在 0 到 1439 分钟之间");
        }

        if (configuration.ScheduleEnabled && configuration.StartMinutes == configuration.EndMinutes)
        {
            return DeviceCommandResult<LightingAutomationState>.Rejected(
                DeviceCommandStatus.InvalidArgument,
                "定时关灯的开始和结束时间不能相同");
        }

        DisplayFeatureProfile.TurnLightsOffWhenDisplayOff = configuration.TurnOffWhenDisplayOff;
        DisplayFeatureProfile.ScheduledLightsOffEnabled = configuration.ScheduleEnabled;
        DisplayFeatureProfile.ScheduledLightsOffStartMinutes = configuration.StartMinutes;
        DisplayFeatureProfile.ScheduledLightsOffEndMinutes = configuration.EndMinutes;

        Interlocked.Exchange(ref forceReapply, 1);
        try
        {
            await EvaluateAsync(cancellationToken);
            var state = BuildState();
            return DeviceCommandResult<LightingAutomationState>.Succeeded("自动关灯配置已保存并完成评估", state);
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandResult<LightingAutomationState>.Rejected(
                DeviceCommandStatus.Cancelled,
                "自动关灯配置已保存，但本次评估已取消");
        }
        catch (Exception exception)
        {
            SetStatus($"自动关灯执行失败：{exception.Message}");
            return DeviceCommandResult<LightingAutomationState>.Rejected(
                DeviceCommandStatus.Failed,
                $"自动关灯配置已保存，但评估失败：{exception.Message}");
        }
    }

    public void NotifyDesiredLightingStateChanged()
    {
        Interlocked.Exchange(ref forceReapply, 1);
        RequestEvaluation();
    }

    private void DisplayMonitor_DisplayStateChanged(object? sender, bool isOff)
    {
        displayIsOff = isOff;
        RequestEvaluation();
    }

    private async Task MonitorScheduleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SchedulePollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                RequestEvaluation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RequestEvaluation()
    {
        if (disposed)
            return;

        Interlocked.Exchange(ref evaluationRequested, 1);
        if (Interlocked.Exchange(ref evaluationQueued, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref evaluationRequested, 0);
                    await EvaluateAsync(shutdown.Token);
                }
                while (Volatile.Read(ref evaluationRequested) == 1);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SetStatus($"自动关灯执行失败：{exception.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref evaluationQueued, 0);
                if (Volatile.Read(ref evaluationRequested) == 1)
                    RequestEvaluation();
            }
        });
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await evaluationGate.WaitAsync(cancellationToken);
        try
        {
            var displayRuleActive = DisplayFeatureProfile.TurnLightsOffWhenDisplayOff && displayIsOff;
            var scheduleEnabled = DisplayFeatureProfile.ScheduledLightsOffEnabled;
            var startMinutes = NormalizeMinutes(DisplayFeatureProfile.ScheduledLightsOffStartMinutes);
            var endMinutes = NormalizeMinutes(DisplayFeatureProfile.ScheduledLightsOffEndMinutes);
            var scheduleValid = startMinutes != endMinutes;
            var scheduleRuleActive = scheduleEnabled
                && scheduleValid
                && IsWithinSchedule(DateTime.Now.TimeOfDay, startMinutes, endMinutes);
            var shouldTurnOff = displayRuleActive || scheduleRuleActive;
            var reapply = Interlocked.Exchange(ref forceReapply, 0) == 1;
            var ambientOperationSucceeded = ambientForcedOff && !reapply;
            var pixelOperationSucceeded = pixelForcedOff && !reapply;
            var requiresMutation = shouldTurnOff
                ? !ambientForcedOff || !pixelForcedOff || reapply
                : ambientForcedOff || pixelForcedOff;

            if (requiresMutation)
            {
                await LightingControlCoordinator.SharedMutationGate.WaitAsync(cancellationToken);
                try
                {
                    LightingControlCoordinator.NotifyExternalMutationStarting();
                    if (shouldTurnOff)
                    {
                        ambientOperationSucceeded = ambientForcedOff && !reapply
                            || await TrySetAmbientEnabledAsync(false, cancellationToken);
                        pixelOperationSucceeded = pixelForcedOff && !reapply
                            || await TrySetPixelEnabledAsync(false, cancellationToken);
                        ambientForcedOff = reapply
                            ? ambientOperationSucceeded
                            : ambientForcedOff || ambientOperationSucceeded;
                        pixelForcedOff = reapply
                            ? pixelOperationSucceeded
                            : pixelForcedOff || pixelOperationSucceeded;
                        if (ambientForcedOff || pixelForcedOff)
                            DisplayFeatureProfile.LightsTurnedOffByAutomation = true;
                    }
                    else
                    {
                        if (ambientForcedOff)
                        {
                            var restored = await RestoreAmbientAsync(cancellationToken);
                            ambientForcedOff = !restored;
                        }

                        if (pixelForcedOff)
                        {
                            var restored = await RestorePixelAsync(cancellationToken);
                            pixelForcedOff = !restored;
                        }
                    }
                }
                finally
                {
                    LightingControlCoordinator.SharedMutationGate.Release();
                }
            }

            if (shouldTurnOff)
            {
                var reason = displayRuleActive && scheduleRuleActive
                    ? "电脑已熄屏，且当前处于定时关灯时段"
                    : displayRuleActive
                        ? "电脑已熄屏"
                        : "当前处于定时关灯时段";
                SetStatus(ambientOperationSucceeded && pixelOperationSucceeded
                    ? $"{reason}；氛围灯和像素屏已关闭"
                    : $"{reason}；设备未连接或部分灯光未能关闭，将自动重试");
                return;
            }

            if (ambientForcedOff || pixelForcedOff)
            {
                SetStatus("自动关灯条件已结束，但设备状态尚未完全恢复，将自动重试");
                return;
            }

            DisplayFeatureProfile.LightsTurnedOffByAutomation = false;

            if (scheduleEnabled && !scheduleValid)
            {
                SetStatus("定时关灯的开始和结束时间不能相同");
                return;
            }

            SetStatus(BuildStandbyStatus(startMinutes, endMinutes));
        }
        finally
        {
            lastEvaluatedAt = DateTimeOffset.Now;
            evaluationGate.Release();
        }
    }

    private LightingAutomationState BuildState()
    {
        var configuration = CurrentConfiguration;
        var scheduleActive = configuration.ScheduleEnabled
            && configuration.StartMinutes != configuration.EndMinutes
            && IsWithinSchedule(DateTime.Now.TimeOfDay, configuration.StartMinutes, configuration.EndMinutes);
        return new LightingAutomationState(
            configuration,
            displayIsOff,
            scheduleActive,
            ambientForcedOff || pixelForcedOff,
            ambientForcedOff,
            pixelForcedOff,
            currentStatus,
            lastEvaluatedAt);
    }

    private async Task<bool> RestoreAmbientAsync(CancellationToken cancellationToken)
    {
        var enabled = DisplayFeatureProfile.AmbientLightEnabled;
        if (!await TrySetAmbientEnabledAsync(enabled, cancellationToken))
            return false;

        if (!enabled)
            return true;

        var options = new AmbientLightOptions
        {
            IsEnabled = true,
            Effect = (AmbientLightEffect)(Math.Clamp(DisplayFeatureProfile.AmbientLightEffectIndex, 0, 5) + 1),
            Brightness = (AmbientLightBrightness)(Math.Clamp(DisplayFeatureProfile.AmbientLightBrightnessIndex, 0, 2) + 1),
            Speed = (byte)Math.Clamp((int)Math.Round(DisplayFeatureProfile.AmbientLightSpeed), 1, 10),
            Color = BuildColor(
                DisplayFeatureProfile.AmbientLightRed,
                DisplayFeatureProfile.AmbientLightGreen,
                DisplayFeatureProfile.AmbientLightBlue)
        };
        return await lightingService.SetAmbientLightAsync(options, cancellationToken);
    }

    private Task<bool> RestorePixelAsync(CancellationToken cancellationToken)
        => lightingService.SetPixelScreenEnabledAsync(
            BuildColor(
                DisplayFeatureProfile.PixelScreenRed,
                DisplayFeatureProfile.PixelScreenGreen,
                DisplayFeatureProfile.PixelScreenBlue),
            DisplayFeatureProfile.PixelScreenEnabled,
            cancellationToken);

    private async Task<bool> TrySetAmbientEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            return await lightingService.SetAmbientLightEnabledAsync(enabled, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> TrySetPixelEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            return await lightingService.SetPixelScreenEnabledAsync(
                BuildColor(
                    DisplayFeatureProfile.PixelScreenRed,
                    DisplayFeatureProfile.PixelScreenGreen,
                    DisplayFeatureProfile.PixelScreenBlue),
                enabled,
                cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private string BuildStandbyStatus(int startMinutes, int endMinutes)
    {
        var rules = new List<string>();
        if (DisplayFeatureProfile.TurnLightsOffWhenDisplayOff)
            rules.Add(displayMonitoringAvailable ? "熄屏自动关灯已启用" : "熄屏监听不可用");
        if (DisplayFeatureProfile.ScheduledLightsOffEnabled)
            rules.Add($"每天 {FormatMinutes(startMinutes)}–{FormatMinutes(endMinutes)} 关灯");
        return rules.Count == 0 ? "自动关灯未启用" : string.Join("；", rules) + "；当前待命";
    }

    private void SetStatus(string value)
    {
        if (string.Equals(currentStatus, value, StringComparison.Ordinal))
            return;

        currentStatus = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool IsWithinSchedule(TimeSpan now, int startMinutes, int endMinutes)
    {
        var currentMinutes = (int)now.TotalMinutes;
        return startMinutes < endMinutes
            ? currentMinutes >= startMinutes && currentMinutes < endMinutes
            : currentMinutes >= startMinutes || currentMinutes < endMinutes;
    }

    private static int NormalizeMinutes(int value) => Math.Clamp(value, 0, (24 * 60) - 1);

    private static string FormatMinutes(int value) => $"{value / 60:00}:{value % 60:00}";

    private static HaloPixelColor BuildColor(int red, int green, int blue)
        => new((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255));

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        shutdown.Cancel();
        if (displayMonitor is not null)
        {
            displayMonitor.DisplayStateChanged -= DisplayMonitor_DisplayStateChanged;
            displayMonitor.Dispose();
            displayMonitor = null;
        }
        shutdown.Dispose();
    }
}

public sealed record LightingAutomationConfiguration(
    bool TurnOffWhenDisplayOff,
    bool ScheduleEnabled,
    int StartMinutes,
    int EndMinutes);

public sealed record LightingAutomationState(
    LightingAutomationConfiguration Configuration,
    bool DisplayIsOff,
    bool ScheduleActive,
    bool IsForcingOff,
    bool AmbientForcedOff,
    bool PixelForcedOff,
    string Status,
    DateTimeOffset? EvaluatedAt);

internal sealed class ConsoleDisplayStateMonitor : Forms.NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private IntPtr registration;

    public event EventHandler<bool>? DisplayStateChanged;

    public void Start()
    {
        if (Handle != IntPtr.Zero)
            return;

        CreateHandle(new Forms.CreateParams
        {
            Caption = "HaloPixelToolBox.DisplayStateMonitor",
            Parent = new IntPtr(-3)
        });
        var setting = ConsoleDisplayState;
        registration = RegisterPowerSettingNotification(Handle, ref setting, 0);
        if (registration == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new InvalidOperationException($"无法监听电脑熄屏状态（Win32 {error}）");
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == WmPowerBroadcast
            && message.WParam.ToInt32() == PbtPowerSettingChange
            && message.LParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(message.LParam);
            if (setting.PowerSetting == ConsoleDisplayState && setting.DataLength >= sizeof(byte))
                DisplayStateChanged?.Invoke(this, setting.Data == 0);
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (registration != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(registration);
            registration = IntPtr.Zero;
        }
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}

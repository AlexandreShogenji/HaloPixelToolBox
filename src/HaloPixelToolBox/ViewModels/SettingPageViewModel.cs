using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Services.Translation;
using HaloPixelToolBox.Core.Utilities;
using HaloPixelToolBox.Core.Utilities.Helpers;
using HaloPixelToolBox.Interface.Services;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;
using HaloPixelToolBox.Utilities;
using Microsoft.Win32;
using System.Reflection;
using Windows.System;
using XFEExtension.NetCore.FileExtension;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.ViewModels;

public partial class SettingPageViewModel : ViewModelBase
{
    private const string TencentEndpoint = "tmt.tencentcloudapi.com";
    private const string TencentSecretIdVariable = "TENCENTCLOUD_SECRET_ID";
    private const string TencentSecretKeyVariable = "TENCENTCLOUD_SECRET_KEY";
    private readonly DshIntegrationService dshIntegrationService = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private bool voiceAgentEventsAttached;
    private bool suppressVoiceAgentToggle;

    [ObservableProperty]
    private int closeButtonActionIndex = SystemProfile.MinimizeWhenClose ? 0 : 1;
    [ObservableProperty]
    bool isAutoStartEnable = SystemProfile.AutoStart;
    [ObservableProperty]
    private bool minimizeWhenOpen = SystemProfile.MinimizeWhenOpen;
    [ObservableProperty]
    string appCacheDirectory = AppPathHelper.AppCache;
    [ObservableProperty]
    string appCacheSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppCache)).FileSize();
    [ObservableProperty]
    string asrModelCacheDirectory = HaloPixelCachePaths.BrowserSubtitleAsrModelRoot;
    [ObservableProperty]
    string asrModelCacheSize = FileHelper.GetDirectorySize(new(HaloPixelCachePaths.BrowserSubtitleAsrModelRoot)).FileSize();
    [ObservableProperty]
    string appDataDirectory = AppPathHelper.AppLocalData;
    [ObservableProperty]
    string appDataSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppLocalData)).FileSize();
    [ObservableProperty]
    string appLogDirectory = AppPath.LogDictionary;
    [ObservableProperty]
    string appLogSize = FileHelper.GetDirectorySize(new(AppPath.LogDictionary)).FileSize();
    [ObservableProperty]
    private string currentVersion = Assembly.GetEntryAssembly()?.GetName().Version is Version version ? version.ToString(3) : "无法获取版本信息";
    [ObservableProperty]
    private string tencentCloudSecretId = string.Empty;
    [ObservableProperty]
    private string tencentCloudSecretKey = string.Empty;
    [ObservableProperty]
    private int selectedTencentRegionIndex;
    [ObservableProperty]
    private string tencentTranslationStatus = HasTencentCredentials()
        ? "已配置，展开可修改或测试"
        : "尚未配置，展开填写 SecretId 与 SecretKey";
    [ObservableProperty]
    private string tencentTranslationDetail = "配置会沿用浏览器字幕现有 Tencent TMT 调用链。";
    [ObservableProperty]
    private int quickActionSlotOneIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotOneIndex);
    [ObservableProperty]
    private int quickActionSlotTwoIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotTwoIndex);
    [ObservableProperty]
    private int quickActionSlotThreeIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotThreeIndex);
    [ObservableProperty]
    private int quickActionSlotFourIndex = NormalizeQuickActionIndex(DisplayFeatureProfile.QuickActionSlotFourIndex);
    [ObservableProperty]
    private string dshExecutablePath = DisplayFeatureProfile.DshExecutablePath;
    [ObservableProperty]
    private string dshHomePath = DisplayFeatureProfile.DshHomePath;
    [ObservableProperty]
    private string dshTaskRootDirectory = DisplayFeatureProfile.DshTaskRootDirectory;
    [ObservableProperty]
    private string dshProfileName = DisplayFeatureProfile.DshProfileName;
    [ObservableProperty]
    private double dshCommandTimeoutSeconds = DisplayFeatureProfile.DshCommandTimeoutSeconds;
    [ObservableProperty]
    private string dshStatus = "尚未检测";
    [ObservableProperty]
    private string dshDetail = "可检测 DSH、安装或更新 PixelBar 工具插件，并验证本机设备桥接。";
    [ObservableProperty]
    private bool isDshBusy;
    [ObservableProperty]
    private bool voiceAgentEnabled = DisplayFeatureProfile.VoiceAgentEnabled;
    [ObservableProperty]
    private string voiceAgentPythonPath = DisplayFeatureProfile.VoiceAgentPythonPath;
    [ObservableProperty]
    private string voiceAgentFfmpegPath = DisplayFeatureProfile.VoiceAgentFfmpegPath;
    [ObservableProperty]
    private string voiceAgentInputDevice = DisplayFeatureProfile.VoiceAgentInputDevice;
    [ObservableProperty]
    private int voiceAgentSensitivityIndex = NormalizeVoiceAgentSensitivityIndex(DisplayFeatureProfile.VoiceAgentSensitivityIndex);
    [ObservableProperty]
    private string voiceAgentDshProfileName = DisplayFeatureProfile.VoiceAgentDshProfileName;
    [ObservableProperty]
    private double voiceAgentCommandTimeoutSeconds = DisplayFeatureProfile.VoiceAgentCommandTimeoutSeconds;
    [ObservableProperty]
    private double voiceAgentCommandPauseSeconds = DisplayFeatureProfile.VoiceAgentCommandSilenceMilliseconds / 1000.0;
    [ObservableProperty]
    private double voiceAgentCommandMaxSpeechSeconds = DisplayFeatureProfile.VoiceAgentCommandMaxSpeechSeconds;
    [ObservableProperty]
    private string voiceAgentStatus = App.VoiceAgent.Current.Status;
    [ObservableProperty]
    private string voiceAgentDetail = App.VoiceAgent.Current.Detail;
    [ObservableProperty]
    private string voiceAgentLastTranscript = App.VoiceAgent.Current.LastTranscript;
    [ObservableProperty]
    private string voiceAgentLastResponse = App.VoiceAgent.Current.LastResponse;
    [ObservableProperty]
    private bool isVoiceAgentRunning = App.VoiceAgent.Current.IsRunning;
    [ObservableProperty]
    private bool isVoiceAgentBusy;

    public List<string> TencentRegions { get; } =
    [
        "ap-shanghai",
        "ap-guangzhou",
        "ap-beijing",
        "ap-hongkong"
    ];
    public IReadOnlyList<string> QuickActionOptions { get; } =
    [
        "个性场景",
        "灯光控制",
        "歌词同步",
        "视频字幕",
        "浏览器字幕",
        "自定义字幕"
    ];
    public IReadOnlyList<string> VoiceAgentSensitivityOptions { get; } =
    [
        "标准（安静近讲）",
        "灵敏（推荐）",
        "远场（可能增加误唤醒）"
    ];
    public string DshPipeName => DeviceControlPipeServer.PipeName;
    public string VoiceAgentActionText => IsVoiceAgentRunning ? "停止监听" : "启动并监听";
    public ISettingService SettingService { get; set; } = ServiceManager.GetService<ISettingService>();
    public IDialogService DialogService { get; set; } = ServiceManager.GetService<IDialogService>();

    public SettingPageViewModel()
    {
        var configuredEndpoint = DisplayFeatureProfile.TranslationApiEndpoint;
        var configuredRegion = configuredEndpoint
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .FirstOrDefault();
        var regionIndex = TencentRegions.IndexOf(configuredRegion ?? string.Empty);
        SelectedTencentRegionIndex = regionIndex >= 0 ? regionIndex : 0;

        var defaultHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh");
        if (string.IsNullOrWhiteSpace(DshHomePath) && File.Exists(Path.Combine(defaultHome, "settings.yaml")))
            DshHomePath = defaultHome;

        DshExecutablePath = dshIntegrationService.FindLikelyExecutable(DshExecutablePath);
        if (File.Exists(Path.Combine(DshHomePath, "settings.yaml")))
        {
            DshStatus = "已检测到 DSH 数据目录";
            DshDetail = $"全局设置：{Path.Combine(DshHomePath, "settings.yaml")}；插件将安装到 profile {DshProfileName}。";
        }
    }

    partial void OnCloseButtonActionIndexChanged(int value) => SystemProfile.MinimizeWhenClose = value == 0;

    partial void OnIsAutoStartEnableChanged(bool value)
    {
        SystemProfile.AutoStart = value;
        SetAutoStart(value);
    }

    partial void OnMinimizeWhenOpenChanged(bool value) => SystemProfile.MinimizeWhenOpen = value;

    partial void OnQuickActionSlotOneIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotOneIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotTwoIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotTwoIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotThreeIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotThreeIndex = NormalizeQuickActionIndex(value);

    partial void OnQuickActionSlotFourIndexChanged(int value) => DisplayFeatureProfile.QuickActionSlotFourIndex = NormalizeQuickActionIndex(value);

    partial void OnVoiceAgentEnabledChanged(bool value)
    {
        DisplayFeatureProfile.VoiceAgentEnabled = value;
        if (!suppressVoiceAgentToggle)
            _ = ApplyVoiceAgentEnabledAsync(value);
    }

    partial void OnIsVoiceAgentRunningChanged(bool value) => OnPropertyChanged(nameof(VoiceAgentActionText));

    private static int NormalizeQuickActionIndex(int value) => Math.Clamp(value, 0, 5);

    public void AttachVoiceAgentEvents()
    {
        if (voiceAgentEventsAttached)
            return;
        voiceAgentEventsAttached = true;
        App.VoiceAgent.StatusChanged += VoiceAgent_StatusChanged;
        ApplyVoiceAgentSnapshot(App.VoiceAgent.Current);
    }

    public void DetachVoiceAgentEvents()
    {
        if (!voiceAgentEventsAttached)
            return;
        voiceAgentEventsAttached = false;
        App.VoiceAgent.StatusChanged -= VoiceAgent_StatusChanged;
    }

    private void VoiceAgent_StatusChanged(object? sender, VoiceAgentSnapshot snapshot)
    {
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
            ApplyVoiceAgentSnapshot(snapshot);
        else
            dispatcherQueue.TryEnqueue(() => ApplyVoiceAgentSnapshot(snapshot));
    }

    private void ApplyVoiceAgentSnapshot(VoiceAgentSnapshot snapshot)
    {
        VoiceAgentStatus = snapshot.Status;
        VoiceAgentDetail = snapshot.Detail;
        VoiceAgentLastTranscript = snapshot.LastTranscript;
        VoiceAgentLastResponse = snapshot.LastResponse;
        IsVoiceAgentRunning = snapshot.IsRunning;
    }

    [RelayCommand]
    private void SaveDshSettings()
    {
        if (!TrySaveDshSettings(out var message))
        {
            DshStatus = "配置未保存";
            DshDetail = message;
            return;
        }

        DshStatus = "配置已保存";
        DshDetail = $"DSH_HOME：{DshHomePath} · Profile：{DshProfileName}";
    }

    [RelayCommand]
    private void DetectDsh()
    {
        var detectedHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh");
        if (File.Exists(Path.Combine(detectedHome, "settings.yaml")))
            DshHomePath = detectedHome;

        DshExecutablePath = dshIntegrationService.FindLikelyExecutable(DshExecutablePath);
        DshStatus = File.Exists(Path.Combine(DshHomePath, "settings.yaml"))
            ? "已检测到 DSH 安装"
            : "未找到 DSH 数据目录";
        DshDetail = $"启动程序：{DshExecutablePath} · DSH_HOME：{DshHomePath}";
    }

    [RelayCommand]
    private async Task TestDshAsync()
    {
        if (!TrySaveDshSettings(out var validationMessage))
        {
            DshStatus = "检测未开始";
            DshDetail = validationMessage;
            return;
        }

        await RunDshOperationAsync(
            "正在检测 DSH",
            () => dshIntegrationService.TestDshAsync(
                DshExecutablePath,
                DshHomePath,
                (int)DshCommandTimeoutSeconds));
    }

    [RelayCommand]
    private async Task InstallDshPluginAsync()
    {
        if (!TrySaveDshSettings(out var validationMessage))
        {
            DshStatus = "安装未开始";
            DshDetail = validationMessage;
            return;
        }

        await RunDshOperationAsync(
            "正在安装或更新 PixelBar 插件",
            () => dshIntegrationService.InstallOrUpdatePluginAsync(
                DshExecutablePath,
                DshHomePath,
                DshProfileName));
    }

    [RelayCommand]
    private async Task VerifyDshBridgeAsync()
    {
        await RunDshOperationAsync(
            "正在验证设备桥接",
            () => dshIntegrationService.VerifyBridgeAsync((int)DshCommandTimeoutSeconds));
    }

    [RelayCommand]
    private void SaveVoiceAgentSettings()
    {
        if (!TrySaveVoiceAgentSettings(out var message))
        {
            VoiceAgentStatus = "语音配置未保存";
            VoiceAgentDetail = message;
            return;
        }

        VoiceAgentStatus = "语音配置已保存";
        VoiceAgentDetail = IsVoiceAgentRunning
            ? "新配置会在下次启动监听时生效。"
            : $"输入：{VoiceAgentInputDevice} · 设备指令复用“音箱控制”会话";
    }

    [RelayCommand]
    private async Task DetectVoiceAgentEnvironmentAsync()
    {
        if (!TrySaveVoiceAgentSettings(out var validationMessage))
        {
            VoiceAgentStatus = "环境检测未开始";
            VoiceAgentDetail = validationMessage;
            return;
        }

        if (IsVoiceAgentBusy)
            return;

        IsVoiceAgentBusy = true;
        VoiceAgentStatus = "正在加载模型并检测语音环境";
        VoiceAgentDetail = "首次检测通常需要约 20 秒。";
        try
        {
            var result = await App.VoiceAgent.TestEnvironmentAsync(BuildVoiceAgentOptions());
            if (result.Success)
            {
                VoiceAgentPythonPath = result.PythonPath;
                VoiceAgentFfmpegPath = result.FfmpegPath;
                DisplayFeatureProfile.VoiceAgentPythonPath = result.PythonPath;
                DisplayFeatureProfile.VoiceAgentFfmpegPath = result.FfmpegPath;
            }
            VoiceAgentStatus = result.Message;
            VoiceAgentDetail = result.Detail;
        }
        catch (Exception exception)
        {
            VoiceAgentStatus = "语音环境检测失败";
            VoiceAgentDetail = exception.Message;
        }
        finally
        {
            IsVoiceAgentBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallVoiceAgentProfileAsync()
    {
        if (!TrySaveDshSettings(out var dshValidationMessage))
        {
            VoiceAgentStatus = "设备会话准备未开始";
            VoiceAgentDetail = dshValidationMessage;
            return;
        }
        if (IsVoiceAgentBusy)
            return;

        IsVoiceAgentBusy = true;
        VoiceAgentStatus = "正在准备设备会话";
        VoiceAgentDetail = $"正在连接 {DshProfileName}，后续设备口令将复用同一个会话。";
        try
        {
            await App.DshSessions.PrepareDeviceSessionAsync();
            VoiceAgentStatus = "设备会话服务已就绪";
            VoiceAgentDetail = "首次设备口令会创建“音箱控制”会话，之后持续复用；不需要启动麦克风。";
        }
        catch (Exception exception)
        {
            VoiceAgentStatus = "设备会话准备失败";
            VoiceAgentDetail = exception.Message;
        }
        finally
        {
            IsVoiceAgentBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestVoiceAgentDshAsync()
    {
        if (IsVoiceAgentBusy)
            return;
        if (IsVoiceAgentRunning)
        {
            VoiceAgentStatus = "语音 DSH 测试未开始";
            VoiceAgentDetail = "请先停止语音监听，避免测试结果被实时状态覆盖。";
            return;
        }

        if (!TrySaveDshSettings(out var validationMessage))
        {
            VoiceAgentStatus = "语音 DSH 测试未开始";
            VoiceAgentDetail = validationMessage;
            return;
        }

        IsVoiceAgentBusy = true;
        const string testPrompt = "只调用 get_pixelbar_status 一次，不得调用其他工具，不得更改任何设备状态；根据该工具的返回结果用一句中文回答。若工具不可用或失败，直接报告失败。";
        VoiceAgentStatus = "正在测试语音 DSH 链路";
        VoiceAgentDetail = "直接提交只读指令，不使用麦克风。";
        VoiceAgentLastTranscript = $"测试：{testPrompt}";
        VoiceAgentLastResponse = string.Empty;
        try
        {
            var result = await App.DshSessions.ExecuteDeviceCommandAsync(
                testPrompt,
                (int)Math.Clamp(VoiceAgentCommandTimeoutSeconds, 30, 300));
            var verifiedReadOnlyCall = result.CalledTools.Count == 1
                && result.CalledTools[0].Equals("get_pixelbar_status", StringComparison.Ordinal)
                && result.SuccessfulTools.Contains("get_pixelbar_status", StringComparer.Ordinal);
            var succeeded = result.Success && verifiedReadOnlyCall;
            VoiceAgentStatus = succeeded ? "语音 DSH 链路正常" : "语音 DSH 链路测试失败";
            VoiceAgentDetail = succeeded
                ? "DSH 已在“音箱控制”会话通过 get_pixelbar_status 完成只读查询。"
                : result.Success
                    ? "DSH 返回了文本，但没有按要求完成唯一的只读状态工具调用。"
                    : result.Message;
            VoiceAgentLastResponse = result.Success ? result.FinalText : result.Message;
        }
        catch (Exception exception)
        {
            VoiceAgentStatus = "语音 DSH 链路测试失败";
            VoiceAgentDetail = exception.Message;
            VoiceAgentLastResponse = exception.Message;
        }
        finally
        {
            IsVoiceAgentBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleVoiceAgentAsync()
    {
        if (IsVoiceAgentBusy)
            return;

        if (IsVoiceAgentRunning)
        {
            await StopVoiceAgentAsync(disableAutoStart: true);
            return;
        }

        if (!TrySaveDshSettings(out var dshValidationMessage))
        {
            VoiceAgentStatus = "语音 Agent 未启动";
            VoiceAgentDetail = dshValidationMessage;
            return;
        }
        if (!TrySaveVoiceAgentSettings(out var voiceValidationMessage))
        {
            VoiceAgentStatus = "语音 Agent 未启动";
            VoiceAgentDetail = voiceValidationMessage;
            return;
        }

        SetVoiceAgentEnabledWithoutStarting(true);
        await StartVoiceAgentAsync();
    }

    [RelayCommand]
    private async Task PlayVoiceAcknowledgementAsync()
    {
        if (IsVoiceAgentRunning)
        {
            VoiceAgentStatus = "试听未开始";
            VoiceAgentDetail = "请先停止监听，避免音箱回复被麦克风重新识别。";
            return;
        }

        var played = await App.VoiceAgent.PlayAcknowledgementAsync();
        VoiceAgentStatus = played ? "已播放“我在”" : "提示音播放失败";
        VoiceAgentDetail = played ? "使用系统默认播放设备。" : "未找到提示音文件或 Windows 播放接口返回失败。";
    }

    private async Task ApplyVoiceAgentEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            if (!TrySaveDshSettings(out var dshValidationMessage))
            {
                VoiceAgentStatus = "语音 Agent 未启动";
                VoiceAgentDetail = dshValidationMessage;
                SetVoiceAgentEnabledWithoutStarting(false);
                return;
            }
            if (!TrySaveVoiceAgentSettings(out var voiceValidationMessage))
            {
                VoiceAgentStatus = "语音 Agent 未启动";
                VoiceAgentDetail = voiceValidationMessage;
                SetVoiceAgentEnabledWithoutStarting(false);
                return;
            }
            await StartVoiceAgentAsync();
        }
        else
        {
            await StopVoiceAgentAsync(disableAutoStart: false);
        }
    }

    private async Task StartVoiceAgentAsync()
    {
        if (IsVoiceAgentBusy || IsVoiceAgentRunning)
            return;
        IsVoiceAgentBusy = true;
        try
        {
            var started = await App.VoiceAgent.StartAsync(BuildVoiceAgentOptions());
            if (!started)
                SetVoiceAgentEnabledWithoutStarting(false);
        }
        catch (Exception exception)
        {
            VoiceAgentStatus = "语音 Agent 启动失败";
            VoiceAgentDetail = exception.Message;
            SetVoiceAgentEnabledWithoutStarting(false);
        }
        finally
        {
            IsVoiceAgentBusy = false;
        }
    }

    private async Task StopVoiceAgentAsync(bool disableAutoStart)
    {
        if (IsVoiceAgentBusy)
            return;
        IsVoiceAgentBusy = true;
        try
        {
            await App.VoiceAgent.StopAsync();
            if (disableAutoStart)
                SetVoiceAgentEnabledWithoutStarting(false);
        }
        catch (Exception exception)
        {
            VoiceAgentStatus = "语音 Agent 停止失败";
            VoiceAgentDetail = exception.Message;
        }
        finally
        {
            IsVoiceAgentBusy = false;
        }
    }

    private void SetVoiceAgentEnabledWithoutStarting(bool enabled)
    {
        suppressVoiceAgentToggle = true;
        try
        {
            VoiceAgentEnabled = enabled;
            DisplayFeatureProfile.VoiceAgentEnabled = enabled;
        }
        finally
        {
            suppressVoiceAgentToggle = false;
        }
    }

    private bool TrySaveVoiceAgentSettings(out string message)
    {
        var python = Environment.ExpandEnvironmentVariables(VoiceAgentPythonPath.Trim().Trim('"'));
        var ffmpeg = Environment.ExpandEnvironmentVariables(VoiceAgentFfmpegPath.Trim().Trim('"'));
        var inputDevice = VoiceAgentInputDevice.Trim();
        var profile = DshProfileName.Trim();
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            message = "请填写 ffmpeg 或 ffmpeg.exe 的完整路径。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(inputDevice))
        {
            message = "请填写麦克风的 DirectShow 设备名称。";
            return false;
        }
        if (!DshIntegrationService.IsValidProfileName(profile))
        {
            message = "DSH Profile 名称只能包含字母、数字、点、下划线和连字符。";
            return false;
        }

        VoiceAgentPythonPath = python;
        VoiceAgentFfmpegPath = ffmpeg;
        VoiceAgentInputDevice = inputDevice;
        VoiceAgentSensitivityIndex = NormalizeVoiceAgentSensitivityIndex(VoiceAgentSensitivityIndex);
        VoiceAgentDshProfileName = profile;
        VoiceAgentCommandTimeoutSeconds = Math.Clamp(VoiceAgentCommandTimeoutSeconds, 30, 300);
        VoiceAgentCommandPauseSeconds = NormalizeVoiceAgentPauseSeconds(VoiceAgentCommandPauseSeconds);
        VoiceAgentCommandMaxSpeechSeconds = NormalizeVoiceAgentMaxSpeechSeconds(VoiceAgentCommandMaxSpeechSeconds);
        DisplayFeatureProfile.VoiceAgentPythonPath = VoiceAgentPythonPath;
        DisplayFeatureProfile.VoiceAgentFfmpegPath = VoiceAgentFfmpegPath;
        DisplayFeatureProfile.VoiceAgentInputDevice = VoiceAgentInputDevice;
        DisplayFeatureProfile.VoiceAgentSensitivityIndex = VoiceAgentSensitivityIndex;
        DisplayFeatureProfile.VoiceAgentCommandTimeoutSeconds = (int)VoiceAgentCommandTimeoutSeconds;
        DisplayFeatureProfile.VoiceAgentCommandSilenceMilliseconds = (int)Math.Round(VoiceAgentCommandPauseSeconds * 1000);
        DisplayFeatureProfile.VoiceAgentCommandMaxSpeechSeconds = (int)VoiceAgentCommandMaxSpeechSeconds;
        message = string.Empty;
        return true;
    }

    private VoiceAgentOptions BuildVoiceAgentOptions() => new(
        VoiceAgentPythonPath,
        VoiceAgentFfmpegPath,
        VoiceAgentInputDevice,
        NormalizeVoiceAgentSensitivityIndex(VoiceAgentSensitivityIndex),
        DshExecutablePath,
        DshHomePath,
        DshProfileName,
        (int)VoiceAgentCommandTimeoutSeconds)
    {
        CommandSilenceMilliseconds = (int)Math.Round(NormalizeVoiceAgentPauseSeconds(VoiceAgentCommandPauseSeconds) * 1000),
        CommandMaxSpeechSeconds = (int)NormalizeVoiceAgentMaxSpeechSeconds(VoiceAgentCommandMaxSpeechSeconds)
    };

    private static double NormalizeVoiceAgentPauseSeconds(double value)
        => double.IsFinite(value) ? Math.Clamp(value, .8, 4) : 1.8;
    private static double NormalizeVoiceAgentMaxSpeechSeconds(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 15, 120) : 60;

    partial void OnVoiceAgentCommandPauseSecondsChanged(double value)
    {
        if (double.IsFinite(value))
            DisplayFeatureProfile.VoiceAgentCommandSilenceMilliseconds = (int)Math.Round(NormalizeVoiceAgentPauseSeconds(value) * 1000);
    }

    partial void OnVoiceAgentCommandMaxSpeechSecondsChanged(double value)
    {
        if (double.IsFinite(value))
            DisplayFeatureProfile.VoiceAgentCommandMaxSpeechSeconds = (int)NormalizeVoiceAgentMaxSpeechSeconds(value);
    }

    private static int NormalizeVoiceAgentSensitivityIndex(int value) => Math.Clamp(value, 0, 2);

    private bool TrySaveDshSettings(out string message)
    {
        var executable = DshExecutablePath.Trim().Trim('"');
        var home = Environment.ExpandEnvironmentVariables(DshHomePath.Trim().Trim('"'));
        var profile = DshProfileName.Trim();
        if (string.IsNullOrWhiteSpace(executable))
        {
            message = "请填写 dsh、npx 或对应可执行文件路径。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(home))
        {
            message = "请填写 DSH_HOME。";
            return false;
        }

        if (!DshIntegrationService.IsValidProfileName(profile))
        {
            message = "Profile 名称只能包含字母、数字、点、下划线和连字符。";
            return false;
        }

        var taskRoot = Environment.ExpandEnvironmentVariables(DshTaskRootDirectory.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(taskRoot) || taskRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            message = "默认任务目录须为本机绝对路径，例如 D:\\DSH任务。";
            return false;
        }
        try { taskRoot = Path.GetFullPath(taskRoot); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            message = "默认任务目录无效。";
            return false;
        }

        DshExecutablePath = executable;
        DshHomePath = Path.GetFullPath(home);
        DshProfileName = profile;
        DshCommandTimeoutSeconds = Math.Clamp(DshCommandTimeoutSeconds, 5, 120);
        DisplayFeatureProfile.DshExecutablePath = DshExecutablePath;
        DisplayFeatureProfile.DshHomePath = DshHomePath;
        DisplayFeatureProfile.DshProfileName = DshProfileName;
        DisplayFeatureProfile.DshCommandTimeoutSeconds = (int)DshCommandTimeoutSeconds;
        DshTaskRootDirectory = taskRoot;
        DisplayFeatureProfile.DshTaskRootDirectory = taskRoot;
        message = string.Empty;
        return true;
    }

    private async Task RunDshOperationAsync(string pendingStatus, Func<Task<DshOperationResult>> operation)
    {
        if (IsDshBusy)
            return;

        IsDshBusy = true;
        DshStatus = pendingStatus;
        DshDetail = "请稍候…";
        try
        {
            var result = await operation();
            DshStatus = result.Success ? result.Message : "操作失败";
            DshDetail = result.Success
                ? FirstNonEmptyLine(result.StandardOutput) ?? result.Message
                : FirstNonEmptyLine(result.StandardError)
                  ?? FirstNonEmptyLine(result.StandardOutput)
                  ?? result.Message;
        }
        catch (Exception exception)
        {
            DshStatus = "操作失败";
            DshDetail = exception.Message;
        }
        finally
        {
            IsDshBusy = false;
        }
    }

    private static string? FirstNonEmptyLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static void SetAutoStart(bool enable) => SetAutoStart(enable, Assembly.GetExecutingAssembly().GetName().Name ?? "HaloPixelToolBox");

    private static void SetAutoStart(bool enable, string appName, string exePath = "")
    {
        string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
        if (enable)
        {
            if (exePath == string.Empty)
                exePath = Environment.ProcessPath ?? string.Empty;

            key?.SetValue(appName, $"\"{exePath}\"");
        }
        else
        {
            if (key?.GetValue(appName) != null)
            {
                key.DeleteValue(appName);
            }
        }
    }

    [RelayCommand]
    static void OpenPath(string originalPath) => Helper.OpenPath(originalPath);

    [RelayCommand]
    async Task ClearCache()
    {
        if (await DialogService.ShowDialog("cleanCacheContentDialog") == ContentDialogResult.Primary)
        {
            Directory.Delete(AppPathHelper.AppCache, true);
            AppCacheSize = FileHelper.GetDirectorySize(new(AppPathHelper.AppCache)).FileSize();
        }
    }

    [RelayCommand]
    static async Task LinkToGithubRepo() => await Launcher.LaunchUriAsync(new Uri("https://github.com/AlexandreShogenji/HaloPixelToolBox"));

    [RelayCommand]
    static async Task LinkToGithubIssue() => await Launcher.LaunchUriAsync(new Uri("https://github.com/AlexandreShogenji/HaloPixelToolBox/issues"));

    [RelayCommand]
    static async Task CheckUpgrade() => await ServiceManager.GetGlobalService<IUpgradeService>()!.CheckUpgrade();

    [RelayCommand]
    private void SaveTencentTranslation()
    {
        var secretId = TencentCloudSecretId.Trim();
        var secretKey = TencentCloudSecretKey.Trim();
        var hasNewSecretId = !string.IsNullOrWhiteSpace(secretId);
        var hasNewSecretKey = !string.IsNullOrWhiteSpace(secretKey);

        if (hasNewSecretId != hasNewSecretKey)
        {
            TencentTranslationStatus = "配置未保存";
            TencentTranslationDetail = "SecretId 与 SecretKey 必须同时填写。";
            return;
        }

        if (!hasNewSecretId && !HasTencentCredentials())
        {
            TencentTranslationStatus = "配置未保存";
            TencentTranslationDetail = "请先填写 SecretId 与 SecretKey。";
            return;
        }

        try
        {
            if (hasNewSecretId)
            {
                SetTencentEnvironmentVariable(TencentSecretIdVariable, secretId);
                SetTencentEnvironmentVariable(TencentSecretKeyVariable, secretKey);
            }

            DisplayFeatureProfile.TranslationApiEndpoint = BuildTencentEndpoint();
            DisplayFeatureProfile.TranslationApiKey = string.Empty;
            TencentCloudSecretId = string.Empty;
            TencentCloudSecretKey = string.Empty;
            TencentTranslationStatus = "已保存，等待连接测试";
            TencentTranslationDetail = $"Endpoint：{TencentEndpoint} · Region：{GetSelectedTencentRegion()}";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "保存失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestTencentTranslationAsync()
    {
        var secretId = TencentCloudSecretId.Trim();
        var secretKey = TencentCloudSecretKey.Trim();
        if ((!string.IsNullOrWhiteSpace(secretId) || !string.IsNullOrWhiteSpace(secretKey))
            && (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey)))
        {
            TencentTranslationStatus = "测试未开始";
            TencentTranslationDetail = "SecretId 与 SecretKey 必须同时填写。";
            return;
        }

        TencentTranslationStatus = "正在测试连接";
        TencentTranslationDetail = "正在测试腾讯云翻译服务…";
        try
        {
            var apiKey = string.IsNullOrWhiteSpace(secretId) ? null : $"{secretId}:{secretKey}";
            var service = new TencentMachineTranslationService(BuildTencentEndpoint(), apiKey);
            var startedAt = DateTimeOffset.Now;
            var translated = await service.TranslateAsync("こんにちは", "ja", "zh-CN");
            var elapsed = DateTimeOffset.Now - startedAt;
            TencentTranslationStatus = "连接测试成功";
            TencentTranslationDetail = $"こんにちは → {translated} · {elapsed.TotalMilliseconds:F0} ms · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "连接测试失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearTencentTranslation()
    {
        try
        {
            SetTencentEnvironmentVariable(TencentSecretIdVariable, null);
            SetTencentEnvironmentVariable(TencentSecretKeyVariable, null);
            DisplayFeatureProfile.TranslationApiKey = string.Empty;
            TencentCloudSecretId = string.Empty;
            TencentCloudSecretKey = string.Empty;
            TencentTranslationStatus = "尚未配置";
            TencentTranslationDetail = "腾讯云翻译凭据已从本机配置中移除。";
        }
        catch (Exception ex)
        {
            TencentTranslationStatus = "清除失败";
            TencentTranslationDetail = ex.Message;
        }
    }

    private string BuildTencentEndpoint() => $"{TencentEndpoint}|{GetSelectedTencentRegion()}";

    private string GetSelectedTencentRegion()
        => TencentRegions[Math.Clamp(SelectedTencentRegionIndex, 0, TencentRegions.Count - 1)];

    private static bool HasTencentCredentials()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TencentSecretIdVariable))
           && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TencentSecretKeyVariable));

    private static void SetTencentEnvironmentVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Device;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Interface.Services;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;

namespace HaloPixelToolBox.ViewModels;

public partial class MainPageViewModel : ViewModelBase
{
    private readonly HaloPixelDisplayService displayService = new();
    private readonly HaloPixelDeviceConnectionMonitor deviceConnectionMonitor = new();
    private readonly PersonalSceneRestoreService restoreService = new();
    private readonly DispatcherTimer deviceStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim deviceControlLock = new(1, 1);
    private int lastSentDeviceVolume = -1;
    private int volumeUpdateVersion;
    private bool isApplyingDeviceVolume;
    private bool isMonitoring;

    [ObservableProperty]
    private double subtitleSpeakerVolume = 14;

    [ObservableProperty]
    private string volumeStatusMessage = "字幕音箱音量范围 0–16";

    [ObservableProperty]
    private string currentSystemTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty]
    private string deviceTimeStatusMessage = "将电脑当前时间同步到设备";

    [ObservableProperty]
    private bool isCalibratingDeviceTime;

    [ObservableProperty]
    private string deviceConnectionStatusText = "正在检测设备";

    [ObservableProperty]
    private string deviceConnectionDetailText = "检测中";

    [ObservableProperty]
    private SolidColorBrush deviceConnectionBrush = new(Colors.Gray);

    [ObservableProperty]
    private string currentOutputText = "等待字幕输出";

    [ObservableProperty]
    private string currentOutputStatusText = "等待歌词、视频字幕或自定义内容输出";

    [ObservableProperty]
    private string currentOutputSourceText = "等待内容来源";

    [ObservableProperty]
    private ImageSource? currentScenePreviewSource;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Visibility currentScenePreviewVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Visibility currentOutputTextVisibility = Microsoft.UI.Xaml.Visibility.Visible;

    [ObservableProperty]
    private SolidColorBrush ambientPreviewBrush = new(Color.FromArgb(255, 45, 0, 179));

    [ObservableProperty]
    private SolidColorBrush pixelPreviewBrush = new(Color.FromArgb(255, 0, 85, 170));

    [ObservableProperty]
    private bool ambientPreviewEnabled = true;

    [ObservableProperty]
    private AmbientLightEffect ambientPreviewEffect = AmbientLightEffect.Static;

    [ObservableProperty]
    private AmbientLightBrightness ambientPreviewBrightness = AmbientLightBrightness.High;

    [ObservableProperty]
    private byte ambientPreviewSpeed = 10;

    [ObservableProperty]
    private string ambientPreviewColorText = "#2D00B3";

    [ObservableProperty]
    private string pixelPreviewColorText = "#0055AA";

    [ObservableProperty]
    private DashboardQuickActionItem quickActionOne = ResolveQuickAction(0);

    [ObservableProperty]
    private DashboardQuickActionItem quickActionTwo = ResolveQuickAction(1);

    [ObservableProperty]
    private DashboardQuickActionItem quickActionThree = ResolveQuickAction(2);

    [ObservableProperty]
    private DashboardQuickActionItem quickActionFour = ResolveQuickAction(5);

    public string SubtitleSpeakerVolumeText => $"{Math.Clamp((int)Math.Round(SubtitleSpeakerVolume), 0, 16)} / 16";

    private static INavigationViewService? NavigationViewService => ServiceManager.GetGlobalService<INavigationViewService>();

    public MainPageViewModel()
    {
        HaloPixelLightingService.SetPreviewColors(ReadAmbientProfileColor(), ReadPixelProfileColor());
        deviceStatusTimer.Tick += (_, _) =>
        {
            RefreshDeviceConnectionStatus();
            RefreshPreviewState();
            UpdateCurrentSystemTime();
        };
        RefreshDeviceConnectionStatus();
        RefreshPreviewState();
        UpdateCurrentSystemTime();
        RefreshQuickActions();
    }

    public void StartMonitoring()
    {
        if (!isMonitoring)
        {
            HaloPixelDisplayService.ContentSent += DisplayService_ContentSent;
            HaloPixelLightingService.PreviewStateChanged += LightingService_PreviewStateChanged;
            isMonitoring = true;
        }

        RefreshDeviceConnectionStatus();
        RefreshPreviewState();
        RefreshQuickActions();
        if (HaloPixelDisplayService.LastContentSent is { } lastContent)
            ApplyDisplayContent(lastContent);
        else
            ApplyDisplayContent(HaloPixelDisplayService.CreateScenePreviewSnapshot(restoreService.GetCurrentScene()));
        _ = RefreshDeviceVolumeAsync();
        deviceStatusTimer.Start();
    }

    public void StopMonitoring()
    {
        deviceStatusTimer.Stop();
        if (!isMonitoring)
            return;

        HaloPixelDisplayService.ContentSent -= DisplayService_ContentSent;
        HaloPixelLightingService.PreviewStateChanged -= LightingService_PreviewStateChanged;
        isMonitoring = false;
    }

    partial void OnSubtitleSpeakerVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(SubtitleSpeakerVolumeText));
        if (isApplyingDeviceVolume)
            return;

        _ = SetSubtitleSpeakerVolumeAsync(value);
    }

    [RelayCommand]
    private void TestDeviceConnection()
    {
        RefreshDeviceConnectionStatus();
        CurrentOutputStatusText = deviceConnectionMonitor.IsConnected()
            ? "设备连接正常，可以发送字幕、场景和灯光设置"
             : "未检测到花再 Halo PixelBar，请检查 USB 连接";
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CalibrateDeviceTimeAsync()
    {
        IsCalibratingDeviceTime = true;
        DeviceTimeStatusMessage = "正在校准设备时间…";

        await deviceControlLock.WaitAsync();
        try
        {
            var targetTime = DateTime.Now;
            CurrentSystemTimeText = targetTime.ToString("yyyy-MM-dd HH:mm:ss");
            var calibrated = await displayService.CalibrateDeviceTimeAsync(targetTime);
            DeviceTimeStatusMessage = calibrated
                ? $"校准成功：{targetTime:yyyy-MM-dd HH:mm:ss}"
                : "设备未确认校时，请检查 USB 连接";
            RefreshDeviceConnectionStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            DeviceTimeStatusMessage = "校时失败，设备通信暂不可用";
        }
        finally
        {
            deviceControlLock.Release();
            IsCalibratingDeviceTime = false;
            UpdateCurrentSystemTime();
        }
    }

    [RelayCommand]
    private async Task RestoreDefaultSceneAsync()
    {
        var restored = await restoreService.RestoreAsync(displayService);
        CurrentOutputSourceText = restored ? "个性场景" : CurrentOutputSourceText;
        CurrentOutputStatusText = restored
            ? "已恢复当前默认个性场景"
            : "未找到可恢复的默认场景，或设备尚未连接";
    }

    [RelayCommand]
    private void OpenQuickActionOne() => NavigateQuickAction(DisplayFeatureProfile.QuickActionSlotOneIndex);

    [RelayCommand]
    private void OpenQuickActionTwo() => NavigateQuickAction(DisplayFeatureProfile.QuickActionSlotTwoIndex);

    [RelayCommand]
    private void OpenQuickActionThree() => NavigateQuickAction(DisplayFeatureProfile.QuickActionSlotThreeIndex);

    [RelayCommand]
    private void OpenQuickActionFour() => NavigateQuickAction(DisplayFeatureProfile.QuickActionSlotFourIndex);

    private static void NavigateQuickAction(int actionIndex)
    {
        switch (Math.Clamp(actionIndex, 0, 5))
        {
            case 0:
                NavigationViewService?.NavigateTo<PersonalSceneToolPage>();
                break;
            case 1:
                NavigationViewService?.NavigateTo<LightingToolPage>();
                break;
            case 2:
                NavigationViewService?.NavigateTo<LyricsSubtitleToolPage>();
                break;
            case 3:
                NavigationViewService?.NavigateTo<VideoSubtitleToolPage>();
                break;
            case 4:
                NavigationViewService?.NavigateTo<BrowserTranslationSubtitleToolPage>();
                break;
            case 5:
                NavigationViewService?.NavigateTo<CustomSubtitleToolPage>();
                break;
        }
    }

    private void RefreshQuickActions()
    {
        QuickActionOne = ResolveQuickAction(DisplayFeatureProfile.QuickActionSlotOneIndex);
        QuickActionTwo = ResolveQuickAction(DisplayFeatureProfile.QuickActionSlotTwoIndex);
        QuickActionThree = ResolveQuickAction(DisplayFeatureProfile.QuickActionSlotThreeIndex);
        QuickActionFour = ResolveQuickAction(DisplayFeatureProfile.QuickActionSlotFourIndex);
    }

    private static DashboardQuickActionItem ResolveQuickAction(int actionIndex)
        => Math.Clamp(actionIndex, 0, 5) switch
        {
            0 => new("切换场景", "个性场景与自定义资源", "\uE790", CreateBrush(22, 162, 240)),
            1 => new("调整灯光", "氛围灯与像素屏双灯色板", "\uE706", CreateBrush(152, 152, 231)),
            2 => new("歌词同步", "网易云、QQ 音乐与 Spotify", "\uE8D6", CreateBrush(76, 217, 100)),
            3 => new("视频字幕", "同步 PotPlayer 本地字幕", "\uE714", CreateBrush(247, 207, 93)),
            4 => new("浏览器字幕", "B 站捕获、翻译与音乐歌词", "\uE774", CreateBrush(101, 148, 255)),
            _ => new("发送文字", "即时预览并发送自定义字幕", "\uE8D4", CreateBrush(96, 160, 240))
        };

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
        => new(Color.FromArgb(255, red, green, blue));

    private void DisplayService_ContentSent(object? sender, DisplayContentChangedEventArgs args)
        => RunOnUiThread(() => ApplyDisplayContent(args));

    private void LightingService_PreviewStateChanged(object? sender, EventArgs args)
        => RunOnUiThread(RefreshPreviewState);

    private void ApplyDisplayContent(DisplayContentChangedEventArgs args)
    {
        if (args.ContentKind == DisplayContentKind.Scene)
        {
            var sceneName = string.IsNullOrWhiteSpace(args.SceneName)
                ? "个性场景"
                : args.SceneName;
            var previewSource = CreateScenePreviewSource(args.ScenePreviewSource);
            var hasPreview = previewSource is not null;

            CurrentScenePreviewSource = previewSource;
            CurrentScenePreviewVisibility = hasPreview
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
            CurrentOutputTextVisibility = hasPreview
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
            CurrentOutputText = hasPreview ? sceneName : $"当前场景：{sceneName}";
            CurrentOutputSourceText = "个性场景";
            CurrentOutputStatusText = hasPreview
                ? $"正在预览：{sceneName}"
                : $"已切换到：{sceneName}";
            return;
        }

        var sourceName = args.ContentKind switch
        {
            DisplayContentKind.Custom => "自定义字幕",
            DisplayContentKind.Lyrics => "歌词同步",
            DisplayContentKind.VideoSubtitle => "视频字幕",
            DisplayContentKind.BrowserTranslation => "浏览器字幕",
            _ => null
        };

        if (sourceName is null)
            return;

        CurrentScenePreviewSource = null;
        CurrentScenePreviewVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        CurrentOutputTextVisibility = Microsoft.UI.Xaml.Visibility.Visible;
        CurrentOutputText = string.IsNullOrWhiteSpace(args.Text) ? "等待下一条字幕" : args.Text;
        CurrentOutputSourceText = sourceName;
        CurrentOutputStatusText = $"{sourceName}已发送到字幕屏";
    }

    private void RefreshPreviewState()
    {
        var previewState = HaloPixelLightingService.PreviewState;
        var ambient = previewState.AmbientColor;
        var pixel = previewState.PixelScreenColor;
        var ambientColor = Color.FromArgb(255, ambient.Red, ambient.Green, ambient.Blue);
        var pixelColor = Color.FromArgb(255, pixel.Red, pixel.Green, pixel.Blue);
        if (!AmbientPreviewBrush.Color.Equals(ambientColor))
            AmbientPreviewBrush = new SolidColorBrush(ambientColor);
        if (!PixelPreviewBrush.Color.Equals(pixelColor))
            PixelPreviewBrush = new SolidColorBrush(pixelColor);
        AmbientPreviewColorText = ambient.ToHex();
        PixelPreviewColorText = pixel.ToHex();
        AmbientPreviewEnabled = previewState.IsEnabled;
        AmbientPreviewEffect = previewState.Effect;
        AmbientPreviewBrightness = previewState.Brightness;
        AmbientPreviewSpeed = previewState.Speed;
    }

    private void RunOnUiThread(Action action)
    {
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        dispatcherQueue.TryEnqueue(() => action());
    }

    private static HaloPixelColor ReadAmbientProfileColor()
        => BuildColor(DisplayFeatureProfile.AmbientLightRed, DisplayFeatureProfile.AmbientLightGreen, DisplayFeatureProfile.AmbientLightBlue);

    private static HaloPixelColor ReadPixelProfileColor()
        => BuildColor(DisplayFeatureProfile.PixelScreenRed, DisplayFeatureProfile.PixelScreenGreen, DisplayFeatureProfile.PixelScreenBlue);

    private static HaloPixelColor BuildColor(int red, int green, int blue)
        => new((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255));

    private static ImageSource? CreateScenePreviewSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || !Uri.TryCreate(source, UriKind.Absolute, out var previewUri))
        {
            return null;
        }

        try
        {
            return new BitmapImage(previewUri);
        }
        catch
        {
            // 无效或已失效的预览地址不能阻断整页 x:Bind 初始化。
            return null;
        }
    }

    private void UpdateCurrentSystemTime()
        => CurrentSystemTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void RefreshDeviceConnectionStatus()
    {
        var isConnected = deviceConnectionMonitor.IsConnected();
        DeviceConnectionStatusText = isConnected ? "设备在线" : "设备离线";
        DeviceConnectionDetailText = isConnected ? "已连接" : "未连接";
        DeviceConnectionBrush = new SolidColorBrush(isConnected ? Colors.LimeGreen : Colors.Gray);
    }

    private async Task SetSubtitleSpeakerVolumeAsync(double value)
    {
        var volume = Math.Clamp((int)Math.Round(value), 0, 16);
        if (volume == lastSentDeviceVolume)
            return;

        var requestVersion = Interlocked.Increment(ref volumeUpdateVersion);
        await deviceControlLock.WaitAsync();
        try
        {
            if (requestVersion != Volatile.Read(ref volumeUpdateVersion))
                return;

            var sent = await displayService.SetDeviceVolumeAsync(volume);
            if (requestVersion != Volatile.Read(ref volumeUpdateVersion))
                return;

            if (sent)
            {
                lastSentDeviceVolume = volume;
                VolumeStatusMessage = $"字幕音箱音量已设置为 {volume}/16";
                return;
            }

            lastSentDeviceVolume = -1;
            VolumeStatusMessage = "音量写入未通过设备回读校验，请确认 USB 连接";
        }
        finally
        {
            deviceControlLock.Release();
        }
    }

    private async Task RefreshDeviceVolumeAsync()
    {
        var requestVersion = Interlocked.Increment(ref volumeUpdateVersion);
        await deviceControlLock.WaitAsync();
        try
        {
            if (requestVersion != Volatile.Read(ref volumeUpdateVersion))
                return;

            var result = await displayService.GetDeviceVolumeAsync();
            if (!result.Success || requestVersion != Volatile.Read(ref volumeUpdateVersion))
                return;

            RunOnUiThread(() =>
            {
                if (requestVersion != Volatile.Read(ref volumeUpdateVersion))
                    return;

                isApplyingDeviceVolume = true;
                try
                {
                    SubtitleSpeakerVolume = result.Current;
                    lastSentDeviceVolume = result.Current;
                    VolumeStatusMessage = $"字幕音箱当前音量 {result.Current}/{result.Maximum}";
                }
                finally
                {
                    isApplyingDeviceVolume = false;
                }
            });
        }
        finally
        {
            deviceControlLock.Release();
        }
    }
}

public sealed record DashboardQuickActionItem(string Title, string Description, string Glyph, SolidColorBrush AccentBrush);

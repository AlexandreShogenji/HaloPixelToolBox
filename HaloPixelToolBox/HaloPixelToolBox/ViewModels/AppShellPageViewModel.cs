using CommunityToolkit.Mvvm.ComponentModel;
using HaloPixelToolBox.Core.Services.Device;
using HaloPixelToolBox.Interface.Services;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Utilities.Helpers;
using HaloPixelToolBox.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;

namespace HaloPixelToolBox.ViewModels;

public partial class AppShellPageViewModel : ViewModelBase
{
    private readonly HaloPixelDeviceConnectionMonitor deviceConnectionMonitor = new();
    private readonly DispatcherTimer deviceStatusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private int selectedIndex;
    [ObservableProperty]
    private bool neverAskAgainWhenClose;
    [ObservableProperty]
    private bool canGoBack;
    [ObservableProperty]
    private string upgradeContentText = string.Empty;
    [ObservableProperty]
    private string userName = Environment.UserName;
    [ObservableProperty]
    private ImageSource userTile = Win32Helper.GetUserTile();
    [ObservableProperty]
    private double deviceIconOpacity = 0.38;
    [ObservableProperty]
    private string deviceConnectionStatusText = "音箱设备未连接";
    [ObservableProperty]
    private SolidColorBrush deviceConnectionBrush = new(Colors.Gray);

    public IDialogService DialogService { get; } = ServiceManager.GetService<IDialogService>();
    public INavigationViewService NavigationViewService { get; } = ServiceManager.GetService<INavigationViewService>();
    public Interface.Services.IPageService PageService { get; } = ServiceManager.GetService<Interface.Services.IPageService>();
    public IMessageService MessageService { get; } = ServiceManager.GetService<IMessageService>();
    public ILoadingService LoadingService { get; } = ServiceManager.GetService<ILoadingService>();
    public IUpgradeService UpgradeService { get; } = ServiceManager.GetService<IUpgradeService>();
    public ICloseWindowService? CloseWindowService { get; } = ServiceManager.GetGlobalService<ICloseWindowService>();

    public AppShellPageViewModel()
    {
        deviceStatusTimer.Tick += (_, _) => RefreshDeviceConnectionStatus();
        RefreshDeviceConnectionStatus();
        deviceStatusTimer.Start();

        NavigationViewService.NavigationService.Navigated += NavigationService_Navigated;
        if (CloseWindowService is not null)
            CloseWindowService.Closed += CloseWindowService_Closed;
        UpgradeService.Initialize(CheckForUpdatesAsync);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (isCheckingForUpdates)
            return;

        isCheckingForUpdates = true;
        try
        {
            MessageService.ShowMessage("正在查询 GitHub Releases...", "检查更新", InfoBarSeverity.Informational);
            Console.WriteLine("正在手动检查 GitHub Releases...");
            var upgradeInfo = await UpgradeHelper.GetLatestReleaseAsync();
            if (upgradeInfo == null)
            {
                MessageService.ShowMessage("无法连接 GitHub，请检查网络后重试", "检查更新", InfoBarSeverity.Error);
                Console.WriteLine("[ERROR]获取 GitHub Release 信息失败");
                return;
            }

            if (upgradeInfo.IsLatest)
            {
                MessageService.ShowMessage($"当前 v{UpgradeHelper.Version.ToString(3)} 已是最新版本", "检查更新", InfoBarSeverity.Success);
                Console.WriteLine("当前已是最新版本");
                return;
            }

            MessageService.ShowMessage($"检测到新版本 {upgradeInfo.LatestVersion}", "检查更新", InfoBarSeverity.Informational);
            Console.WriteLine($"[DEBUG]最新版本: {upgradeInfo.LatestVersion}");
            Console.WriteLine($"[DEBUG]当前版本: {UpgradeHelper.Version.ToString(3)}");
            UpgradeContentText = $"{upgradeInfo.LatestVersion}\n\n{upgradeInfo.ReleaseNotes}";
            if (await DialogService.ShowDialog("upgradeDialog") == ContentDialogResult.Primary)
            {
                Console.WriteLine("用户选择打开安装器下载链接");
                if (!await UpgradeHelper.OpenDownloadPageAsync(upgradeInfo.DownloadUrl))
                    MessageService.ShowMessage("无法打开下载链接，请前往 GitHub Releases 手动下载", "检查更新", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            MessageService.ShowMessage("检查更新失败，请稍后重试", "检查更新", InfoBarSeverity.Error);
            Console.WriteLine($"[ERROR]检查更新时发生错误：{ex}");
        }
        finally
        {
            isCheckingForUpdates = false;
        }
    }

    private void RefreshDeviceConnectionStatus()
    {
        var isConnected = deviceConnectionMonitor.IsConnected();
        DeviceIconOpacity = isConnected ? 1 : 0.38;
        DeviceConnectionStatusText = isConnected ? "音箱设备已连接" : "音箱设备未连接";
        DeviceConnectionBrush = new SolidColorBrush(isConnected ? Colors.LimeGreen : Colors.Gray);
    }

    private async void CloseWindowService_Closed(object sender, WindowEventArgs args)
    {
        args.Handled = true;
        NeverAskAgainWhenClose = SystemProfile.NeverAskAgainWhenClose;
        SelectedIndex = SystemProfile.MinimizeWhenClose ? 0 : 1;
        if (!NeverAskAgainWhenClose)
        {
            if (await DialogService.ShowDialog("closeDialog") == ContentDialogResult.Primary)
            {
                SystemProfile.NeverAskAgainWhenClose = NeverAskAgainWhenClose;
                SystemProfile.MinimizeWhenClose = SelectedIndex == 0;
            }
            else
            {
                return;
            }
        }
        if (SystemProfile.MinimizeWhenClose)
        {
            Win32Helper.ShowWindow(WindowHelper.GetHwndForCurrentWindow(), Win32Helper.SW_HIDE);
        }
        else
        {
            HaloPixelToolBox.App.ExitAfterBackgroundPersonalSceneRestore();
        }
    }

    private void NavigationService_Navigated(object? sender, NavigationEventArgs e)
    {
        CanGoBack = NavigationViewService.NavigationService.CanGoBack;
        if (e.SourcePageType is { } pageType && IsToolPage(pageType))
            DisplayFeatureProfile.LastToolPageName = pageType.FullName ?? string.Empty;
    }

    private static bool IsToolPage(Type pageType)
    {
        return pageType == typeof(MainPage)
            || pageType == typeof(PersonalSceneToolPage)
            || pageType == typeof(LightingToolPage)
            || pageType == typeof(LyricsSubtitleToolPage)
            || pageType == typeof(VideoSubtitleToolPage)
            || pageType == typeof(BrowserTranslationSubtitleToolPage)
            || pageType == typeof(CustomSubtitleToolPage);
    }
}

using HaloPixelToolBox.Interface.Services;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Services;
using HaloPixelToolBox.Services.Audio;
using HaloPixelToolBox.Utilities;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;
using XFEExtension.NetCore.XFEConsole;

namespace HaloPixelToolBox;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public ITrayIconService TrayIconService { get; } = ServiceManager.GetService<ITrayIconService>();
    public ICloseWindowService CloseWindowService { get; } = ServiceManager.GetService<ICloseWindowService>();
    public static LightingAutomationService LightingAutomation { get; } = new();
    public static LightingControlCoordinator LightingControl { get; } = new(
        () => LightingAutomation.NotifyDesiredLightingStateChanged());
    public static LyricsSubtitleControlService LyricsSubtitleControl { get; } = new();
    public static AudioControlService AudioControl { get; } = new();
    public static DeviceControlPipeServer DeviceControlPipe { get; } = new(
        new HaloPixelDeviceControlService(),
        LightingControl,
        LyricsSubtitleControl,
        LightingAutomation,
        AudioControl);
    public static VoiceAgentService VoiceAgent { get; } = new();
    public static DshSessionsService DshSessions { get; } = new();
    public static DshTaskService DshTasks { get; } = new(DshSessions, feedback: DshTaskFeedback.PublishAsync,
        displayRevisionProvider: () => HaloPixelDisplayService.ForegroundRevision);
    /// <summary>
    /// 主页窗口
    /// </summary>
    public static MainWindow MainWindow { get; set; } = new();
    private static int hasScheduledPersonalSceneRestoreExit;
    private static readonly TimeSpan BackgroundExitRestoreTimeout = TimeSpan.FromSeconds(10);
    private ToolboxInstanceGuard? instanceGuard;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey("MainInstance");
        if (!keyInstance.IsCurrent)
        {
            keyInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            Environment.Exit(0);
            return;
        }
        instanceGuard = ToolboxInstanceGuard.Acquire();
        if (!instanceGuard.IsPrimary)
        {
            instanceGuard.ActivateOrExplainDuplicate();
            instanceGuard.Dispose();
            instanceGuard = null;
            Environment.Exit(0);
            return;
        }
        XFEConsole.UseXFEConsoleLog();
        XFEConsole.Log.LogPath = Path.Combine(AppPath.LogDictionary, XFEConsole.Log.LogPath);
        Console.WriteLine("正在初始化应用程序...");
        this.InitializeComponent();
        Console.WriteLine("应用程序初始化完成");
        AppThemeHelper.Theme = SystemProfile.Theme;
        PageManager.RegisterPage(typeof(AppShellPage));
        PageManager.RegisterPage(typeof(CloudMusicLyricsToolPage));
        PageManager.RegisterPage(typeof(PersonalSceneToolPage));
        PageManager.RegisterPage(typeof(LightingToolPage));
        PageManager.RegisterPage(typeof(AudioControlPage));
        PageManager.RegisterPage(typeof(DshSessionsPage));
        PageManager.RegisterPage(typeof(LyricsSubtitleToolPage));
        PageManager.RegisterPage(typeof(VideoSubtitleToolPage));
        PageManager.RegisterPage(typeof(BrowserTranslationSubtitleToolPage));
        PageManager.RegisterPage(typeof(CustomSubtitleToolPage));
        PageManager.RegisterPage(typeof(MainPage));
        PageManager.RegisterPage(typeof(SettingPage));
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        AppInstance.GetCurrent().Activated += App_Activated;
        Console.WriteLine("事件订阅完成");
    }

    private void App_Activated(object? sender, AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Launch)
        {
            MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                // Re-launching is an explicit request to show the existing window,
                // including when its original host started it hidden.
                MainWindow.AppWindow.Show();
                MainWindow.Activate();
            });
        }
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Console.WriteLine($"[ERROR]{ex.Message}");
            Console.WriteLine($"[TRACE]{ex.StackTrace}");
        }
        else
        {
            Console.WriteLine("[ERROR]发生未处理的异常，但无法获取异常信息");
            Console.WriteLine(e.ExceptionObject.ToString());
        }
    }

    private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        VoiceAgent.Dispose();
        DshTasks.Dispose();
        DshSessions.Dispose();
        LightingAutomation.Dispose();
        DeviceControlPipe.Dispose();
        LyricsSubtitleControl.Dispose();
        Console.WriteLine("正在退出...");
        Console.WriteLine("正在保存日志...");
        var logs = Directory.GetFiles(AppPath.LogDictionary);
        if (logs.Length > 10)
        {
            foreach (var log in logs.OrderByDescending(x => x).Skip(10))
            {
                File.Delete(log);
            }
        }
        Console.WriteLine("日志保存成功");
        instanceGuard?.Dispose();
    }

    public static void ExitAfterBackgroundPersonalSceneRestore()
    {
        if (Interlocked.Exchange(ref hasScheduledPersonalSceneRestoreExit, 1) == 1)
            return;

        HideMainWindowForExit();
        _ = Task.Run(RestorePersonalSceneThenExitAsync);
    }

    private static async Task RestorePersonalSceneThenExitAsync()
    {
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var restoreTask = new PersonalSceneRestoreService()
                .RestoreAsync(new HaloPixelDisplayService(), cancellationTokenSource.Token);
            var timeoutTask = Task.Delay(BackgroundExitRestoreTimeout);

            if (await Task.WhenAny(restoreTask, timeoutTask) == restoreTask)
            {
                await restoreTask;
                Console.WriteLine("已在后台恢复当前默认个性场景，准备退出");
            }
            else
            {
                cancellationTokenSource.Cancel();
                Console.WriteLine("[WARN]后台恢复当前默认个性场景超时，准备强制退出");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[WARN]后台恢复当前默认个性场景已取消，准备退出");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN]后台恢复当前默认个性场景失败：{ex.Message}");
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void HideMainWindowForExit()
    {
        try
        {
            if (MainWindow.DispatcherQueue.HasThreadAccess)
            {
                MainWindow.AppWindow.Hide();
            }
            else
            {
                MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.AppWindow.Hide());
            }
        }
        catch
        {
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (ServiceManager.GetService<IMessageService>() is IMessageService messageService)
        {
            messageService.ShowMessage(e.Message, "发生错误", InfoBarSeverity.Error);
            Console.WriteLine($"[ERROR]{e.Message}");
            Console.WriteLine($"[TRACE]{e.Exception.StackTrace}");
            e.Handled = true;
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Console.WriteLine("主窗体启动中...");
        TrayIconService.Initilize(DispatcherQueue.GetForCurrentThread());
        CloseWindowService.Initialize(MainWindow);
        LightingAutomation.Start();
        DeviceControlPipe.Start();
        if (DisplayFeatureProfile.VoiceAgentEnabled)
            _ = StartVoiceAgentOnLaunchAsync();
        MainWindow.Content = new AppShellPage();
        Utilities.Helpers.WindowHelper.ApplyInitialBounds(MainWindow);
        if (SystemProfile.MinimizeWhenOpen)
            MainWindow.AppWindow.Hide();
        else
        {
            MainWindow.AppWindow.Show();
            MainWindow.Activate();
        }
        AppThemeHelper.MainWindow = MainWindow;
        Console.WriteLine("主窗体启动完成");
    }

    private static async Task StartVoiceAgentOnLaunchAsync()
    {
        try
        {
            await VoiceAgent.StartFromProfileAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[WARN]语音 Agent 自动启动失败：{exception.Message}");
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Lyrics;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Core.Services.Translation;
using HaloPixelToolBox.Core.Utilities;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace HaloPixelToolBox.ViewModels;

public partial class BrowserTranslationSubtitleToolPageViewModel : ViewModelBase
{
    private const string DefaultBrowserProcessName = "chrome";
    private const int ChromeDevToolsPort = 9222;
    private const int MissingBrowserVideoStopThreshold = 8;
    private const string ChromeDevToolsUserDataDirectoryName = "ChromeCDP_TMP";
    private static readonly IReadOnlyList<LanguageOption> LanguageOptionItems =
    [
        new("auto", "auto"),
        new("ja", "日语"),
        new("zh-CN", "中文"),
        new("en", "英语"),
        new("fr", "法语")
    ];

    private readonly IBrowserSubtitleCapture placeholderSubtitleCapture = new PlaceholderBrowserSubtitleCapture();
    private readonly BilibiliCcSubtitleCapture bilibiliCcSubtitleCapture = new();
    private readonly BilibiliAsrSubtitleCapture bilibiliAsrSubtitleCapture = new();
    private readonly BrowserVideoPlaybackStateReader browserPlaybackStateReader = new();
    private readonly BrowserTimelineSynchronizer browserTimelineSynchronizer;
    private readonly BilibiliVideoSubtitleSource videoSubtitleSource;
    private readonly BilibiliMusicSubtitleSource musicSubtitleSource;
    private readonly BrowserMusicMetadataResolver browserMusicMetadataResolver = new();
    private readonly BrowserMusicLyricsService browserMusicLyricsService = new();
    private readonly ITranslationService fallbackTranslationService = new PlaceholderTranslationService();
    private readonly HaloPixelDisplayService displayService = new();
    private readonly PersonalSceneRestoreService restoreService = new();
    private readonly ConcurrentDictionary<string, string> translationCache = new();
    private CancellationTokenSource? cancellationTokenSource;
    private CancellationTokenSource? manualSubtitleSendDebounceSource;
    private CancellationTokenSource? modeSwitchRestartSource;
    private IReadOnlyList<SubtitleCue> loadedCues = [];
    private string loadedSourceName = string.Empty;
    private bool isUpdatingManualSubtitleIndex;
    private bool isUpdatingBilibiliVideoUrlFromBrowser;
    private bool isUpdatingBrowserMusicFields;
    private bool hasManualBilibiliVideoUrl;
    private bool hasManualBrowserMusicSongTitle = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled
                                                  && !string.IsNullOrWhiteSpace(DisplayFeatureProfile.BrowserBilibiliMusicSongTitle);
    private bool hasManualBrowserMusicArtist = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled
                                               && !string.IsNullOrWhiteSpace(DisplayFeatureProfile.BrowserBilibiliMusicArtist);
    private bool hasSessionManualBrowserMusicSongTitle;
    private bool hasSessionManualBrowserMusicArtist;
    private int nextTimelineCueIndex;
    private int timelineCursorVersion;
    private long browserMusicLyricsSyncOffsetVersion;
    private BrowserSubtitleCaptureMode activeCaptureMode = BrowserSubtitleCaptureMode.VideoSubtitle;
    private BrowserMusicTrackMetadata? currentBrowserMusicMetadata;

    public static BrowserTranslationSubtitleToolPageViewModel Shared { get; } = new();

    public BrowserTranslationSubtitleToolPageViewModel()
    {
        browserTimelineSynchronizer = new BrowserTimelineSynchronizer(browserPlaybackStateReader);
        videoSubtitleSource = new BilibiliVideoSubtitleSource(bilibiliCcSubtitleCapture);
        musicSubtitleSource = new BilibiliMusicSubtitleSource(browserMusicLyricsService);
    }

    public List<string> OutputModeNames { get; } = ["发送原文", "发送译文", "原文 + 译文"];
    public List<string> AsrEngineNames { get; } = ["快速：SenseVoiceSmall", "高质量：Whisper large-v3-turbo", "日语优化：Kotoba-Whisper"];
    public IReadOnlyList<LanguageOption> LanguageOptions => LanguageOptionItems;
    public string BrowserMusicLyricsSyncOffsetText => BrowserMusicLyricsSyncOffsetMilliseconds switch
    {
        > 0 => $"延后 {BrowserMusicLyricsSyncOffsetMilliseconds:0} ms",
        < 0 => $"提前 {Math.Abs(BrowserMusicLyricsSyncOffsetMilliseconds):0} ms",
        _ => "不偏移"
    };

    private double browserMusicLyricsSyncOffsetSliderValue = Math.Clamp(
        Math.Round(DisplayFeatureProfile.BrowserBilibiliMusicLyricsSyncOffsetMilliseconds / 50d) * 50d,
        -3000d,
        3000d) + 3000d;

    public double BrowserMusicLyricsSyncOffsetSliderValue
    {
        get => browserMusicLyricsSyncOffsetSliderValue;
        set
        {
            var normalizedSliderValue = Math.Clamp(Math.Round(value / 50d) * 50d, 0d, 6000d);
            SetProperty(ref browserMusicLyricsSyncOffsetSliderValue, normalizedSliderValue);
            BrowserMusicLyricsSyncOffsetMilliseconds = normalizedSliderValue - 3000d;
        }
    }

    [ObservableProperty]
    private string browserProcessName = ResolveBrowserProcessName();

    [ObservableProperty]
    private string bilibiliVideoUrl = string.Empty;

    [ObservableProperty]
    private bool isBilibiliMusicModeEnabled = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled;

    [ObservableProperty]
    private string browserMusicDetectedTitle = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled
        ? "尚未识别 B 站音乐页面"
        : "B 站音乐模式未开启";

    [ObservableProperty]
    private string browserMusicSongTitle = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled
        ? DisplayFeatureProfile.BrowserBilibiliMusicSongTitle
        : string.Empty;

    [ObservableProperty]
    private string browserMusicArtist = DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled
        ? DisplayFeatureProfile.BrowserBilibiliMusicArtist
        : string.Empty;

    [ObservableProperty]
    private string browserMusicSourceUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrowserMusicLyricsSyncOffsetText))]
    private double browserMusicLyricsSyncOffsetMilliseconds = Math.Clamp(
        Math.Round(DisplayFeatureProfile.BrowserBilibiliMusicLyricsSyncOffsetMilliseconds / 50d) * 50d,
        -3000d,
        3000d);

    [ObservableProperty]
    private string translationApiEndpoint = DisplayFeatureProfile.TranslationApiEndpoint;

    [ObservableProperty]
    private string translationApiKey = DisplayFeatureProfile.TranslationApiKey;

    [ObservableProperty]
    private string tencentCloudSecretId = Environment.GetEnvironmentVariable("TENCENTCLOUD_SECRET_ID") ?? string.Empty;

    [ObservableProperty]
    private string tencentCloudSecretKey = Environment.GetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY") ?? string.Empty;

    [ObservableProperty]
    private string huggingFaceToken = Environment.GetEnvironmentVariable("HF_TOKEN")
                                      ?? Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN")
                                      ?? string.Empty;

    [ObservableProperty]
    private string huggingFaceEndpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? string.Empty;

    [ObservableProperty]
    private string huggingFaceTokenStatus = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HF_TOKEN"))
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN"))
            ? "尚未配置 HF_TOKEN；配置后可加速 Hugging Face 模型下载"
            : "已检测到 HF_TOKEN，模型下载会自动使用该令牌";

    [ObservableProperty]
    private string tencentTranslationTestResult = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY"))
        ? "尚未检测到腾讯翻译环境变量"
        : "已检测到腾讯翻译环境变量，SecretId/SecretKey 不会回显";

    [ObservableProperty]
    private string sourceLanguage = "auto";

    [ObservableProperty]
    private string targetLanguage = "zh-CN";

    [ObservableProperty]
    private int selectedOutputModeIndex = Math.Clamp(DisplayFeatureProfile.BrowserSubtitleOutputModeIndex, 0, 2);

    [ObservableProperty]
    private int selectedAsrEngineIndex = Math.Clamp(DisplayFeatureProfile.BrowserSubtitleAsrEngineIndex, 0, 2);

    [ObservableProperty]
    private string originalText = "尚未捕获原文字幕";

    [ObservableProperty]
    private string translatedText = "尚未生成翻译字幕";

    [ObservableProperty]
    private string capturedText = "尚未发送字幕";

    [ObservableProperty]
    private string statusMessage = "捕获、翻译、发送链路已搭建";

    [ObservableProperty]
    private double manualSubtitleIndex;

    [ObservableProperty]
    private double manualSubtitleMaximum;

    [ObservableProperty]
    private bool isManualSubtitleSeekEnabled;

    [ObservableProperty]
    private string manualSubtitlePositionText = "未加载字幕";

    partial void OnBrowserProcessNameChanged(string value) => DisplayFeatureProfile.BrowserProcessName = value;
    partial void OnBilibiliVideoUrlChanged(string value)
    {
        if (!isUpdatingBilibiliVideoUrlFromBrowser)
            hasManualBilibiliVideoUrl = !string.IsNullOrWhiteSpace(value);
    }
    partial void OnIsBilibiliMusicModeEnabledChanged(bool value)
    {
        var shouldRestartCapture = cancellationTokenSource is not null;
        DisplayFeatureProfile.BrowserBilibiliMusicModeEnabled = value;
        if (!value)
            ClearBrowserMusicFields();
        else if (string.IsNullOrWhiteSpace(BrowserMusicDetectedTitle) || BrowserMusicDetectedTitle == "B 站音乐模式未开启")
            BrowserMusicDetectedTitle = "尚未识别 B 站音乐页面";

        if (shouldRestartCapture)
            RestartCaptureForModeSwitch();
    }
    partial void OnBrowserMusicLyricsSyncOffsetMillisecondsChanged(double value)
    {
        var normalized = Math.Clamp(Math.Round(value / 50d) * 50d, -3000d, 3000d);
        if (Math.Abs(value - normalized) > 0.1)
        {
            BrowserMusicLyricsSyncOffsetMilliseconds = normalized;
            return;
        }

        DisplayFeatureProfile.BrowserBilibiliMusicLyricsSyncOffsetMilliseconds = normalized;
        browserMusicLyricsSyncOffsetVersion++;
        var sliderValue = normalized + 3000d;
        if (Math.Abs(browserMusicLyricsSyncOffsetSliderValue - sliderValue) > 0.1)
            SetProperty(ref browserMusicLyricsSyncOffsetSliderValue, sliderValue, nameof(BrowserMusicLyricsSyncOffsetSliderValue));
    }
    partial void OnBrowserMusicSongTitleChanged(string value)
    {
        DisplayFeatureProfile.BrowserBilibiliMusicSongTitle = value;
        if (!isUpdatingBrowserMusicFields)
        {
            hasManualBrowserMusicSongTitle = !string.IsNullOrWhiteSpace(value);
            hasSessionManualBrowserMusicSongTitle = hasManualBrowserMusicSongTitle;
        }
    }
    partial void OnBrowserMusicArtistChanged(string value)
    {
        DisplayFeatureProfile.BrowserBilibiliMusicArtist = value;
        if (!isUpdatingBrowserMusicFields)
        {
            hasManualBrowserMusicArtist = !string.IsNullOrWhiteSpace(value);
            hasSessionManualBrowserMusicArtist = hasManualBrowserMusicArtist;
        }
    }
    partial void OnTranslationApiEndpointChanged(string value) => DisplayFeatureProfile.TranslationApiEndpoint = value;
    partial void OnTranslationApiKeyChanged(string value) => DisplayFeatureProfile.TranslationApiKey = value;
    partial void OnSelectedOutputModeIndexChanged(int value) => DisplayFeatureProfile.BrowserSubtitleOutputModeIndex = Math.Clamp(value, 0, 2);
    partial void OnSelectedAsrEngineIndexChanged(int value) => DisplayFeatureProfile.BrowserSubtitleAsrEngineIndex = Math.Clamp(value, 0, 2);
    partial void OnManualSubtitleIndexChanged(double value)
    {
        UpdateManualSubtitlePositionText();
        if (!isUpdatingManualSubtitleIndex && IsManualSubtitleSeekEnabled)
            DebounceSendManualSubtitle();
    }

    private void RestartCaptureForModeSwitch()
    {
        modeSwitchRestartSource?.Cancel();
        var restartSource = new CancellationTokenSource();
        modeSwitchRestartSource = restartSource;
        _ = RestartCaptureForModeSwitchAsync(restartSource);
    }

    private async Task RestartCaptureForModeSwitchAsync(CancellationTokenSource restartSource)
    {
        try
        {
            StopCapture(returnToDefaultScene: false, updateStatus: false);
            await Task.Delay(250, restartSource.Token);
            StatusMessage = IsBilibiliMusicModeEnabled
                ? "已切换到 B 站音乐模式，正在重启同步歌词链路..."
                : "已关闭 B 站音乐模式，正在重启视频字幕链路...";
            await StartCaptureAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"切换字幕链路失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(modeSwitchRestartSource, restartSource))
                modeSwitchRestartSource = null;
            restartSource.Dispose();
        }
    }

    [RelayCommand]
    private void LaunchCdpBrowser()
    {
        BrowserProcessName = DefaultBrowserProcessName;

        var chromePath = ResolveChromeExecutablePath();
        if (string.IsNullOrWhiteSpace(chromePath))
        {
            StatusMessage = "未找到 Chrome，可先安装 Chrome 或检查安装路径";
            return;
        }

        try
        {
            var cdpUserDataDirectory = Path.Combine(Path.GetTempPath(), ChromeDevToolsUserDataDirectoryName);
            Directory.CreateDirectory(cdpUserDataDirectory);

            var launchUrl = NormalizeBilibiliVideoUrl(BilibiliVideoUrl);
            var processStartInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                UseShellExecute = false
            };
            processStartInfo.ArgumentList.Add($"--remote-debugging-port={ChromeDevToolsPort}");
            processStartInfo.ArgumentList.Add("--remote-allow-origins=*");
            processStartInfo.ArgumentList.Add($"--user-data-dir={cdpUserDataDirectory}");
            processStartInfo.ArgumentList.Add("--no-first-run");
            processStartInfo.ArgumentList.Add("--no-default-browser-check");
            processStartInfo.ArgumentList.Add("--disable-extensions");

            if (!string.IsNullOrWhiteSpace(launchUrl))
                processStartInfo.ArgumentList.Add(launchUrl);

            Process.Start(processStartInfo);
            StatusMessage = string.IsNullOrWhiteSpace(launchUrl)
                ? $"已启动 Chrome CDP 实例：端口 {ChromeDevToolsPort}"
                : $"已启动 Chrome CDP 实例并打开视频：{launchUrl}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动 Chrome CDP 实例失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestTranslationAsync()
    {
        try
        {
            var service = CreateTencentTranslationService();
            var translated = await service.TranslateAsync("こんにちは", "ja", "zh-CN");
            OriginalText = "こんにちは";
            TranslatedText = translated;
            TencentTranslationTestResult = $"测试成功：こんにちは -> {translated}";
            StatusMessage = $"腾讯翻译测试成功：{translated}";
        }
        catch (Exception ex)
        {
            TencentTranslationTestResult = $"测试失败：{ex.Message}";
            StatusMessage = $"腾讯翻译测试失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ConfigureTencentEnvironment()
    {
        if (string.IsNullOrWhiteSpace(TencentCloudSecretId) || string.IsNullOrWhiteSpace(TencentCloudSecretKey))
        {
            TencentTranslationTestResult = "请先填写 TENCENTCLOUD_SECRET_ID 和 TENCENTCLOUD_SECRET_KEY";
            StatusMessage = TencentTranslationTestResult;
            return;
        }

        try
        {
            var secretId = TencentCloudSecretId.Trim();
            var secretKey = TencentCloudSecretKey.Trim();
            Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_ID", secretId);
            Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY", secretKey);
            Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_ID", secretId, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY", secretKey, EnvironmentVariableTarget.User);

            if (string.IsNullOrWhiteSpace(TranslationApiEndpoint))
                TranslationApiEndpoint = "tmt.tencentcloudapi.com|ap-shanghai";

            TranslationApiKey = string.Empty;
            translationCache.Clear();
            TencentTranslationTestResult = "环境变量已写入，SecretId/SecretKey 已清空显示，可点击测试腾讯翻译验证";
            StatusMessage = "腾讯翻译环境变量已写入，可点击测试腾讯翻译验证";
        }
        catch (Exception ex)
        {
            TencentTranslationTestResult = $"配置失败：{ex.Message}";
            StatusMessage = $"配置腾讯翻译环境变量失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ConfigureApiEnvironment()
    {
        try
        {
            var hasTencentSecret = !string.IsNullOrWhiteSpace(TencentCloudSecretId)
                                   && !string.IsNullOrWhiteSpace(TencentCloudSecretKey);
            var hasHuggingFaceToken = !string.IsNullOrWhiteSpace(HuggingFaceToken);
            var hasHuggingFaceEndpoint = !string.IsNullOrWhiteSpace(HuggingFaceEndpoint);
            if (!hasTencentSecret && !hasHuggingFaceToken && !hasHuggingFaceEndpoint)
            {
                TencentTranslationTestResult = "请先填写腾讯翻译密钥、HF_TOKEN 或 HF_ENDPOINT";
                HuggingFaceTokenStatus = "请先填写 HF_TOKEN 或 HF_ENDPOINT";
                StatusMessage = "请先填写需要保存的 API 环境变量";
                return;
            }

            if (hasTencentSecret)
            {
                var secretId = TencentCloudSecretId.Trim();
                var secretKey = TencentCloudSecretKey.Trim();
                Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_ID", secretId);
                Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY", secretKey);
                Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_ID", secretId, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY", secretKey, EnvironmentVariableTarget.User);
                if (string.IsNullOrWhiteSpace(TranslationApiEndpoint))
                    TranslationApiEndpoint = "tmt.tencentcloudapi.com|ap-shanghai";

                TranslationApiKey = string.Empty;
                translationCache.Clear();
                TencentTranslationTestResult = "腾讯翻译环境变量已写入，SecretKey 以遮盖形式回显，可点击测试腾讯翻译验证";
            }

            if (hasHuggingFaceToken)
            {
                var token = HuggingFaceToken.Trim();
                Environment.SetEnvironmentVariable("HF_TOKEN", token);
                Environment.SetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN", token);
                Environment.SetEnvironmentVariable("HF_TOKEN", token, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN", token, EnvironmentVariableTarget.User);
                HuggingFaceTokenStatus = "HF_TOKEN 已写入环境变量，ASR 模型下载会自动使用该令牌";
            }

            if (hasHuggingFaceEndpoint)
            {
                var endpoint = HuggingFaceEndpoint.Trim().TrimEnd('/');
                Environment.SetEnvironmentVariable("HF_ENDPOINT", endpoint);
                Environment.SetEnvironmentVariable("HF_ENDPOINT", endpoint, EnvironmentVariableTarget.User);
                HuggingFaceEndpoint = endpoint;
                HuggingFaceTokenStatus = string.IsNullOrWhiteSpace(HuggingFaceTokenStatus)
                    ? $"HF_ENDPOINT 已写入环境变量：{endpoint}"
                    : $"{HuggingFaceTokenStatus}；HF_ENDPOINT={endpoint}";
            }

            StatusMessage = "API 环境变量已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"API 环境变量保存失败：{ex.Message}";
            TencentTranslationTestResult = StatusMessage;
            HuggingFaceTokenStatus = StatusMessage;
        }
    }

    [RelayCommand]
    private async Task SendManualBilibiliMusicAsync()
    {
        if (!IsBilibiliMusicModeEnabled)
        {
            ClearBrowserMusicFields();
            StatusMessage = "B 站音乐模式未开启，已跳过手动发送";
            return;
        }

        var manualTitle = BrowserMusicSongTitle.Trim();
        var manualArtist = BrowserMusicArtist.Trim();
        if (string.IsNullOrWhiteSpace(manualTitle))
        {
            StatusMessage = "请先填写歌名，再发送手动修改";
            return;
        }

        var manualOverride = new BrowserMusicManualOverride(manualTitle, manualArtist);
        ApplyManualBrowserMusicOverride(manualOverride);
        modeSwitchRestartSource?.Cancel();
        modeSwitchRestartSource = null;
        StopCapture(returnToDefaultScene: false, updateStatus: false);
        cancellationTokenSource = new CancellationTokenSource();
        activeCaptureMode = BrowserSubtitleCaptureMode.BilibiliMusic;

        try
        {
            var target = await ResolveBrowserCaptureTargetAsync(BuildConfiguration(), cancellationTokenSource.Token);
            var configuration = target.Configuration;
            if (string.IsNullOrWhiteSpace(configuration.BilibiliVideoUrl))
            {
                StatusMessage = "未识别到可用的 B 站视频页面，无法发送手动修改";
                return;
            }

            StatusMessage = $"正在按手动修改查询同步歌词：{manualTitle} {manualArtist}".Trim();
            await CaptureBilibiliMusicLyricsOrAsrAsync(configuration, target.Snapshot, cancellationTokenSource.Token, manualOverride);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "手动修改发送已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送手动修改失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        StopCapture(returnToDefaultScene: false, updateStatus: false);
        cancellationTokenSource = new CancellationTokenSource();

        try
        {
            activeCaptureMode = IsBilibiliMusicModeEnabled
                ? BrowserSubtitleCaptureMode.BilibiliMusic
                : BrowserSubtitleCaptureMode.VideoSubtitle;
            if (activeCaptureMode == BrowserSubtitleCaptureMode.VideoSubtitle)
                ClearBrowserMusicFields();

            var target = await ResolveBrowserCaptureTargetAsync(BuildConfiguration(), cancellationTokenSource.Token);
            var configuration = target.Configuration;
            if (!string.IsNullOrWhiteSpace(configuration.BilibiliVideoUrl))
            {
                if (activeCaptureMode == BrowserSubtitleCaptureMode.BilibiliMusic)
                {
                    await CaptureBilibiliMusicLyricsOrAsrAsync(configuration, target.Snapshot, cancellationTokenSource.Token);
                    return;
                }

                await CaptureBilibiliCcAsync(configuration, cancellationTokenSource.Token);
                return;
            }

            await CapturePlaceholderAsync(configuration, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "实时字幕捕获已停止";
        }
        catch (Exception ex)
        {
            StatusMessage = $"字幕捕获失败：{ex.Message}";
        }
    }

    private void SetBilibiliVideoUrlFromBrowser(string value)
    {
        isUpdatingBilibiliVideoUrlFromBrowser = true;
        try
        {
            BilibiliVideoUrl = NormalizeBilibiliVideoUrl(value);
        }
        finally
        {
            isUpdatingBilibiliVideoUrlFromBrowser = false;
        }
    }

    private async Task<BrowserCaptureTarget> ResolveBrowserCaptureTargetAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        var probe = await browserPlaybackStateReader.ProbeCurrentBilibiliSnapshotAsync(
            configuration.BrowserProcessName,
            string.Empty,
            activeCaptureMode == BrowserSubtitleCaptureMode.BilibiliMusic,
            cancellationToken);
        var snapshot = probe.Snapshot;
        if (snapshot is not null && !string.IsNullOrWhiteSpace(snapshot.Url))
        {
            SetBilibiliVideoUrlFromBrowser(snapshot.Url);
            StatusMessage = $"{probe.Message}：{snapshot.Title}";
            return new BrowserCaptureTarget(BuildConfiguration(), snapshot);
        }

        if (hasManualBilibiliVideoUrl && !string.IsNullOrWhiteSpace(configuration.BilibiliVideoUrl))
        {
            StatusMessage = string.IsNullOrWhiteSpace(probe.Message)
                ? "未读取到浏览器播放进度，使用手动填写的 B 站 URL"
                : $"{probe.Message}；使用手动填写的 B 站 URL";
            return new BrowserCaptureTarget(configuration, null);
        }

        SetBilibiliVideoUrlFromBrowser(string.Empty);
        StatusMessage = probe.Message;
        return new BrowserCaptureTarget(BuildConfiguration(), null);
    }

    private async Task<BrowserMusicTrackMetadata?> ResolveBrowserMusicMetadataAsync(
        BrowserTranslationConfiguration configuration,
        bool preserveUserEdits,
        BrowserVideoPlaybackSnapshot? knownSnapshot = null,
        bool preferPageMetadata = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = knownSnapshot;
        var needsMusicMetadataRead = snapshot is null
                                     || (string.IsNullOrWhiteSpace(snapshot.MusicTitle)
                                         && string.IsNullOrWhiteSpace(snapshot.MusicArtist)
                                         && string.IsNullOrWhiteSpace(snapshot.MusicAlbum));
        if (needsMusicMetadataRead)
        {
            var preferredBvid = ExtractBvid(snapshot?.Url ?? configuration.BilibiliVideoUrl);
            var probe = await browserPlaybackStateReader.ProbeCurrentBilibiliSnapshotAsync(
                configuration.BrowserProcessName,
                preferredBvid,
                readMusicMetadata: true,
                cancellationToken: cancellationToken);
            snapshot = probe.Snapshot ?? snapshot;
            if (snapshot is null)
            {
                ClearBrowserMusicFields();
                StatusMessage = probe.Message;
                return null;
            }
        }

        if (snapshot is null)
        {
            ClearBrowserMusicFields();
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Url))
            SetBilibiliVideoUrlFromBrowser(snapshot.Url);

        var metadata = browserMusicMetadataResolver.Resolve(snapshot);
        ApplyBrowserMusicMetadata(metadata, preserveUserEdits, preferPageMetadata);
        return metadata;
    }

    private void ApplyBrowserMusicMetadata(BrowserMusicTrackMetadata metadata, bool preserveUserEdits, bool preferPageMetadata)
    {
        var previousMetadata = currentBrowserMusicMetadata;
        var previousSourceUrl = BrowserMusicSourceUrl;
        var previousSourceTitle = previousMetadata?.SourceTitle ?? string.Empty;
        var hasPreviousSource = !string.IsNullOrWhiteSpace(previousSourceUrl);
        var sourceChanged = hasPreviousSource
                            && !previousSourceUrl.Equals(metadata.SourceUrl, StringComparison.OrdinalIgnoreCase);
        var titleChanged = !string.IsNullOrWhiteSpace(previousSourceTitle)
                           && !NormalizeBrowserMusicIdentity(previousSourceTitle).Equals(
                               NormalizeBrowserMusicIdentity(metadata.SourceTitle),
                               StringComparison.OrdinalIgnoreCase);
        var trackChanged = previousMetadata is not null && HasDifferentBrowserMusicMetadata(previousMetadata, metadata);
        var shouldResetManualEdits = sourceChanged || (hasPreviousSource && titleChanged) || trackChanged;
        var firstMetadataForPage = currentBrowserMusicMetadata is null;
        var shouldPreferDetectedTitle = shouldResetManualEdits
                                        || !preserveUserEdits
                                        || (firstMetadataForPage && !hasSessionManualBrowserMusicSongTitle)
                                        || (metadata.HasPageMusicTitle && preferPageMetadata);
        var shouldPreferDetectedArtist = shouldResetManualEdits
                                         || !preserveUserEdits
                                         || (firstMetadataForPage && !hasSessionManualBrowserMusicArtist)
                                         || (metadata.HasPageMusicArtist && preferPageMetadata);

        if (shouldResetManualEdits)
        {
            hasManualBrowserMusicSongTitle = false;
            hasManualBrowserMusicArtist = false;
            hasSessionManualBrowserMusicSongTitle = false;
            hasSessionManualBrowserMusicArtist = false;
        }

        currentBrowserMusicMetadata = metadata;
        BrowserMusicSourceUrl = metadata.SourceUrl;
        var detectedName = string.Join(" / ", new[] { metadata.Title, metadata.Artist }.Where(value => !string.IsNullOrWhiteSpace(value)));
        BrowserMusicDetectedTitle = metadata.HasPageMusicMetadata && !string.IsNullOrWhiteSpace(detectedName)
            ? $"已识别：{detectedName}"
            : !string.IsNullOrWhiteSpace(detectedName)
                ? $"标题兜底：{detectedName}"
                : string.IsNullOrWhiteSpace(metadata.SourceTitle)
                    ? metadata.SourceUrl
                    : metadata.SourceTitle;

        isUpdatingBrowserMusicFields = true;
        try
        {
            if (shouldPreferDetectedTitle || !hasManualBrowserMusicSongTitle || string.IsNullOrWhiteSpace(BrowserMusicSongTitle))
            {
                BrowserMusicSongTitle = metadata.Title;
                if (shouldPreferDetectedTitle)
                {
                    hasManualBrowserMusicSongTitle = false;
                    hasSessionManualBrowserMusicSongTitle = false;
                }
            }

            if (shouldPreferDetectedArtist || !hasManualBrowserMusicArtist || string.IsNullOrWhiteSpace(BrowserMusicArtist))
            {
                BrowserMusicArtist = metadata.Artist;
                if (shouldPreferDetectedArtist)
                {
                    hasManualBrowserMusicArtist = false;
                    hasSessionManualBrowserMusicArtist = false;
                }
            }
        }
        finally
        {
            isUpdatingBrowserMusicFields = false;
        }
    }

    private void ClearBrowserMusicFields()
    {
        currentBrowserMusicMetadata = null;
        hasManualBrowserMusicSongTitle = false;
        hasManualBrowserMusicArtist = false;
        hasSessionManualBrowserMusicSongTitle = false;
        hasSessionManualBrowserMusicArtist = false;
        BrowserMusicDetectedTitle = IsBilibiliMusicModeEnabled
            ? "尚未识别 B 站音乐页面"
            : "B 站音乐模式未开启";
        BrowserMusicSourceUrl = string.Empty;
        isUpdatingBrowserMusicFields = true;
        try
        {
            BrowserMusicSongTitle = string.Empty;
            BrowserMusicArtist = string.Empty;
        }
        finally
        {
            isUpdatingBrowserMusicFields = false;
        }
    }

    private void ApplyManualBrowserMusicOverride(BrowserMusicManualOverride manualOverride)
    {
        isUpdatingBrowserMusicFields = true;
        try
        {
            BrowserMusicSongTitle = manualOverride.Title;
            BrowserMusicArtist = manualOverride.Artist;
        }
        finally
        {
            isUpdatingBrowserMusicFields = false;
        }

        hasManualBrowserMusicSongTitle = !string.IsNullOrWhiteSpace(manualOverride.Title);
        hasManualBrowserMusicArtist = !string.IsNullOrWhiteSpace(manualOverride.Artist);
        hasSessionManualBrowserMusicSongTitle = hasManualBrowserMusicSongTitle;
        hasSessionManualBrowserMusicArtist = hasManualBrowserMusicArtist;
        DisplayFeatureProfile.BrowserBilibiliMusicSongTitle = manualOverride.Title;
        DisplayFeatureProfile.BrowserBilibiliMusicArtist = manualOverride.Artist;
    }

    private LyricsQuery BuildBrowserMusicLyricsQuery(BrowserTranslationConfiguration configuration, BrowserMusicTrackMetadata? metadata)
    {
        metadata ??= currentBrowserMusicMetadata;

        var title = BrowserMusicSongTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = metadata?.Title.Trim() ?? string.Empty;

        var artist = BrowserMusicArtist.Trim();
        if (string.IsNullOrWhiteSpace(artist))
            artist = metadata?.Artist.Trim() ?? string.Empty;

        var keyword = string.Join(' ', new[] { title, artist }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            keyword = metadata?.SourceTitle.Trim() ?? string.Empty;

        return new LyricsQuery
        {
            Provider = LyricsProviderKind.Custom,
            Keyword = keyword,
            Title = title,
            Artist = artist,
            Album = metadata?.Album ?? string.Empty,
            Duration = metadata?.Duration,
            PlatformTrackId = metadata?.PlatformTrackId ?? ExtractBvid(configuration.BilibiliVideoUrl),
            SourceUrl = metadata?.SourceUrl ?? configuration.BilibiliVideoUrl,
            PreferSyncedLyrics = true
        };
    }

    private async Task CapturePlaceholderAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        StatusMessage = "占位字幕捕获已启动";
        await foreach (var cue in placeholderSubtitleCapture.CaptureAsync(configuration, cancellationToken))
        {
            var translated = await TranslateIfNeededAsync(cue.Text, cancellationToken);
            await SendSubtitleAsync(cue.Text, translated, cancellationToken, cue.End - cue.Start);
        }
    }

    private async Task CaptureBilibiliMusicLyricsOrAsrAsync(
        BrowserTranslationConfiguration configuration,
        BrowserVideoPlaybackSnapshot? knownSnapshot,
        CancellationToken cancellationToken,
        BrowserMusicManualOverride? manualOverride = null)
    {
        if (await TryCaptureBilibiliMusicLyricsAsync(configuration, knownSnapshot, cancellationToken, manualOverride))
            return;

        StatusMessage = "未找到可同步的 B 站音乐歌词，正在回退到 ASR 识别...";
        await CaptureBilibiliAsrStreamingAsync(configuration, cancellationToken);
    }

    private async Task<bool> TryCaptureBilibiliMusicLyricsAsync(
        BrowserTranslationConfiguration configuration,
        BrowserVideoPlaybackSnapshot? knownSnapshot,
        CancellationToken cancellationToken,
        BrowserMusicManualOverride? manualOverride = null)
    {
        var metadata = await ResolveBrowserMusicMetadataAsync(
            configuration,
            preserveUserEdits: true,
            knownSnapshot,
            cancellationToken: cancellationToken);
        if (manualOverride is not null)
            ApplyManualBrowserMusicOverride(manualOverride);

        var query = BuildBrowserMusicLyricsQuery(configuration, metadata);
        var source = await musicSubtitleSource.LoadAsync(query, message => StatusMessage = message, cancellationToken);
        if (source.Kind == BrowserSubtitleSourceKind.AsrStreaming)
            return false;

        isUpdatingBrowserMusicFields = true;
        try
        {
            if (!hasManualBrowserMusicSongTitle || string.IsNullOrWhiteSpace(BrowserMusicSongTitle))
                BrowserMusicSongTitle = source.Title;
            if (!hasManualBrowserMusicArtist || string.IsNullOrWhiteSpace(BrowserMusicArtist))
                BrowserMusicArtist = source.Artist;
        }
        finally
        {
            isUpdatingBrowserMusicFields = false;
        }

        await SendTimelineAsync(source.Cues, source.SourceName, configuration, cancellationToken);
        return true;
    }

    private async Task CaptureBilibiliCcAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        var source = await videoSubtitleSource.LoadAsync(configuration, message => StatusMessage = message, cancellationToken);
        if (source.Kind == BrowserSubtitleSourceKind.AsrStreaming)
        {
            await CaptureBilibiliAsrStreamingAsync(configuration, cancellationToken);
            return;
        }

        await SendTimelineAsync(source.Cues, source.SourceName, configuration, cancellationToken);
    }

    private async Task CaptureBilibiliAsrStreamingAsync(BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<SubtitleCue>>();
        var progress = new Progress<string>(message => StatusMessage = message);
        using var streamCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamToken = streamCancellationSource.Token;
        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in bilibiliAsrSubtitleCapture.StreamCuesAsync(configuration, progress, streamToken))
                    await channel.Writer.WriteAsync(chunk, streamToken);

                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var cues = new List<SubtitleCue>();
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            DrainStreamingCueChunks(cues, channel.Reader, "ASR");
            if (cues.Count > 0)
                break;
        }

        if (cues.Count == 0)
        {
            await producerTask;
            StatusMessage = "ASR 流式未生成字幕";
            return;
        }

        SetLoadedCues(cues, "ASR");
        try
        {
            if (await TrySendBrowserSyncedStreamingTimelineAsync(cues, channel.Reader, producerTask, streamCancellationSource, configuration, cancellationToken))
                return;

            StatusMessage = $"ASR 流式已加载 {cues.Count} 条字幕，本地时间线开始发送";
            await SendLocalStreamingTimelineAsync(cues, channel.Reader, producerTask, "ASR", cancellationToken);
        }
        finally
        {
            streamCancellationSource.Cancel();
            await CompleteProducerSilentlyAsync(producerTask);
        }
    }

    private async Task SendTimelineAsync(IReadOnlyList<SubtitleCue> cues, string sourceName, BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        SetLoadedCues(cues, sourceName);
        if (await TrySendBrowserSyncedTimelineAsync(cues, sourceName, configuration, cancellationToken))
            return;

        StatusMessage = $"已加载 {cues.Count} 条{sourceName}字幕，可拖动进度条调整继续播放位置";
        await SendLocalTimelineAsync(cues, sourceName, cancellationToken);
    }

    private async Task SendLocalTimelineAsync(IReadOnlyList<SubtitleCue> cues, string sourceName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var index = Math.Clamp(nextTimelineCueIndex, 0, cues.Count);
            if (index >= cues.Count)
                return;

            var cue = cues[index];
            SetManualSubtitleIndex(index);
            var translated = await TranslateIfNeededAsync(cue.Text, cancellationToken);
            var sendStarted = Stopwatch.GetTimestamp();
            await SendSubtitleAsync(cue.Text, translated, cancellationToken, cue.End - cue.Start);
            var sendElapsed = Stopwatch.GetElapsedTime(sendStarted);

            nextTimelineCueIndex = index + 1;
            var versionAtSend = timelineCursorVersion;
            StatusMessage = $"已发送{sourceName}字幕：{CapturedText}";

            if (nextTimelineCueIndex >= cues.Count)
                return;

            var delay = cues[nextTimelineCueIndex].Start - cue.Start - sendElapsed;
            if (delay <= TimeSpan.Zero)
                delay = TimeSpan.FromMilliseconds(500);

            await DelayUntilNextCueOrCursorChangeAsync(delay, versionAtSend, cancellationToken);
        }
    }

    private async Task SendLocalStreamingTimelineAsync(
        List<SubtitleCue> cues,
        ChannelReader<IReadOnlyList<SubtitleCue>> reader,
        Task producerTask,
        string sourceName,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DrainStreamingCueChunks(cues, reader, sourceName);
            await ThrowIfStreamingReaderFailedAsync(reader);
            var index = Math.Clamp(nextTimelineCueIndex, 0, cues.Count);
            if (index >= cues.Count)
            {
                if (await WaitForMoreStreamingCuesAsync(cues, reader, producerTask, sourceName, cancellationToken))
                    continue;

                return;
            }

            var cue = cues[index];
            SetManualSubtitleIndex(index);
            var translated = await TranslateIfNeededAsync(cue.Text, cancellationToken);
            var sendStarted = Stopwatch.GetTimestamp();
            await SendSubtitleAsync(cue.Text, translated, cancellationToken, cue.End - cue.Start);
            var sendElapsed = Stopwatch.GetElapsedTime(sendStarted);

            nextTimelineCueIndex = index + 1;
            var versionAtSend = timelineCursorVersion;
            StatusMessage = $"已发送{sourceName}流式字幕：{CapturedText}";

            if (nextTimelineCueIndex >= cues.Count)
            {
                await WaitForMoreStreamingCuesAsync(cues, reader, producerTask, sourceName, cancellationToken);
                continue;
            }

            var delay = cues[nextTimelineCueIndex].Start - cue.Start - sendElapsed;
            if (delay <= TimeSpan.Zero)
                delay = TimeSpan.FromMilliseconds(500);

            await DelayUntilNextCueOrCursorChangeAsync(delay, versionAtSend, cancellationToken);
        }
    }

    private async Task DelayUntilNextCueOrCursorChangeAsync(TimeSpan delay, int versionAtStart, CancellationToken cancellationToken)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero && timelineCursorVersion == versionAtStart)
        {
            var slice = remaining > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : remaining;
            await Task.Delay(slice, cancellationToken);
            remaining -= slice;
        }
    }

    private async Task<bool> TrySendBrowserSyncedTimelineAsync(IReadOnlyList<SubtitleCue> cues, string sourceName, BrowserTranslationConfiguration configuration, CancellationToken cancellationToken)
    {
        if (cues.Count == 0 || string.IsNullOrWhiteSpace(configuration.BilibiliVideoUrl))
            return false;

        var shouldApplyMusicLyricsOffset = activeCaptureMode == BrowserSubtitleCaptureMode.BilibiliMusic
                                           || sourceName.Contains("音乐", StringComparison.OrdinalIgnoreCase);
        var result = await browserTimelineSynchronizer.SyncAsync(new BrowserTimelineSyncRequest
        {
            BrowserProcessName = configuration.BrowserProcessName,
            TargetVideoUrl = configuration.BilibiliVideoUrl,
            SourceName = sourceName,
            Cues = cues,
            TimelineOffsetProvider = () => shouldApplyMusicLyricsOffset
                ? TimeSpan.FromMilliseconds(BrowserMusicLyricsSyncOffsetMilliseconds)
                : TimeSpan.Zero,
            TimelineOffsetVersionProvider = () => shouldApplyMusicLyricsOffset
                ? browserMusicLyricsSyncOffsetVersion
                : 0,
            SetCueIndex = index =>
            {
                if (Math.Abs(ManualSubtitleIndex - index) > 0.1)
                    SetManualSubtitleIndex(index);
            },
            SetNextCueIndex = index => nextTimelineCueIndex = index,
            ReportStatus = message => StatusMessage = message,
            SendCueAsync = async (cue, token) =>
            {
                var translated = await TranslateIfNeededAsync(cue.Text, token);
                await SendSubtitleAsync(cue.Text, translated, token, cue.End - cue.Start);
                return CapturedText;
            }
        }, cancellationToken);

        return await HandleTimelineSyncResultAsync(result, cancellationToken);
    }

    private async Task<bool> HandleTimelineSyncResultAsync(BrowserTimelineSyncResult result, CancellationToken cancellationToken)
    {
        switch (result.Kind)
        {
            case BrowserTimelineSyncResultKind.NotStarted:
                return false;

            case BrowserTimelineSyncResultKind.TargetChanged:
                if (result.Snapshot is not null)
                    await SwitchToBrowserVideoAsync(result.Snapshot, cancellationToken);
                return true;

            case BrowserTimelineSyncResultKind.BrowserClosed:
                StatusMessage = "Chrome CDP Bilibili video tab was closed; restoring default scene";
                StopCapture(returnToDefaultScene: true, updateStatus: false);
                await RestoreCurrentSceneAfterDelayAsync(CancellationToken.None);
                StatusMessage = "Chrome CDP Bilibili video tab was closed; default scene restore requested";
                return true;

            default:
                return true;
        }
    }

    private async Task<bool> TrySendBrowserSyncedStreamingTimelineAsync(
        List<SubtitleCue> cues,
        ChannelReader<IReadOnlyList<SubtitleCue>> reader,
        Task producerTask,
        CancellationTokenSource streamCancellationSource,
        BrowserTranslationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (cues.Count == 0 || string.IsNullOrWhiteSpace(configuration.BilibiliVideoUrl))
            return false;

        var targetBvid = ExtractBvid(configuration.BilibiliVideoUrl);
        if (string.IsNullOrWhiteSpace(targetBvid))
            return false;

        while (!cancellationToken.IsCancellationRequested)
        {
            DrainStreamingCueChunks(cues, reader, "ASR");
            await ThrowIfStreamingReaderFailedAsync(reader);
            var initialProbe = await browserPlaybackStateReader.ProbeCurrentBilibiliSnapshotAsync(configuration.BrowserProcessName, targetBvid, cancellationToken);
            var initialSnapshot = initialProbe.Snapshot;
            if (initialSnapshot is not null
                && !BrowserTimelineSynchronizer.IsMatchingBilibiliSnapshot(initialSnapshot, targetBvid)
                && !string.IsNullOrWhiteSpace(ExtractBvid(initialSnapshot.Url)))
            {
                streamCancellationSource.Cancel();
                await CompleteProducerSilentlyAsync(producerTask);
                await SwitchToBrowserVideoAsync(initialSnapshot, cancellationToken);
                return true;
            }

            if (initialSnapshot is not null
                && BrowserTimelineSynchronizer.IsMatchingBilibiliSnapshot(initialSnapshot, targetBvid)
                && initialSnapshot.HasReliablePosition)
            {
                break;
            }

            StatusMessage = $"ASR 流式已加载 {cues.Count} 条字幕，正在等待浏览器播放进度（{initialProbe.Message}）";
            await Task.Delay(300, cancellationToken);
        }

        StatusMessage = $"ASR 流式已加载 {cues.Count} 条字幕，正在跟随浏览器进度";
        var lastSentIndex = -1;
        TimeSpan? lastBrowserPosition = null;
        var missingBrowserVideoReads = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            DrainStreamingCueChunks(cues, reader, "ASR");
            await ThrowIfStreamingReaderFailedAsync(reader);
            var probe = await browserPlaybackStateReader.ProbeCurrentBilibiliSnapshotAsync(configuration.BrowserProcessName, targetBvid, cancellationToken);
            if (!probe.HasDevToolsConnection || !probe.HasBilibiliDevToolsTab)
            {
                missingBrowserVideoReads++;
                if (missingBrowserVideoReads >= MissingBrowserVideoStopThreshold)
                {
                    StatusMessage = "Chrome CDP Bilibili video tab was closed; restoring default scene";
                    StopCapture(returnToDefaultScene: true, updateStatus: false);
                    await RestoreCurrentSceneAfterDelayAsync(CancellationToken.None);
                    StatusMessage = "Chrome CDP Bilibili video tab was closed; default scene restore requested";
                    return true;
                }
            }
            else
            {
                missingBrowserVideoReads = 0;
            }

            var snapshot = probe.Snapshot;
            if (snapshot is not null && !BrowserTimelineSynchronizer.IsMatchingBilibiliSnapshot(snapshot, targetBvid))
            {
                if (!string.IsNullOrWhiteSpace(ExtractBvid(snapshot.Url)))
                {
                    streamCancellationSource.Cancel();
                    await CompleteProducerSilentlyAsync(producerTask);
                    await SwitchToBrowserVideoAsync(snapshot, cancellationToken);
                    return true;
                }

                StatusMessage = $"浏览器播放进度读取中断：{probe.Message}";
                await Task.Delay(500, cancellationToken);
                continue;
            }

            if (snapshot?.Position is null)
            {
                StatusMessage = $"浏览器播放进度暂不可用：{probe.Message}";
                await Task.Delay(500, cancellationToken);
                continue;
            }

            if (snapshot.IsPaused)
            {
                StatusMessage = $"浏览器视频暂停：{FormatTime(snapshot.Position.Value)}";
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var position = snapshot.Position.Value;
            if (lastBrowserPosition is { } previousPosition
                && position + TimeSpan.FromSeconds(1.5) < previousPosition)
            {
                lastSentIndex = -1;
                StatusMessage = "检测到浏览器播放进度回退，流式字幕游标已重置";
            }

            lastBrowserPosition = position;
            var cueIndex = BrowserTimelineSynchronizer.FindCueIndexForBrowserPosition(cues, position);
            if (cueIndex >= 0)
            {
                nextTimelineCueIndex = Math.Min(cueIndex + 1, cues.Count);
                if (Math.Abs(ManualSubtitleIndex - cueIndex) > 0.1)
                    SetManualSubtitleIndex(cueIndex);
            }

            if (cueIndex >= 0 && cueIndex != lastSentIndex)
            {
                var cue = cues[cueIndex];
                var translated = await TranslateIfNeededAsync(cue.Text, cancellationToken);
                await SendSubtitleAsync(cue.Text, translated, cancellationToken, cue.End - cue.Start);
                lastSentIndex = cueIndex;
                StatusMessage = $"已按浏览器进度发送 ASR 流式字幕：{CapturedText}";
            }
            else if (cueIndex < 0 && cues.Count > 0 && position > cues[^1].End && !producerTask.IsCompleted)
            {
                StatusMessage = $"ASR 正在准备 {FormatTime(position)} 附近的后续字幕";
                await WaitForMoreStreamingCuesAsync(cues, reader, producerTask, "ASR", cancellationToken, TimeSpan.FromSeconds(1));
                continue;
            }

            if (cues.Count > 0 && position > cues[^1].End + TimeSpan.FromSeconds(2) && producerTask.IsCompleted)
            {
                await ThrowIfStreamingReaderFailedAsync(reader);
                StatusMessage = "浏览器进度已到达当前识别字幕末尾，正在等待跳转、循环或下一段识别结果";
                await Task.Delay(500, cancellationToken);
                continue;
            }

            await Task.Delay(300, cancellationToken);
        }

        return true;
    }

    private static bool HasDifferentBrowserMusicMetadata(BrowserMusicTrackMetadata currentMetadata, BrowserMusicTrackMetadata nextMetadata)
    {
        if (HasDifferentNonEmptyValue(currentMetadata.PlatformTrackId, nextMetadata.PlatformTrackId))
            return true;

        var currentBvid = ExtractBvid(currentMetadata.SourceUrl);
        var nextBvid = ExtractBvid(nextMetadata.SourceUrl);
        if (HasDifferentNonEmptyValue(currentBvid, nextBvid))
            return true;

        if (string.IsNullOrWhiteSpace(currentBvid)
            && string.IsNullOrWhiteSpace(nextBvid)
            && HasDifferentNonEmptyValue(currentMetadata.SourceUrl, nextMetadata.SourceUrl))
        {
            return true;
        }

        if (HasDifferentNonEmptyValue(
                NormalizeBrowserMusicIdentity(currentMetadata.Title),
                NormalizeBrowserMusicIdentity(nextMetadata.Title)))
        {
            return true;
        }

        if (HasDifferentNonEmptyValue(
                NormalizeBrowserMusicIdentity(currentMetadata.Artist),
                NormalizeBrowserMusicIdentity(nextMetadata.Artist)))
        {
            return true;
        }

        if (HasDifferentNonEmptyValue(
                NormalizeBrowserMusicIdentity(currentMetadata.Album),
                NormalizeBrowserMusicIdentity(nextMetadata.Album)))
        {
            return true;
        }

        return HasDifferentNonEmptyValue(
            NormalizeBrowserMusicIdentity(currentMetadata.SourceTitle),
            NormalizeBrowserMusicIdentity(nextMetadata.SourceTitle));
    }

    private static bool HasDifferentNonEmptyValue(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left)
               && !string.IsNullOrWhiteSpace(right)
               && !left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBrowserMusicIdentity(string value)
    {
        return new string((value ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray())
            .Trim();
    }

    private async Task SwitchToBrowserVideoAsync(BrowserVideoPlaybackSnapshot snapshot, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeBilibiliVideoUrl(snapshot.Url);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
            return;

        SetBilibiliVideoUrlFromBrowser(normalizedUrl);
        loadedCues = [];
        loadedSourceName = string.Empty;
        nextTimelineCueIndex = 0;
        timelineCursorVersion++;
        ManualSubtitleMaximum = 0;
        SetManualSubtitleIndex(0);
        IsManualSubtitleSeekEnabled = false;
        ManualSubtitlePositionText = "正在切换字幕";
        StatusMessage = $"检测到浏览器切换视频，正在加载新字幕：{snapshot.Title}";

        var configuration = BuildConfiguration();
        if (activeCaptureMode == BrowserSubtitleCaptureMode.BilibiliMusic)
            await CaptureBilibiliMusicLyricsOrAsrAsync(configuration, snapshot, cancellationToken);
        else
            await CaptureBilibiliCcAsync(configuration, cancellationToken);
    }

    private void DrainStreamingCueChunks(List<SubtitleCue> cues, ChannelReader<IReadOnlyList<SubtitleCue>> reader, string sourceName)
    {
        var appended = false;
        while (reader.TryRead(out var chunk))
        {
            if (chunk.Count == 0)
                continue;

            cues.AddRange(chunk);
            appended = true;
        }

        if (!appended)
            return;

        loadedCues = cues;
        loadedSourceName = sourceName;
        ManualSubtitleMaximum = Math.Max(0, cues.Count - 1);
        IsManualSubtitleSeekEnabled = cues.Count > 0;
        UpdateManualSubtitlePositionText();
        StatusMessage = $"{sourceName} 流式已加载 {cues.Count} 条字幕";
    }

    private async Task<bool> WaitForMoreStreamingCuesAsync(
        List<SubtitleCue> cues,
        ChannelReader<IReadOnlyList<SubtitleCue>> reader,
        Task producerTask,
        string sourceName,
        CancellationToken cancellationToken,
        TimeSpan? maxWait = null)
    {
        if (reader.TryPeek(out _))
        {
            DrainStreamingCueChunks(cues, reader, sourceName);
            return true;
        }

        if (producerTask.IsCompleted)
        {
            await ThrowIfStreamingReaderFailedAsync(reader);
            await producerTask;
            DrainStreamingCueChunks(cues, reader, sourceName);
            return false;
        }

        if (maxWait is null)
        {
            if (await reader.WaitToReadAsync(cancellationToken))
            {
                DrainStreamingCueChunks(cues, reader, sourceName);
                return true;
            }

            await producerTask;
            await ThrowIfStreamingReaderFailedAsync(reader);
            return false;
        }

        var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
        var delayTask = Task.Delay(maxWait.Value, cancellationToken);
        var completedTask = await Task.WhenAny(waitTask, delayTask);
        if (completedTask == waitTask && await waitTask)
        {
            DrainStreamingCueChunks(cues, reader, sourceName);
            return true;
        }

        if (producerTask.IsCompleted || reader.Completion.IsCompleted)
            await ThrowIfStreamingReaderFailedAsync(reader);

        return false;
    }

    private static async Task ThrowIfStreamingReaderFailedAsync(ChannelReader<IReadOnlyList<SubtitleCue>> reader)
    {
        if (!reader.Completion.IsCompleted)
            return;

        await reader.Completion;
    }

    private static async Task CompleteProducerSilentlyAsync(Task producerTask)
    {
        try
        {
            await producerTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void SetLoadedCues(IReadOnlyList<SubtitleCue> cues, string sourceName)
    {
        loadedCues = cues;
        loadedSourceName = sourceName;
        ManualSubtitleMaximum = Math.Max(0, cues.Count - 1);
        nextTimelineCueIndex = 0;
        timelineCursorVersion++;
        SetManualSubtitleIndex(0);
        IsManualSubtitleSeekEnabled = cues.Count > 0;
        UpdateManualSubtitlePositionText();
    }

    private void SetManualSubtitleIndex(double value)
    {
        isUpdatingManualSubtitleIndex = true;
        try
        {
            ManualSubtitleIndex = value;
        }
        finally
        {
            isUpdatingManualSubtitleIndex = false;
        }
    }

    private void UpdateManualSubtitlePositionText()
    {
        if (loadedCues.Count == 0)
        {
            ManualSubtitlePositionText = "未加载字幕";
            return;
        }

        var index = GetManualSubtitleIndex();
        var cue = loadedCues[index];
        ManualSubtitlePositionText = $"{index + 1}/{loadedCues.Count}  {FormatTime(cue.Start)} - {FormatTime(cue.End)}";
        OriginalText = cue.Text;
    }

    private void DebounceSendManualSubtitle()
    {
        manualSubtitleSendDebounceSource?.Cancel();
        manualSubtitleSendDebounceSource = new CancellationTokenSource();
        _ = DebounceSendManualSubtitleAsync(manualSubtitleSendDebounceSource.Token);
    }

    private async Task DebounceSendManualSubtitleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            await SendManualSubtitleAtCurrentIndexAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendManualSubtitleAtCurrentIndexAsync(CancellationToken cancellationToken = default)
    {
        if (loadedCues.Count == 0)
        {
            StatusMessage = "尚未加载可手动发送的字幕";
            return;
        }

        var index = GetManualSubtitleIndex();
        var cue = loadedCues[index];
        var translated = await TranslateIfNeededAsync(cue.Text, cancellationToken);
        await SendSubtitleAsync(cue.Text, translated, cancellationToken, cue.End - cue.Start);

        nextTimelineCueIndex = Math.Min(index + 1, loadedCues.Count);
        timelineCursorVersion++;
        StatusMessage = $"已手动发送{loadedSourceName}字幕：{CapturedText}";
    }

    [RelayCommand]
    private async Task SendPreviousSubtitleAsync()
    {
        if (loadedCues.Count == 0)
            return;

        manualSubtitleSendDebounceSource?.Cancel();
        SetManualSubtitleIndex(Math.Max(0, GetManualSubtitleIndex() - 1));
        await SendManualSubtitleAtCurrentIndexAsync();
    }

    [RelayCommand]
    private async Task SendNextSubtitleAsync()
    {
        if (loadedCues.Count == 0)
            return;

        manualSubtitleSendDebounceSource?.Cancel();
        SetManualSubtitleIndex(Math.Min(loadedCues.Count - 1, GetManualSubtitleIndex() + 1));
        await SendManualSubtitleAtCurrentIndexAsync();
    }

    private int GetManualSubtitleIndex()
    {
        if (loadedCues.Count == 0)
            return 0;

        return Math.Clamp((int)Math.Round(ManualSubtitleIndex), 0, loadedCues.Count - 1);
    }

    private static string ExtractBvid(string input)
    {
        return BilibiliVideoUrlHelper.ExtractBvid(input);
    }

    private static string FormatTime(TimeSpan value) => value.ToString(@"mm\:ss");

    [RelayCommand]
    private void StopCapture()
    {
        modeSwitchRestartSource?.Cancel();
        modeSwitchRestartSource = null;
        StopCapture(returnToDefaultScene: true, updateStatus: true);
    }

    private void StopCapture(bool returnToDefaultScene, bool updateStatus = true)
    {
        manualSubtitleSendDebounceSource?.Cancel();
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
        if (returnToDefaultScene)
            _ = RestoreCurrentSceneAsync(CancellationToken.None);

        if (!updateStatus)
            return;
        StatusMessage = "实时字幕捕获已停止";
    }

    private async Task RestoreCurrentSceneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await restoreService.RestoreAsync(displayService, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore default scene failed: {ex.Message}";
        }
    }

    private async Task RestoreCurrentSceneAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await restoreService.RestoreAsync(displayService, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delayed restore default scene failed: {ex.Message}";
        }
    }

    private Task<string> TranslateIfNeededAsync(string original, CancellationToken cancellationToken = default)
    {
        if (GetOutputMode() == BrowserSubtitleOutputMode.Original)
            return Task.FromResult(string.Empty);

        return TranslateWithFallbackAsync(original, cancellationToken);
    }

    private async Task<string> TranslateWithFallbackAsync(string original, CancellationToken cancellationToken)
    {
        var cacheKey = $"{SourceLanguage.Trim()}|{TargetLanguage.Trim()}|{original}";
        if (translationCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var service = CreateTencentTranslationService();
            var translated = await service.TranslateAsync(original, SourceLanguage, TargetLanguage, cancellationToken);
            translationCache[cacheKey] = translated;
            return translated;
        }
        catch (Exception ex)
        {
            StatusMessage = $"腾讯翻译不可用，已回退占位翻译：{ex.Message}";
            var translated = await fallbackTranslationService.TranslateAsync(original, SourceLanguage, TargetLanguage, cancellationToken);
            translationCache[cacheKey] = translated;
            return translated;
        }
    }

    private TencentMachineTranslationService CreateTencentTranslationService()
    {
        var apiKey = !string.IsNullOrWhiteSpace(TencentCloudSecretId) && !string.IsNullOrWhiteSpace(TencentCloudSecretKey)
            ? $"{TencentCloudSecretId.Trim()}:{TencentCloudSecretKey.Trim()}"
            : TranslationApiKey;

        return new TencentMachineTranslationService(TranslationApiEndpoint, apiKey);
    }

    private async Task SendSubtitleAsync(string original, string translated, CancellationToken cancellationToken = default, TimeSpan? displayDuration = null)
    {
        var displayText = BuildDisplayText(original, translated);
        OriginalText = original;
        TranslatedText = translated;
        CapturedText = displayText;

        var segments = SplitDisplayText(displayText);
        var segmentDelay = CalculateSegmentDelay(displayDuration, segments.Count);
        foreach (var segment in segments)
        {
            await displayService.SendTextAsync(new DisplayTextOptions
            {
                Text = segment.Text,
                Source = DisplayContentKind.BrowserTranslation,
                Layout = HaloPixelTextLayout.Center,
                ScrollDirection = TextScrollDirection.None
            }, cancellationToken);

            if (!segment.IsLast)
                await Task.Delay(segmentDelay, cancellationToken);
        }
    }

    private BrowserTranslationConfiguration BuildConfiguration()
    {
        return new BrowserTranslationConfiguration
        {
            BrowserProcessName = BrowserProcessName.Trim(),
            BilibiliVideoUrl = NormalizeBilibiliVideoUrl(BilibiliVideoUrl),
            TranslationApiEndpoint = TranslationApiEndpoint.Trim(),
            TranslationApiKey = TranslationApiKey.Trim(),
            SourceLanguage = SourceLanguage.Trim(),
            TargetLanguage = TargetLanguage.Trim(),
            OutputMode = GetOutputMode(),
            AsrEngine = GetAsrEngine()
        };
    }

    private BrowserSubtitleOutputMode GetOutputMode()
    {
        return Math.Clamp(SelectedOutputModeIndex, 0, 2) switch
        {
            0 => BrowserSubtitleOutputMode.Original,
            2 => BrowserSubtitleOutputMode.Bilingual,
            _ => BrowserSubtitleOutputMode.Translated
        };
    }

    private BrowserSubtitleAsrEngine GetAsrEngine()
    {
        return Math.Clamp(SelectedAsrEngineIndex, 0, 2) switch
        {
            0 => BrowserSubtitleAsrEngine.SenseVoiceSmall,
            2 => BrowserSubtitleAsrEngine.KotobaWhisperJapanese,
            _ => BrowserSubtitleAsrEngine.WhisperLargeV3Turbo
        };
    }

    private string BuildDisplayText(string original, string translated)
    {
        return GetOutputMode() switch
        {
            BrowserSubtitleOutputMode.Original => original,
            BrowserSubtitleOutputMode.Bilingual => $"{original} / {translated}",
            _ => translated
        };
    }

    private static IReadOnlyList<DisplayTextSegment> SplitDisplayText(string text)
    {
        const int centeredDisplayUnits = 32;

        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return [new DisplayTextSegment(string.Empty, false, true)];

        var segments = new List<DisplayTextSegment>();
        var remaining = TrimLeadingDisplayBreaks(text);
        while (!string.IsNullOrWhiteSpace(remaining))
        {
            var segment = TakeDisplaySegment(remaining, centeredDisplayUnits);
            remaining = TrimLeadingDisplayBreaks(remaining[segment.Length..]);
            var cleanSegment = segment.Trim();
            if (cleanSegment.Length == 0)
                continue;

            segments.Add(new DisplayTextSegment(
                cleanSegment,
                false,
                false));
        }

        if (segments.Count == 0)
            return [new DisplayTextSegment(string.Empty, false, true)];

        return segments
            .Select((segment, index) => segment with { IsLast = index == segments.Count - 1 })
            .ToList();
    }

    private static string TrimLeadingDisplayBreaks(string text)
    {
        var index = 0;
        while (index < text.Length && IsSoftBreak(text[index]))
            index++;

        return text[index..].TrimStart();
    }

    private static TimeSpan CalculateSegmentDelay(TimeSpan? displayDuration, int segmentCount)
    {
        if (segmentCount <= 1)
            return TimeSpan.Zero;

        if (displayDuration is null || displayDuration.Value <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(600);

        var interval = TimeSpan.FromTicks(displayDuration.Value.Ticks / segmentCount);
        if (interval < TimeSpan.FromMilliseconds(300))
            return TimeSpan.FromMilliseconds(300);
        if (interval > TimeSpan.FromMilliseconds(1800))
            return TimeSpan.FromMilliseconds(1800);

        return interval;
    }

    private static string TakeDisplaySegment(string text, int maxUnits)
    {
        var units = 0;
        var lastSoftBreak = -1;

        for (var index = 0; index < text.Length; index++)
        {
            var width = GetDisplayUnitWidth(text[index]);
            if (units + width > maxUnits)
            {
                if (lastSoftBreak > 0)
                    return text[..(lastSoftBreak + 1)];

                return text[..index];
            }

            units += width;
            if (IsSoftBreak(text[index]))
                lastSoftBreak = index;
        }

        return text;
    }

    private static bool IsSoftBreak(char value)
    {
        return char.IsWhiteSpace(value)
               || value is '，' or '。' or '、' or '！' or '？' or '；' or '：'
               || value is ',' or '.' or '!' or '?' or ';' or ':'
               || value is ')' or '）' or ']' or '】';
    }

    private static int GetDisplayUnitWidth(string text)
    {
        var width = 0;
        foreach (var value in text)
            width += GetDisplayUnitWidth(value);
        return width;
    }

    private static int GetDisplayUnitWidth(char value)
    {
        if (value <= 0x007f)
            return 1;

        return IsWideDisplayCharacter(value) ? 2 : 1;
    }

    private static bool IsWideDisplayCharacter(char value)
    {
        return value is >= '\u1100' and <= '\u115f'
               || value is >= '\u2e80' and <= '\ua4cf'
               || value is >= '\uac00' and <= '\ud7a3'
               || value is >= '\uf900' and <= '\ufaff'
               || value is >= '\uff00' and <= '\uffef';
    }

    private static string ResolveBrowserProcessName()
    {
        var configured = DisplayFeatureProfile.BrowserProcessName.Trim();
        return string.IsNullOrWhiteSpace(configured) || configured.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            ? DefaultBrowserProcessName
            : configured;
    }

    private static string ResolveChromeExecutablePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "chrome.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string NormalizeBilibiliVideoUrl(string input)
    {
        return BilibiliVideoUrlHelper.NormalizeVideoUrl(input);
    }

    private sealed record DisplayTextSegment(string Text, bool ShouldScroll, bool IsLast);

    private sealed record BrowserCaptureTarget(BrowserTranslationConfiguration Configuration, BrowserVideoPlaybackSnapshot? Snapshot);

    private sealed record BrowserMusicManualOverride(string Title, string Artist);

    public sealed record LanguageOption(string Value, string Name);

    private enum BrowserSubtitleCaptureMode
    {
        VideoSubtitle,
        BilibiliMusic
    }
}

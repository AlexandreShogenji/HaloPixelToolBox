using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Lyrics;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Diagnostics;
using System.Globalization;
using Windows.Storage.Pickers;

namespace HaloPixelToolBox.ViewModels;

public partial class LyricsSubtitleToolPageViewModel : ViewModelBase
{
    private readonly SpotifyMediaSessionPlaybackProvider spotifyPlaybackProvider = new();
    private readonly LyricsProviderRegistry providerRegistry;
    private readonly HaloPixelDisplayService displayService = new();
    private readonly PersonalSceneRestoreService restoreService = new();
    private readonly LyricsTimelineSyncService lyricsTimelineSyncService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly LyricsProviderKind[] providerMapping =
    [
        LyricsProviderKind.NetEaseCloudMusic,
        LyricsProviderKind.QQMusic,
        LyricsProviderKind.Spotify,
        LyricsProviderKind.LocalFile,
        LyricsProviderKind.Custom
    ];

    private LyricsTrack? currentTrack;
    private string lastSentCueKey = string.Empty;
    private string currentSpotifyTrackKey = string.Empty;
    private bool isSeekingPlaybackPosition;
    private bool isSpotifyAutoReloading;
    private bool isProviderReadinessMonitorSyncing;
    private int lastSentDeviceVolume = -1;
    private DateTimeOffset lastAutoSyncAttemptAt;
    private readonly CancellationTokenSource providerReadinessMonitorCancellationTokenSource = new();
    private CancellationTokenSource? liveLineSyncCancellationTokenSource;

    public List<string> ProviderNames { get; } = ["网易云音乐（桌面歌词）", "QQ 音乐（待适配）", "Spotify 当前播放", "本地 LRC", "自定义 Provider"];

    [ObservableProperty]
    private int selectedProviderIndex = Math.Clamp(DisplayFeatureProfile.LyricsProviderIndex, 0, 4);

    public bool IsNetEaseProviderSelected => ResolveSelectedProviderKind() == LyricsProviderKind.NetEaseCloudMusic;
    public bool IsQQMusicProviderSelected => ResolveSelectedProviderKind() == LyricsProviderKind.QQMusic;
    public bool IsSpotifyProviderSelected => ResolveSelectedProviderKind() == LyricsProviderKind.Spotify;
    public bool IsLocalFileProviderSelected => ResolveSelectedProviderKind() == LyricsProviderKind.LocalFile;
    public bool IsCustomProviderSelected => ResolveSelectedProviderKind() == LyricsProviderKind.Custom;

    [ObservableProperty]
    private string netEaseProviderStatus = "网易云 检测中";

    [ObservableProperty]
    private string qqMusicProviderStatus = "QQ 检测中";

    [ObservableProperty]
    private string spotifyProviderStatus = "Spotify 检测中";

    [ObservableProperty]
    private string localFileProviderStatus = "本地 可选择";

    [ObservableProperty]
    private string customProviderStatus = "自定义 待适配";

    [ObservableProperty]
    private string keyword = string.Empty;

    [ObservableProperty]
    private string localLyricsFilePath = string.Empty;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private string playbackPositionText = "00:00.000";

    [ObservableProperty]
    private double playbackDurationSeconds = 1;

    [ObservableProperty]
    private double offsetSeconds;

    [ObservableProperty]
    private bool enableScroll = true;

    [ObservableProperty]
    private bool enableLyricsSync = true;

    [ObservableProperty]
    private double subtitleSpeakerVolume = 14;

    public string SubtitleSpeakerVolumeText => $"音量：{Math.Clamp((int)Math.Round(SubtitleSpeakerVolume), 0, 16)}/16";

    [ObservableProperty]
    private string volumeStatusMessage = "字幕音箱音量范围 0-16";

    [ObservableProperty]
    private string previewText = "尚未加载歌词";

    [ObservableProperty]
    private string currentLyricLine = "等待同步歌词";

    [ObservableProperty]
    private string statusMessage = "本地 LRC 已可加载，平台接口将按计划继续适配";

    [ObservableProperty]
    private bool isLyricsSyncRunning;

    public LyricsSubtitleToolPageViewModel()
    {
        providerRegistry = new LyricsProviderRegistry(spotifyPlaybackProvider);
        lyricsTimelineSyncService = new LyricsTimelineSyncService(displayService);
        lyricsTimelineSyncService.PositionChanged += (_, position) => RunOnUiThread(() =>
        {
            if (!isSeekingPlaybackPosition)
                SetPlaybackPositionFromSeconds(position.TotalSeconds);
        });
        lyricsTimelineSyncService.CueSent += (_, cue) => RunOnUiThread(() =>
        {
            lastSentCueKey = BuildCueKey(cue);
            CurrentLyricLine = cue.Text;
            StatusMessage = $"已同步歌词：{cue.Text}";
        });
        lyricsTimelineSyncService.StatusChanged += (_, message) => RunOnUiThread(() =>
        {
            StatusMessage = message;
            IsLyricsSyncRunning = lyricsTimelineSyncService.IsRunning;
        });
        lyricsTimelineSyncService.ExternalTrackChanged += (_, snapshot) => RunOnUiThread(() =>
        {
            _ = AutoReloadSpotifyTrackAsync(snapshot);
        });

        _ = RunProviderReadinessMonitorAsync(providerReadinessMonitorCancellationTokenSource.Token);
    }

    partial void OnSelectedProviderIndexChanged(int value)
    {
        DisplayFeatureProfile.LyricsProviderIndex = Math.Clamp(value, 0, providerMapping.Length - 1);
        NotifyProviderSelectionChanged();
        ResetLoadedLyricsState();
        var providerKind = ResolveSelectedProviderKind();
        if (providerKind is not LyricsProviderKind.LocalFile and not LyricsProviderKind.Spotify)
            LocalLyricsFilePath = string.Empty;

        StatusMessage = providerKind switch
        {
            LyricsProviderKind.NetEaseCloudMusic => "已切换到网易云桌面歌词，请确认网易云已开启桌面歌词并点重新加载",
            LyricsProviderKind.Spotify => "已切换到 Spotify 当前播放，可直接重新加载自动匹配歌词，也可先选择本地 LRC",
            LyricsProviderKind.LocalFile => "已切换到本地 LRC，请选择并加载歌词文件",
            _ => "该歌词来源尚未完成适配"
        };

        if (EnableLyricsSync)
            _ = TryStartSyncForReadyProviderAsync(force: true);
    }

    partial void OnEnableLyricsSyncChanged(bool value)
    {
        if (value)
            _ = ResumeLyricsSyncAsync();
        else
            _ = StopLyricsSyncAndRestoreSceneAsync();
    }

    partial void OnSubtitleSpeakerVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(SubtitleSpeakerVolumeText));
        _ = SetSubtitleSpeakerVolumeAsync(value);
    }

    [RelayCommand]
    private void SelectNetEaseProvider() => SelectProvider(LyricsProviderKind.NetEaseCloudMusic);

    [RelayCommand]
    private void SelectQQMusicProvider() => SelectProvider(LyricsProviderKind.QQMusic);

    [RelayCommand]
    private void SelectSpotifyProvider() => SelectProvider(LyricsProviderKind.Spotify);

    [RelayCommand]
    private void SelectLocalFileProvider() => SelectProvider(LyricsProviderKind.LocalFile);

    [RelayCommand]
    private void SelectCustomProvider() => SelectProvider(LyricsProviderKind.Custom);

    [RelayCommand]
    private async Task SelectLocalLyricsFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };

        foreach (var extension in new[] { ".lrc", ".txt" })
            picker.FileTypeFilter.Add(extension);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        LocalLyricsFilePath = file.Path;
        if (ResolveSelectedProviderKind() != LyricsProviderKind.Spotify)
            SelectedProviderIndex = Array.IndexOf(providerMapping, LyricsProviderKind.LocalFile);
        if (string.IsNullOrWhiteSpace(Keyword))
            Keyword = Path.GetFileNameWithoutExtension(file.Path);

        StatusMessage = $"已选择歌词文件，正在自动加载：{Path.GetFileName(file.Path)}";
        await LoadLyricsAsync();
    }

    [RelayCommand]
    private Task LoadLyricsAsync()
    {
        return LoadLyricsCoreAsync(EnableLyricsSync);
    }

    private async Task LoadLyricsCoreAsync(bool startSyncAfterLoad)
    {
        try
        {
            StopLyricsSync();
            currentTrack = null;
            lastSentCueKey = string.Empty;
            PreviewText = "尚未加载歌词";
            CurrentLyricLine = "正在加载歌词";
            PlaybackDurationSeconds = 1;
            SetPlaybackPositionFromSeconds(0);

            var providerKind = ResolveSelectedProviderKind();
            await AttachSpotifySnapshotToQueryStateAsync(providerKind);
            var provider = providerRegistry.GetProvider(providerKind);
            currentTrack = await provider.SearchAsync(BuildLyricsQuery(providerKind));
            lastSentCueKey = string.Empty;

            PreviewText = currentTrack is null
                ? "未获取到歌词"
                : BuildPreviewText(currentTrack);
            CurrentLyricLine = currentTrack is null
                ? "未获取到歌词"
                : "歌词已加载，等待同步";
            PlaybackDurationSeconds = Math.Max(1, currentTrack?.Duration?.TotalSeconds ?? currentTrack?.Lines.LastOrDefault()?.End.TotalSeconds ?? 1);
            SetPlaybackPositionFromSeconds(currentTrack?.CurrentPosition?.TotalSeconds ?? PositionSeconds);
            StatusMessage = currentTrack is null
                ? "未获取到歌词"
                : $"已加载 {currentTrack.Lines.Count} 行歌词：{BuildTrackName(currentTrack)}（{currentTrack.SourceName}）";

            if (startSyncAfterLoad
                && EnableLyricsSync
                && (currentTrack is not null || provider is ILyricsLiveLineProvider))
            {
                await StartLyricsSyncAsync();
            }
        }
        catch (Exception ex)
        {
            currentTrack = null;
            lastSentCueKey = string.Empty;
            PreviewText = $"歌词加载失败：{ex.Message}";
            CurrentLyricLine = "歌词加载失败";
            StatusMessage = $"歌词加载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SendCurrentLineAsync()
    {
        var providerKind = ResolveSelectedProviderKind();
        var provider = providerRegistry.GetProvider(providerKind);
        if (provider is ILyricsLiveLineProvider liveLineProvider)
        {
            await SendLiveCurrentLineAsync(liveLineProvider);
            return;
        }

        if (currentTrack is null)
            await LoadLyricsCoreAsync(false);

        var position = await ResolveCurrentSendPositionAsync(providerKind);
        if (position is null)
        {
            return;
        }

        SetPlaybackPositionFromSeconds(position.Value.TotalSeconds);
        var offset = SafeTimeSpanFromSeconds(OffsetSeconds);
        var cue = currentTrack is null
            ? null
            : GetCurrentCue(currentTrack, position.Value, offset)
            ?? GetNearestPreviousCue(currentTrack, position.Value, offset)
            ?? currentTrack?.Lines.FirstOrDefault();
        if (cue is null)
        {
            StatusMessage = "没有可发送的歌词";
            return;
        }

        var cueKey = BuildCueKey(cue);
        if (cueKey == lastSentCueKey)
        {
            StatusMessage = $"当前歌词已发送：{cue.Text}";
            return;
        }

        await displayService.SendSubtitleCueAsync(cue, new DisplayTextOptions
        {
            Source = DisplayContentKind.Lyrics,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = ResolveScrollDirection(cue.Text)
        });
        lastSentCueKey = cueKey;
        CurrentLyricLine = cue.Text;
        StatusMessage = $"已发送歌词：{cue.Text}";
    }

    [RelayCommand]
    private async Task StartLyricsSyncAsync()
    {
        var providerKind = ResolveSelectedProviderKind();
        if (providerKind == LyricsProviderKind.Spotify)
        {
            await StartSpotifyLyricsSyncAsync();
            return;
        }

        var provider = providerRegistry.GetProvider(providerKind);
        if (provider is ILyricsLiveLineProvider liveLineProvider)
        {
            StartLiveLineSync(liveLineProvider);
            return;
        }

        if (currentTrack is null)
            await LoadLyricsCoreAsync(false);

        if (currentTrack is null || currentTrack.Lines.Count == 0)
        {
            StatusMessage = "请先加载歌词";
            return;
        }

        if (!TryParseTimeCode(PlaybackPositionText, out var position))
        {
            StatusMessage = "播放进度格式不正确，请输入类似 00:42.550 的时间";
            return;
        }

        SetPlaybackPositionFromSeconds(position.TotalSeconds);
        lastSentCueKey = string.Empty;
        IsLyricsSyncRunning = true;
        lyricsTimelineSyncService.Start(
            currentTrack,
            position,
            SafeTimeSpanFromSeconds(OffsetSeconds),
            cue => BuildDisplayOptions(cue.Text));
    }

    private async Task StartSpotifyLyricsSyncAsync()
    {
        if (currentTrack is null)
            await LoadLyricsCoreAsync(false);

        if (currentTrack is null || currentTrack.Lines.Count == 0)
        {
            StatusMessage = string.IsNullOrWhiteSpace(LocalLyricsFilePath)
                ? "请先重新加载 Spotify 歌词；自动匹配失败时可选择本地 LRC"
                : "请先加载歌词";
            return;
        }

        var snapshot = await spotifyPlaybackProvider.GetSnapshotAsync();
        if (!snapshot.HasTrack)
        {
            StatusMessage = "未检测到 Spotify 当前播放歌曲，请先打开 Spotify 并播放音乐";
            return;
        }

        currentSpotifyTrackKey = BuildSpotifyTrackKey(snapshot);
        if (snapshot.Position is { } position)
            SetPlaybackPositionFromSeconds(position.TotalSeconds);

        lastSentCueKey = string.Empty;
        IsLyricsSyncRunning = true;
        lyricsTimelineSyncService.StartExternal(
            currentTrack,
            spotifyPlaybackProvider.GetSnapshotAsync,
            IsExpectedSpotifyTrack,
            SafeTimeSpanFromSeconds(OffsetSeconds),
            cue => BuildDisplayOptions(cue.Text));
    }

    private async Task AutoReloadSpotifyTrackAsync(LyricsPlaybackSnapshot snapshot)
    {
        if (isSpotifyAutoReloading || ResolveSelectedProviderKind() != LyricsProviderKind.Spotify)
            return;

        isSpotifyAutoReloading = true;
        try
        {
            var trackName = BuildSpotifyKeyword(snapshot);
            StatusMessage = $"检测到 Spotify 切歌：{trackName}，正在自动匹配歌词";
            LocalLyricsFilePath = string.Empty;
            await LoadLyricsCoreAsync(false);
            if (currentTrack is null || currentTrack.Lines.Count == 0)
            {
                StatusMessage = $"未获取到 Spotify 当前歌曲歌词：{Keyword}。可选择本地 LRC 作为保底";
                return;
            }

            if (EnableLyricsSync)
                await StartSpotifyLyricsSyncAsync();
        }
        finally
        {
            isSpotifyAutoReloading = false;
        }
    }

    [RelayCommand]
    private void StopLyricsSync() 
    {
        if (liveLineSyncCancellationTokenSource is not null)
        {
            liveLineSyncCancellationTokenSource.Cancel();
            liveLineSyncCancellationTokenSource.Dispose();
            liveLineSyncCancellationTokenSource = null;
        }

        lyricsTimelineSyncService.Stop();
        IsLyricsSyncRunning = false;
    }

    private async Task StopLyricsSyncAndRestoreSceneAsync()
    {
        StopLyricsSync();
        await RestoreCurrentSceneAsync(CancellationToken.None);
    }

    private async Task ResumeLyricsSyncAsync()
    {
        await TryStartSyncForReadyProviderAsync(force: true);
    }

    private async Task RestoreCurrentSceneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var restored = await restoreService.RestoreAsync(displayService, cancellationToken);
            StatusMessage = restored
                ? "歌词同步已关闭，已返回当前默认个性场景"
                : "歌词同步已关闭，未找到可恢复的默认个性场景";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"歌词同步已关闭，返回默认个性场景失败：{ex.Message}";
        }
    }


    private async Task SetSubtitleSpeakerVolumeAsync(double value)
    {
        var volume = Math.Clamp((int)Math.Round(value), 0, 16);
        if (volume == lastSentDeviceVolume)
            return;

        lastSentDeviceVolume = volume;
        var sent = await displayService.SetDeviceVolumeAsync(volume);
        VolumeStatusMessage = sent
            ? $"字幕音箱音量已设置为 {volume}/16"
            : "未检测到字幕音箱，请确认设备已连接";
    }

    private async Task RunProviderReadinessMonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshProviderReadinessAsync(cancellationToken);
                if (EnableLyricsSync)
                    await TryStartSyncForReadyProviderAsync(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task RefreshProviderReadinessAsync(CancellationToken cancellationToken)
    {
        var netEase = FindSoftware(["cloudmusic", "CloudMusic", "NeteaseMusic", "网易云音乐"]);
        var qqMusic = FindSoftware(["QQMusic", "qqmusic", "QQMusicSvr"]);
        var spotifyProcess = FindSoftware(["Spotify", "spotify"]);
        LyricsPlaybackSnapshot? spotifySnapshot = null;

        try
        {
            spotifySnapshot = await spotifyPlaybackProvider.GetSnapshotAsync(cancellationToken);
        }
        catch
        {
        }

        RunOnUiThread(() =>
        {
            NetEaseProviderStatus = netEase.IsRunning
                ? $"网易云 就绪{FormatVersionSuffix(netEase.Version)}"
                : "网易云 未运行";
            QqMusicProviderStatus = qqMusic.IsRunning
                ? $"QQ 运行中{FormatVersionSuffix(qqMusic.Version)}（待适配）"
                : "QQ 未运行（待适配）";
            SpotifyProviderStatus = spotifySnapshot?.HasTrack == true
                ? $"Spotify 就绪{FormatVersionSuffix(spotifyProcess.Version)}"
                : spotifyProcess.IsRunning
                    ? $"Spotify 运行中{FormatVersionSuffix(spotifyProcess.Version)}"
                    : "Spotify 未运行";
            LocalFileProviderStatus = File.Exists(LocalLyricsFilePath)
                ? "本地 就绪"
                : "本地 可选择";
            CustomProviderStatus = "自定义 待适配";
        });
    }

    private async Task TryStartSyncForReadyProviderAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!EnableLyricsSync || IsLyricsSyncRunning || isProviderReadinessMonitorSyncing)
            return;

        var now = DateTimeOffset.Now;
        if (!force && now - lastAutoSyncAttemptAt < TimeSpan.FromSeconds(8))
            return;

        var providerKind = ResolveSelectedProviderKind();
        var ready = await IsProviderReadyForAutoSyncAsync(providerKind, cancellationToken);
        if (!ready)
        {
            if (force)
                StatusMessage = "同步开关已开启，等待当前歌词来源就绪";
            return;
        }

        lastAutoSyncAttemptAt = now;
        isProviderReadinessMonitorSyncing = true;
        try
        {
            await StartLyricsSyncAsync();
        }
        finally
        {
            isProviderReadinessMonitorSyncing = false;
        }
    }

    private async Task<bool> IsProviderReadyForAutoSyncAsync(LyricsProviderKind providerKind, CancellationToken cancellationToken)
    {
        switch (providerKind)
        {
            case LyricsProviderKind.NetEaseCloudMusic:
                return FindSoftware(["cloudmusic", "CloudMusic", "NeteaseMusic", "网易云音乐"]).IsRunning;
            case LyricsProviderKind.Spotify:
            {
                var snapshot = await spotifyPlaybackProvider.GetSnapshotAsync(cancellationToken);
                if (!snapshot.HasTrack)
                    return false;

                var snapshotKey = BuildSpotifyTrackKey(snapshot);
                if (!string.IsNullOrWhiteSpace(currentSpotifyTrackKey)
                    && !snapshotKey.Equals(currentSpotifyTrackKey, StringComparison.OrdinalIgnoreCase))
                {
                    currentTrack = null;
                    lastSentCueKey = string.Empty;
                }

                currentSpotifyTrackKey = snapshotKey;
                return true;
            }
            case LyricsProviderKind.LocalFile:
                return File.Exists(LocalLyricsFilePath);
            default:
                return false;
        }
    }

    private static SoftwareStatus FindSoftware(string[] processNames)
    {
        foreach (var processName in processNames)
        {
            var processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        return new SoftwareStatus(true, TryReadVersion(process));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return new SoftwareStatus(false, string.Empty);
    }

    private static string TryReadVersion(Process process)
    {
        try
        {
            var versionInfo = process.MainModule?.FileVersionInfo;
            return versionInfo?.ProductVersion
                ?? versionInfo?.FileVersion
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatVersionSuffix(string version)
    {
        return string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version}";
    }

    private void StartLiveLineSync(ILyricsLiveLineProvider liveLineProvider)
    {
        StopLyricsSync();
        lastSentCueKey = string.Empty;
        IsLyricsSyncRunning = true;
        liveLineSyncCancellationTokenSource = new CancellationTokenSource();
        StatusMessage = "网易云实时歌词同步已启动，正在定位当前桌面歌词";
        _ = RunLiveLineSyncAsync(liveLineProvider, liveLineSyncCancellationTokenSource.Token);
    }

    private async Task RunLiveLineSyncAsync(ILyricsLiveLineProvider liveLineProvider, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var track = await liveLineProvider.ReadCurrentLineAsync(cancellationToken);
                var cue = track?.Lines.FirstOrDefault();
                if (cue is not null && !string.IsNullOrWhiteSpace(cue.Text))
                {
                    var cueKey = BuildCueKey(cue);
                    if (cueKey != lastSentCueKey)
                    {
                        await displayService.SendSubtitleCueAsync(cue, BuildDisplayOptions(cue.Text), cancellationToken);
                        lastSentCueKey = cueKey;
                        RunOnUiThread(() =>
                        {
                            PreviewText = cue.Text;
                            CurrentLyricLine = cue.Text;
                            StatusMessage = $"网易云实时同步：{cue.Text}";
                        });
                    }
                }

                await Task.Delay(120, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                StatusMessage = $"网易云实时同步失败：{ex.Message}";
                PreviewText = $"网易云实时同步失败：{ex.Message}";
                CurrentLyricLine = "网易云实时同步失败";
                IsLyricsSyncRunning = false;
            });
        }
        finally
        {
            RunOnUiThread(() => IsLyricsSyncRunning = liveLineSyncCancellationTokenSource is not null && !liveLineSyncCancellationTokenSource.IsCancellationRequested);
        }
    }

    private async Task SendLiveCurrentLineAsync(ILyricsLiveLineProvider liveLineProvider)
    {
        try
        {
            var track = await liveLineProvider.ReadCurrentLineAsync();
            var cue = track?.Lines.FirstOrDefault();
            if (cue is null || string.IsNullOrWhiteSpace(cue.Text))
            {
                StatusMessage = "没有读取到网易云当前桌面歌词";
                return;
            }

            await displayService.SendSubtitleCueAsync(cue, BuildDisplayOptions(cue.Text));
            lastSentCueKey = BuildCueKey(cue);
            PreviewText = cue.Text;
            CurrentLyricLine = cue.Text;
            StatusMessage = $"已发送网易云当前歌词：{cue.Text}";
        }
        catch (Exception ex)
        {
            PreviewText = $"网易云当前歌词读取失败：{ex.Message}";
            StatusMessage = $"网易云当前歌词读取失败：{ex.Message}";
        }
    }

    private LyricsProviderKind ResolveSelectedProviderKind()
    {
        return providerMapping[Math.Clamp(SelectedProviderIndex, 0, providerMapping.Length - 1)];
    }

    private void SelectProvider(LyricsProviderKind providerKind)
    {
        var index = Array.IndexOf(providerMapping, providerKind);
        if (index >= 0)
            SelectedProviderIndex = index;
    }

    private void NotifyProviderSelectionChanged()
    {
        OnPropertyChanged(nameof(IsNetEaseProviderSelected));
        OnPropertyChanged(nameof(IsQQMusicProviderSelected));
        OnPropertyChanged(nameof(IsSpotifyProviderSelected));
        OnPropertyChanged(nameof(IsLocalFileProviderSelected));
        OnPropertyChanged(nameof(IsCustomProviderSelected));
    }

    private LyricsQuery BuildLyricsQuery(LyricsProviderKind providerKind)
    {
        return new LyricsQuery
        {
            Provider = providerKind,
            Keyword = Keyword,
            Title = Keyword,
            FilePath = providerKind is LyricsProviderKind.LocalFile or LyricsProviderKind.Spotify ? LocalLyricsFilePath : null
        };
    }

    private void ResetLoadedLyricsState()
    {
        StopLyricsSync();
        currentTrack = null;
        lastSentCueKey = string.Empty;
        currentSpotifyTrackKey = string.Empty;
        isSpotifyAutoReloading = false;
        isSeekingPlaybackPosition = false;
        PreviewText = "尚未加载歌词";
        CurrentLyricLine = "等待同步歌词";
        PlaybackDurationSeconds = 1;
        SetPlaybackPositionFromSeconds(0);
    }

    private async Task AttachSpotifySnapshotToQueryStateAsync(LyricsProviderKind providerKind)
    {
        if (providerKind != LyricsProviderKind.Spotify)
            return;

        var snapshot = await spotifyPlaybackProvider.GetSnapshotAsync();
        if (!snapshot.HasTrack)
            return;

        currentSpotifyTrackKey = BuildSpotifyTrackKey(snapshot);
        Keyword = BuildSpotifyKeyword(snapshot);
        if (snapshot.Position is { } position)
            SetPlaybackPositionFromSeconds(position.TotalSeconds);
        if (snapshot.Duration is { } duration)
            PlaybackDurationSeconds = Math.Max(1, duration.TotalSeconds);
    }

    private async Task<TimeSpan?> ResolveCurrentSendPositionAsync(LyricsProviderKind providerKind)
    {
        if (providerKind == LyricsProviderKind.Spotify)
        {
            var snapshot = await spotifyPlaybackProvider.GetSnapshotAsync();
            if (!snapshot.HasTrack)
            {
                StatusMessage = "未检测到 Spotify 当前播放歌曲";
                return null;
            }

            if (!IsExpectedSpotifyTrack(snapshot))
            {
                StatusMessage = "检测到 Spotify 已切歌，请重新加载歌词后继续同步";
                return null;
            }

            if (snapshot.Position is not { } spotifyPosition)
            {
                StatusMessage = "Spotify 播放进度暂不可用";
                return null;
            }

            return spotifyPosition;
        }

        if (!TryParseTimeCode(PlaybackPositionText, out var position))
        {
            StatusMessage = "播放进度格式不正确，请输入类似 00:42.550 的时间";
            return null;
        }

        return position;
    }

    public void PreviewPlaybackPositionFromSeconds(double seconds)
    {
        PositionSeconds = ClampSeconds(seconds);
        PlaybackPositionText = FormatTimeCode(TimeSpan.FromSeconds(PositionSeconds));
    }

    public void BeginPlaybackSeek()
    {
        isSeekingPlaybackPosition = true;
    }

    public async Task CommitPlaybackPositionFromSecondsAsync(double seconds)
    {
        PreviewPlaybackPositionFromSeconds(seconds);
        if (!isSeekingPlaybackPosition)
            return;

        isSeekingPlaybackPosition = false;
        if (IsLyricsSyncRunning)
            await StartLyricsSyncAsync();
        else
            await SendCurrentLineAsync();
    }

    private void SetPlaybackPositionFromSeconds(double seconds)
    {
        PositionSeconds = ClampSeconds(seconds);
        PlaybackPositionText = FormatTimeCode(TimeSpan.FromSeconds(PositionSeconds));
    }

    private double ClampSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return 0;

        return Math.Clamp(seconds, 0, Math.Max(PlaybackDurationSeconds, 1));
    }

    private DisplayTextOptions BuildDisplayOptions(string text)
    {
        return new DisplayTextOptions
        {
            Source = DisplayContentKind.Lyrics,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = ResolveScrollDirection(text)
        };
    }

    private TextScrollDirection ResolveScrollDirection(string text)
    {
        return ShouldScroll(text) ? TextScrollDirection.RightToLeft : TextScrollDirection.None;
    }

    private string BuildCueKey(SubtitleCue cue)
    {
        return $"{cue.Start.Ticks}:{cue.End.Ticks}:{ResolveScrollDirection(cue.Text)}:{cue.Text}";
    }

    private bool ShouldScroll(string text)
    {
        if (!EnableScroll)
            return false;

        var asciiCount = 0;
        var cjkCount = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
                asciiCount++;
            else if (IsCjkCharacter(character))
                cjkCount++;
        }

        return asciiCount >= 32 || cjkCount >= 16;
    }

    private static SubtitleCue? GetCurrentCue(LyricsTrack track, TimeSpan position, TimeSpan offset)
    {
        return track.Lines.FirstOrDefault(line =>
        {
            var start = ClampToZero(line.Start + offset);
            var end = ClampToZero(line.End + offset);
            return start <= position && end >= position;
        });
    }

    private static SubtitleCue? GetNearestPreviousCue(LyricsTrack track, TimeSpan position, TimeSpan offset)
    {
        return track.Lines
            .Where(line => ClampToZero(line.Start + offset) <= position)
            .OrderByDescending(line => line.Start)
            .FirstOrDefault();
    }

    private static string BuildPreviewText(LyricsTrack track)
    {
        var header = BuildTrackName(track);
        var lines = track.Lines.Select(line => $"[{FormatTimeCode(line.Start)}] {line.Text}");
        return string.IsNullOrWhiteSpace(header)
            ? string.Join(Environment.NewLine, lines)
            : $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    private static string BuildTrackName(LyricsTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(track.Title))
            return $"{track.Title} - {track.Artist}";

        if (!string.IsNullOrWhiteSpace(track.Title))
            return track.Title;

        return track.SourceName;
    }

    private bool IsExpectedSpotifyTrack(LyricsPlaybackSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(currentSpotifyTrackKey)
            || BuildSpotifyTrackKey(snapshot).Equals(currentSpotifyTrackKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpotifyTrackKey(LyricsPlaybackSnapshot snapshot)
    {
        return $"{NormalizeTrackText(snapshot.Title)}|{NormalizeTrackText(snapshot.Artist)}|{NormalizeTrackText(snapshot.Album)}";
    }

    private static string BuildSpotifyKeyword(LyricsPlaybackSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Artist) && !string.IsNullOrWhiteSpace(snapshot.Title))
            return $"{snapshot.Title} - {snapshot.Artist}";

        return string.IsNullOrWhiteSpace(snapshot.Title) ? snapshot.Artist : snapshot.Title;
    }

    private static string NormalizeTrackText(string value)
    {
        return new string(value
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            .ToArray())
            .Trim();
    }

    private static string FormatTimeCode(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private static bool TryParseTimeCode(string value, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        value = value.Trim().Replace(',', '.');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var formats = new[]
        {
            @"mm\:ss\.fff",
            @"mm\:ss\.ff",
            @"mm\:ss\.f",
            @"mm\:ss",
            @"hh\:mm\:ss\.fff",
            @"hh\:mm\:ss\.ff",
            @"hh\:mm\:ss\.f",
            @"hh\:mm\:ss"
        };

        if (TimeSpan.TryParseExact(value, formats, CultureInfo.InvariantCulture, out time))
            return time >= TimeSpan.Zero;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            time = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    private static bool IsCjkCharacter(char character)
    {
        return character is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uF900' and <= '\uFAFF';
    }

    private static TimeSpan SafeTimeSpanFromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan ClampToZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private void RunOnUiThread(Action action)
    {
        if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            dispatcherQueue.TryEnqueue(() => action());
        else
            action();
    }

    private sealed record SoftwareStatus(bool IsRunning, string Version);
}

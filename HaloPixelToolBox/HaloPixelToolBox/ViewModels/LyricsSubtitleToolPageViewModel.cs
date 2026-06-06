using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Lyrics;
using System.Globalization;
using Windows.Storage.Pickers;

namespace HaloPixelToolBox.ViewModels;

public partial class LyricsSubtitleToolPageViewModel : ViewModelBase
{
    private readonly LyricsProviderRegistry providerRegistry = new();
    private readonly HaloPixelDisplayService displayService = new();
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
    private bool isSeekingPlaybackPosition;
    private CancellationTokenSource? liveLineSyncCancellationTokenSource;

    public List<string> ProviderNames { get; } = ["网易云音乐（桌面歌词）", "QQ 音乐（待适配）", "Spotify（待适配）", "本地 LRC", "自定义 Provider"];

    [ObservableProperty]
    private int selectedProviderIndex = 3;

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
    private string previewText = "尚未加载歌词";

    [ObservableProperty]
    private string statusMessage = "本地 LRC 已可加载，平台接口将按计划继续适配";

    [ObservableProperty]
    private bool isLyricsSyncRunning;

    public LyricsSubtitleToolPageViewModel()
    {
        lyricsTimelineSyncService = new LyricsTimelineSyncService(displayService);
        lyricsTimelineSyncService.PositionChanged += (_, position) => RunOnUiThread(() =>
        {
            if (!isSeekingPlaybackPosition)
                SetPlaybackPositionFromSeconds(position.TotalSeconds);
        });
        lyricsTimelineSyncService.CueSent += (_, cue) => RunOnUiThread(() =>
        {
            lastSentCueKey = BuildCueKey(cue);
            StatusMessage = $"已同步歌词：{cue.Text}";
        });
        lyricsTimelineSyncService.StatusChanged += (_, message) => RunOnUiThread(() =>
        {
            StatusMessage = message;
            IsLyricsSyncRunning = lyricsTimelineSyncService.IsRunning;
        });
    }

    partial void OnSelectedProviderIndexChanged(int value)
    {
        ResetLoadedLyricsState();
        var providerKind = ResolveSelectedProviderKind();
        if (providerKind != LyricsProviderKind.LocalFile)
            LocalLyricsFilePath = string.Empty;

        StatusMessage = providerKind switch
        {
            LyricsProviderKind.NetEaseCloudMusic => "已切换到网易云桌面歌词，请确认网易云已开启桌面歌词并点重新加载",
            LyricsProviderKind.LocalFile => "已切换到本地 LRC，请选择并加载歌词文件",
            _ => "该歌词来源尚未完成适配"
        };
    }

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
        SelectedProviderIndex = Array.IndexOf(providerMapping, LyricsProviderKind.LocalFile);
        if (string.IsNullOrWhiteSpace(Keyword))
            Keyword = Path.GetFileNameWithoutExtension(file.Path);

        StatusMessage = $"已选择歌词文件，正在自动加载：{Path.GetFileName(file.Path)}";
        await LoadLyricsAsync();
    }

    [RelayCommand]
    private async Task LoadLyricsAsync()
    {
        try
        {
            StopLyricsSync();
            currentTrack = null;
            lastSentCueKey = string.Empty;
            PreviewText = "尚未加载歌词";
            PlaybackDurationSeconds = 1;
            SetPlaybackPositionFromSeconds(0);

            var providerKind = ResolveSelectedProviderKind();
            var provider = providerRegistry.GetProvider(providerKind);
            currentTrack = await provider.SearchAsync(BuildLyricsQuery(providerKind));
            lastSentCueKey = string.Empty;

            PreviewText = currentTrack is null
                ? "未获取到歌词"
                : BuildPreviewText(currentTrack);
            PlaybackDurationSeconds = Math.Max(1, currentTrack?.Duration?.TotalSeconds ?? currentTrack?.Lines.LastOrDefault()?.End.TotalSeconds ?? 1);
            SetPlaybackPositionFromSeconds(currentTrack?.CurrentPosition?.TotalSeconds ?? PositionSeconds);
            StatusMessage = currentTrack is null
                ? "未获取到歌词"
                : $"已加载 {currentTrack.Lines.Count} 行歌词：{BuildTrackName(currentTrack)}（{currentTrack.SourceName}）";
        }
        catch (Exception ex)
        {
            currentTrack = null;
            lastSentCueKey = string.Empty;
            PreviewText = $"歌词加载失败：{ex.Message}";
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
            await LoadLyricsAsync();

        if (!TryParseTimeCode(PlaybackPositionText, out var position))
        {
            StatusMessage = "播放进度格式不正确，请输入类似 00:42.550 的时间";
            return;
        }

        SetPlaybackPositionFromSeconds(position.TotalSeconds);
        var offset = SafeTimeSpanFromSeconds(OffsetSeconds);
        var cue = currentTrack is null
            ? null
            : GetCurrentCue(currentTrack, position, offset)
            ?? GetNearestPreviousCue(currentTrack, position, offset)
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
        StatusMessage = $"已发送歌词：{cue.Text}";
    }

    [RelayCommand]
    private async Task StartLyricsSyncAsync()
    {
        var providerKind = ResolveSelectedProviderKind();
        var provider = providerRegistry.GetProvider(providerKind);
        if (provider is ILyricsLiveLineProvider liveLineProvider)
        {
            StartLiveLineSync(liveLineProvider);
            return;
        }

        if (currentTrack is null)
            await LoadLyricsAsync();

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

    private LyricsQuery BuildLyricsQuery(LyricsProviderKind providerKind)
    {
        return new LyricsQuery
        {
            Provider = providerKind,
            Keyword = Keyword,
            Title = Keyword,
            FilePath = providerKind == LyricsProviderKind.LocalFile ? LocalLyricsFilePath : null
        };
    }

    private void ResetLoadedLyricsState()
    {
        StopLyricsSync();
        currentTrack = null;
        lastSentCueKey = string.Empty;
        isSeekingPlaybackPosition = false;
        PreviewText = "尚未加载歌词";
        PlaybackDurationSeconds = 1;
        SetPlaybackPositionFromSeconds(0);
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
}

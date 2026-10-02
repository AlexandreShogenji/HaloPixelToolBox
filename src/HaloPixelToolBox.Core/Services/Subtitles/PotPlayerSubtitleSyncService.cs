using System.Text;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Scenes;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerSubtitleSyncService
{
    private static readonly TimeSpan DefaultSceneReturnDelay = TimeSpan.FromSeconds(5);

    private readonly HaloPixelDisplayService displayService;
    private readonly PersonalSceneRestoreService restoreService = new();
    private readonly PotPlayerPlaybackStateReader playbackStateReader;
    private readonly SubtitleParserFactory subtitleParserFactory = new();
    private CancellationTokenSource? cancellationTokenSource;
    private string lastSentText = string.Empty;
    private DateTime lastReadWriteTimeUtc;
    private static readonly string[] SubtitleExtensions =
    [
        ".srt",
        ".ass",
        ".ssa",
        ".vtt",
        ".lrc",
        ".sub",
        ".txt"
    ];

    public PotPlayerSubtitleSyncService()
        : this(new HaloPixelDisplayService(), new PotPlayerPlaybackStateReader())
    {
    }

    public PotPlayerSubtitleSyncService(HaloPixelDisplayService displayService, PotPlayerPlaybackStateReader playbackStateReader)
    {
        this.displayService = displayService;
        this.playbackStateReader = playbackStateReader;
    }

    public bool IsRunning => cancellationTokenSource is not null;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<string>? SubtitleSent;

    public event EventHandler<string>? SubtitleSourceResolved;

    public void Start(PotPlayerSubtitleSyncConfiguration configuration)
    {
        Stop(returnToDefaultScene: false, reportStatus: false);
        lastSentText = string.Empty;
        lastReadWriteTimeUtc = default;
        cancellationTokenSource = new CancellationTokenSource();
        _ = SyncAsync(configuration, cancellationTokenSource.Token);
    }

    public void Stop() => Stop(returnToDefaultScene: true);

    private void Stop(bool returnToDefaultScene, bool reportStatus = true)
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
        if (returnToDefaultScene)
            _ = RestoreCurrentSceneAsync(CancellationToken.None);

        if (reportStatus)
            ReportStatus("PotPlayer 字幕同步已停止");
    }

    private async Task SyncAsync(PotPlayerSubtitleSyncConfiguration configuration, CancellationToken cancellationToken)
    {
        var subtitleOutputPath = ResolvePotPlayerSubtitleOutputPath(configuration.SubtitleOutputPath)
            ?? ResolveSubtitleOutputPath(configuration.SubtitleOutputPath);
        if (subtitleOutputPath is null)
        {
            ReportStatus("未找到当前视频同名字幕，请选择字幕文件");
            return;
        }

        SubtitleSourceResolved?.Invoke(this, subtitleOutputPath);

        if (TryLoadTimelineSubtitle(subtitleOutputPath, out var document))
        {
            ReportStatus($"已加载字幕文件：{Path.GetFileName(subtitleOutputPath)}");
            await SyncTimelineSubtitleAsync(document, configuration, cancellationToken);
            return;
        }

        ReportStatus($"PotPlayer 实时字幕文件同步已启动：{Path.GetFileName(subtitleOutputPath)}");
        await TryReadAndSendSubtitleAsync(subtitleOutputPath, configuration, PotPlayerPlaybackState.RunningUnknown, true, cancellationToken);
        var hasRestoredForMissingPotPlayer = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await playbackStateReader.GetSnapshotAsync(cancellationToken);
                var state = snapshot.State;
                if (state == PotPlayerPlaybackState.NotRunning)
                {
                    if (!hasRestoredForMissingPotPlayer)
                    {
                        await RestoreCurrentSceneAsync(cancellationToken);
                        hasRestoredForMissingPotPlayer = true;
                    }
                    ReportStatus("未检测到 PotPlayer 进程");
                }
                else if (state == PotPlayerPlaybackState.Paused)
                {
                    ReportStatus("PotPlayer 已暂停，等待继续播放");
                }
                else
                {
                    hasRestoredForMissingPotPlayer = false;
                    await TryReadAndSendSubtitleAsync(subtitleOutputPath, configuration, state, false, cancellationToken);
                }

                await Task.Delay(configuration.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ReportStatus($"同步异常：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private async Task SyncTimelineSubtitleAsync(SubtitleDocument document, PotPlayerSubtitleSyncConfiguration configuration, CancellationToken cancellationToken)
    {
        var cues = document.Cues
            .Where(cue => !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(cue => cue.Start)
            .ToList();

        if (cues.Count == 0)
        {
            ReportStatus("字幕文件没有可同步的时间轴内容");
            return;
        }

        ReportStatus($"已加载 {cues.Count} 条字幕，等待 PotPlayer 播放");
        var playbackPosition = TimeSpan.Zero;
        var nextCueIndex = 0;
        var lastTick = DateTimeOffset.Now;
        TimeSpan? defaultSceneReturnPosition = null;
        var hasRestoredForMissingPotPlayer = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await playbackStateReader.GetSnapshotAsync(cancellationToken);
            var state = snapshot.State;
            var now = DateTimeOffset.Now;
            var elapsed = now - lastTick;
            lastTick = now;

            if (snapshot.Position is not null)
                playbackPosition = snapshot.Position.Value;

            if (state == PotPlayerPlaybackState.NotRunning)
            {
                if (!hasRestoredForMissingPotPlayer)
                {
                    await RestoreCurrentSceneAsync(cancellationToken);
                    hasRestoredForMissingPotPlayer = true;
                }
                ReportStatus("未检测到 PotPlayer 进程");
                await Task.Delay(configuration.PollInterval, cancellationToken);
                continue;
            }

            hasRestoredForMissingPotPlayer = false;

            if (state == PotPlayerPlaybackState.Paused)
            {
                ReportStatus($"PotPlayer 已暂停，字幕位置 {FormatPosition(playbackPosition)}（{GetPositionSource(snapshot)}）");
                await Task.Delay(configuration.PollInterval, cancellationToken);
                continue;
            }

            if (snapshot.Position is null)
                playbackPosition += elapsed;

            if (defaultSceneReturnPosition is not null && playbackPosition >= defaultSceneReturnPosition.Value)
            {
                await RestoreCurrentSceneAsync(cancellationToken);
                cancellationTokenSource = null;
                ReportStatus("最后一条字幕已停留 5 秒，已恢复当前使用中的个性场景");
                return;
            }

            while (nextCueIndex < cues.Count - 1 && cues[nextCueIndex].End <= playbackPosition)
                nextCueIndex++;

            var cue = cues[nextCueIndex];
            if (cue.Start <= playbackPosition)
            {
                if (cue.Text != lastSentText)
                    await SendSubtitleTextAsync(cue.Text, configuration, cancellationToken);

                if (nextCueIndex == cues.Count - 1 && defaultSceneReturnPosition is null)
                {
                    defaultSceneReturnPosition = playbackPosition + DefaultSceneReturnDelay;
                    ReportStatus("最后一条字幕已发送，5 秒后恢复当前使用中的个性场景");
                }
            }
            else
            {
                ReportStatus($"时间轴同步中 {FormatPosition(playbackPosition)}（{GetPositionSource(snapshot)}） / 下一条 {FormatPosition(cue.Start)}");
            }

            await Task.Delay(configuration.PollInterval, cancellationToken);
        }
    }

    private async Task TryReadAndSendSubtitleAsync(string subtitleOutputPath, PotPlayerSubtitleSyncConfiguration configuration, PotPlayerPlaybackState state, bool forceRead, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(subtitleOutputPath);
        if (!forceRead && fileInfo.LastWriteTimeUtc == lastReadWriteTimeUtc)
        {
            ReportStatus(GetWaitingStatus(state));
            return;
        }

        lastReadWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        var text = NormalizeSubtitleText(await ReadTextWithSharedAccessAsync(subtitleOutputPath, cancellationToken), Path.GetExtension(subtitleOutputPath));
        if (string.IsNullOrWhiteSpace(text) || text == lastSentText)
        {
            ReportStatus(GetWaitingStatus(state));
            return;
        }

        await SendSubtitleTextAsync(text, configuration, cancellationToken);
    }

    private async Task SendSubtitleTextAsync(string text, PotPlayerSubtitleSyncConfiguration configuration, CancellationToken cancellationToken)
    {
        lastSentText = text;
        configuration.DisplayOptions.Text = TruncateDisplayText(text);
        var sent = await displayService.SendTextAsync(configuration.DisplayOptions, cancellationToken);
        ReportStatus(sent ? $"已发送 PotPlayer 字幕：{configuration.DisplayOptions.Text}" : "发送失败，请确认音箱设备已连接");
        if (sent)
            SubtitleSent?.Invoke(this, configuration.DisplayOptions.Text);
    }

    private static string GetWaitingStatus(PotPlayerPlaybackState state)
        => state == PotPlayerPlaybackState.RunningUnknown
            ? "已检测到 PotPlayer，等待字幕文件更新"
            : "PotPlayer 正在播放，等待字幕文件更新";

    private static async Task<string> ReadTextWithSharedAccessAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private bool TryLoadTimelineSubtitle(string path, out SubtitleDocument document)
    {
        document = new SubtitleDocument { SourceName = Path.GetFileName(path) };
        try
        {
            var parser = subtitleParserFactory.GetParser(path);
            document = parser.Parse(path);
            return document.Cues.Count > 0;
        }   
        catch
        {
            return false;
        } 
    }

    private static string FormatPosition(TimeSpan position)
    {
        return position.TotalHours >= 1
            ? position.ToString(@"hh\:mm\:ss")
            : position.ToString(@"mm\:ss");
    }

    private static string GetPositionSource(PotPlayerPlaybackSnapshot snapshot)
        => snapshot.HasReliablePosition ? "媒体会话" : "本地计时";

    private static string TruncateDisplayText(string text)
    {
        var width = 0d;
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var nextWidth = width + GetDisplayWidth(rune);
            if (nextWidth > 32d)
                break;

            builder.Append(rune);
            width = nextWidth;
        }

        return builder.ToString().Trim();
    }

    private static double GetDisplayWidth(System.Text.Rune rune)
    {
        var value = rune.Value;
        return value <= 0x007f ? 0.5d : 1d;
    }

    private async Task RestoreCurrentSceneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await restoreService.RestoreAsync(displayService, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportStatus($"恢复当前个性场景失败：{ex.Message}");
        }
    }

    private string? ResolvePotPlayerSubtitleOutputPath(string configuredPath)
    {
        var mediaPath = playbackStateReader.TryGetCurrentMediaPath();
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;

        var subtitlePath = FindMatchingSubtitleForMedia(mediaPath);
        if (!string.IsNullOrWhiteSpace(subtitlePath))
            return subtitlePath;

        var configuredDirectory = ResolveConfiguredDirectory(configuredPath);
        return string.IsNullOrWhiteSpace(configuredDirectory)
            ? null
            : FindMatchingSubtitle(Path.Combine(configuredDirectory, Path.GetFileName(mediaPath)));
    }

    private static string? FindMatchingSubtitleForMedia(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : FindMatchingSubtitle(Path.Combine(directory, Path.GetFileName(mediaPath)));
    }

    private static string? FindMatchingSubtitle(string mediaPath)
    {
        var directory = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem) || !Directory.Exists(directory))
            return null;

        foreach (var extension in SubtitleExtensions)
        {
            var exactPath = Path.Combine(directory, $"{stem}{extension}");
            if (File.Exists(exactPath))
                return exactPath;
        }

        return Directory.EnumerateFiles(directory, $"{stem}.*")
            .Where(IsCandidateSubtitleOutput)
            .OrderBy(file => file.Length)
            .FirstOrDefault();
    }

    private static string? ResolveConfiguredDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Directory.Exists(path))
            return path;

        return File.Exists(path) ? Path.GetDirectoryName(path) : null;
    }

    private static string? ResolveSubtitleOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (File.Exists(path))
            return path;

        if (!Directory.Exists(path))
            return null;

        // 用户可直接填写 PotPlayer 输出目录；这里选择最近更新的文本/字幕文件作为实时输出源。
        return Directory.EnumerateFiles(path)
            .Where(file => IsCandidateSubtitleOutput(file))
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private static bool IsCandidateSubtitleOutput(string path)
    {
        var extension = Path.GetExtension(path);
        return SubtitleExtensions.Any(item => extension.Equals(item, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSubtitleText(string text, string extension)
    {
        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Select(line => NormalizeSubtitleLine(line, extension))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
            .Where(line => !int.TryParse(line, out _))
            .Where(line => !line.Contains("-->", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("[V4", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0)
            return string.Empty;

        return string.Join(" / ", lines.TakeLast(3));
    }

    private static string NormalizeSubtitleLine(string line, string extension)
    {
        if (extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
            line = StripLrcTimeTags(line);

        if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            return line;

        var fields = line.Split(',', 10);
        return fields.Length == 10
            ? fields[9].Replace("\\N", " ").Replace("\\n", " ").Trim()
            : line;
    }

    private static string StripLrcTimeTags(string line)
    {
        while (line.StartsWith("[", StringComparison.Ordinal))
        {
            var end = line.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
                break;

            line = line[(end + 1)..].TrimStart();
        }

        return line;
    }

    private void ReportStatus(string message) => StatusChanged?.Invoke(this, message);
}

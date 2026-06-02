using System.Text;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerSubtitleSyncService
{
    private readonly HaloPixelDisplayService displayService;
    private readonly PotPlayerPlaybackStateReader playbackStateReader;
    private readonly SubtitleParserFactory subtitleParserFactory = new();
    private CancellationTokenSource? cancellationTokenSource;
    private string lastSentText = string.Empty;
    private DateTime lastReadWriteTimeUtc;

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

    public void Start(PotPlayerSubtitleSyncConfiguration configuration)
    {
        Stop();
        cancellationTokenSource = new CancellationTokenSource();
        _ = SyncAsync(configuration, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
        ReportStatus("PotPlayer 字幕同步已停止");
    }

    private async Task SyncAsync(PotPlayerSubtitleSyncConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.SubtitleOutputPath))
        {
            ReportStatus("请先填写 PotPlayer 字幕输出文件路径");
            return;
        }

        var subtitleOutputPath = ResolveSubtitleOutputPath(configuration.SubtitleOutputPath);
        if (subtitleOutputPath is null)
        {
            ReportStatus("字幕输出文件不存在");
            return;
        }

        if (TryLoadTimelineSubtitle(subtitleOutputPath, out var document))
        {
            await SyncTimelineSubtitleAsync(document, configuration, cancellationToken);
            return;
        }

        ReportStatus("PotPlayer 实时字幕文件同步已启动");
        await TryReadAndSendSubtitleAsync(subtitleOutputPath, configuration, PotPlayerPlaybackState.RunningUnknown, true, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = playbackStateReader.GetState();
                if (state == PotPlayerPlaybackState.NotRunning)
                {
                    ReportStatus("未检测到 PotPlayer 进程");
                }
                else if (state == PotPlayerPlaybackState.Paused)
                {
                    ReportStatus("PotPlayer 已暂停，等待继续播放");
                }
                else
                {
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

        while (!cancellationToken.IsCancellationRequested)
        {
            var state = playbackStateReader.GetState();
            var now = DateTimeOffset.Now;
            var elapsed = now - lastTick;
            lastTick = now;

            if (state == PotPlayerPlaybackState.NotRunning)
            {
                ReportStatus("未检测到 PotPlayer 进程");
                await Task.Delay(configuration.PollInterval, cancellationToken);
                continue;
            }

            if (state == PotPlayerPlaybackState.Paused)
            {
                ReportStatus($"PotPlayer 已暂停，字幕位置 {FormatPosition(playbackPosition)}");
                await Task.Delay(configuration.PollInterval, cancellationToken);
                continue;
            }

            playbackPosition += elapsed;

            while (nextCueIndex < cues.Count && cues[nextCueIndex].End <= playbackPosition)
                nextCueIndex++;

            if (nextCueIndex >= cues.Count)
            {
                ReportStatus("字幕时间轴同步完成");
                Stop();
                return;
            }

            var cue = cues[nextCueIndex];
            if (cue.Start <= playbackPosition && cue.Text != lastSentText)
                await SendSubtitleTextAsync(cue.Text, configuration, cancellationToken);
            else
                ReportStatus($"时间轴同步中 {FormatPosition(playbackPosition)} / 下一条 {FormatPosition(cue.Start)}");

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

    private static string? ResolveSubtitleOutputPath(string path)
    {
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
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sub", StringComparison.OrdinalIgnoreCase);
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

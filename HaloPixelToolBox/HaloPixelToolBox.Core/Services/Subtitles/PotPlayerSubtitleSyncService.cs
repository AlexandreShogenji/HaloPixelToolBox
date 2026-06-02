using System.Text;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;

namespace HaloPixelToolBox.Core.Services.Subtitles;

public sealed class PotPlayerSubtitleSyncService
{
    private readonly HaloPixelDisplayService displayService;
    private readonly PotPlayerPlaybackStateReader playbackStateReader;
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

        ReportStatus("PotPlayer 字幕同步已启动");

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
                    await TryReadAndSendSubtitleAsync(configuration, state, cancellationToken);
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

    private async Task TryReadAndSendSubtitleAsync(PotPlayerSubtitleSyncConfiguration configuration, PotPlayerPlaybackState state, CancellationToken cancellationToken)
    {
        var subtitleOutputPath = ResolveSubtitleOutputPath(configuration.SubtitleOutputPath);
        if (subtitleOutputPath is null)
        {
            ReportStatus("字幕输出文件不存在");
            return;
        }

        var fileInfo = new FileInfo(subtitleOutputPath);
        if (fileInfo.LastWriteTimeUtc == lastReadWriteTimeUtc)
        {
            ReportStatus(GetWaitingStatus(state));
            return;
        }

        lastReadWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        var text = NormalizeSubtitleText(await ReadTextWithSharedAccessAsync(subtitleOutputPath, cancellationToken));
        if (string.IsNullOrWhiteSpace(text) || text == lastSentText)
        {
            ReportStatus(GetWaitingStatus(state));
            return;
        }

        lastSentText = text;
        configuration.DisplayOptions.Text = text;
        var sent = await displayService.SendTextAsync(configuration.DisplayOptions, cancellationToken);
        ReportStatus(sent ? $"已发送 PotPlayer 字幕：{text}" : "发送失败，请确认音箱设备已连接");
        if (sent)
            SubtitleSent?.Invoke(this, text);
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

    private static string NormalizeSubtitleText(string text)
    {
        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Select(NormalizeSubtitleLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
            .Where(line => !int.TryParse(line, out _))
            .Where(line => !line.Contains("-->", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
            return string.Empty;

        return string.Join(" / ", lines.TakeLast(3));
    }

    private static string NormalizeSubtitleLine(string line)
    {
        if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            return line;

        var fields = line.Split(',', 10);
        return fields.Length == 10
            ? fields[9].Replace("\\N", " ").Replace("\\n", " ").Trim()
            : line;
    }

    private void ReportStatus(string message) => StatusChanged?.Invoke(this, message);
}

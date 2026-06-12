using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Core.Services.Translation;

public class BilibiliAsrSubtitleCapture
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const double StreamingChunkSeconds = 60;
    private const double StreamingCutSearchWindowSeconds = 8;
    private const double MinimumStreamingChunkSeconds = 25;

    public async Task<IReadOnlyList<SubtitleCue>> FetchCuesAsync(
        BrowserTranslationConfiguration configuration,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var videoUrl = NormalizeVideoUrl(configuration.BilibiliVideoUrl);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return [];

        var subtitleCacheKey = BuildSubtitleCacheKey(videoUrl, configuration);
        if (TryReadCachedSubtitleCues(subtitleCacheKey, progress) is { Count: > 0 } cachedCues)
            return cachedCues;

        var ffmpeg = ResolveExecutable(["ffmpeg.exe", "ffmpeg"]);
        if (ffmpeg is null)
            throw new InvalidOperationException("未找到 ffmpeg，请先安装 ffmpeg 并重启软件");

        var ytdlp = ResolveExecutable(["yt-dlp.exe", "yt-dlp"]);
        var usePythonYtDlp = ytdlp is null;

        var requiredAsrModule = GetRequiredPythonModule(configuration.AsrEngine);
        var python = await ResolvePythonPathAsync(usePythonYtDlp, requiredAsrModule, cancellationToken);
        if (python is null)
            throw new InvalidOperationException("未找到 Python，请先安装 Python 3.9-3.12 并加入 PATH");

        if (usePythonYtDlp && !await PythonModuleAvailableAsync(python, "yt_dlp", cancellationToken))
            throw new InvalidOperationException("未找到 yt-dlp，请执行：pip install yt-dlp");

        if (!await PythonModuleAvailableAsync(python, requiredAsrModule, cancellationToken))
            throw new InvalidOperationException(BuildMissingAsrModuleMessage(configuration.AsrEngine));

        var tempDir = HaloPixelCachePaths.CreateOperationDirectory("BiliAsr");

        try
        {
            progress?.Report("ASR 兜底：正在准备 B 站音频...");
            var audioPath = await DownloadAudioAsync(videoUrl, tempDir, python, ytdlp, usePythonYtDlp, configuration.BrowserProcessName, progress, cancellationToken);

            progress?.Report("ASR 兜底：正在转码音频...");
            var wavPath = Path.Combine(tempDir, "audio.wav");
            await RunProcessAsync(ffmpeg, ["-i", audioPath, "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", "-y", wavPath], tempDir, cancellationToken);

            progress?.Report($"ASR 兜底：正在识别字幕（{GetAsrEngineDisplayName(configuration.AsrEngine)}），首次运行可能需要下载模型...");
            var scriptPath = Path.Combine(tempDir, "transcribe.py");
            await File.WriteAllTextAsync(scriptPath, AsrPythonScript, Encoding.UTF8, cancellationToken);

            var language = NormalizeLanguage(configuration.SourceLanguage);
            var result = await RunProcessAsync(
                python,
                ["-u", scriptPath, wavPath, language, GetAsrEngineArgument(configuration.AsrEngine), GetWhisperModelName(configuration.AsrEngine)],
                tempDir,
                cancellationToken,
                progress,
                environment: BuildPythonEnvironment(),
                suppressResultProtocolProgress: true);

            var cues = ParseAsrOutput(result.Stdout);
            if (cues.Count > 0)
                await WriteCachedSubtitleCuesAsync(subtitleCacheKey, videoUrl, configuration, cues, cancellationToken);
            progress?.Report($"ASR 兜底：已识别 {cues.Count} 条字幕");
            return cues;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Temporary files are best-effort cleanup.
            }
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<SubtitleCue>> StreamCuesAsync(
        BrowserTranslationConfiguration configuration,
        IProgress<string>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var videoUrl = NormalizeVideoUrl(configuration.BilibiliVideoUrl);
        if (string.IsNullOrWhiteSpace(videoUrl))
            yield break;

        var subtitleCacheKey = BuildSubtitleCacheKey(videoUrl, configuration);
        if (TryReadCachedSubtitleCues(subtitleCacheKey, progress) is { Count: > 0 } cachedCues)
        {
            yield return cachedCues;
            yield break;
        }

        var ffmpeg = ResolveExecutable(["ffmpeg.exe", "ffmpeg"]);
        if (ffmpeg is null)
            throw new InvalidOperationException("未找到 ffmpeg，请先安装 ffmpeg 并重启软件");

        var ffprobe = ResolveExecutable(["ffprobe.exe", "ffprobe"]);
        var ytdlp = ResolveExecutable(["yt-dlp.exe", "yt-dlp"]);
        var usePythonYtDlp = ytdlp is null;

        var requiredAsrModule = GetRequiredPythonModule(configuration.AsrEngine);
        var python = await ResolvePythonPathAsync(usePythonYtDlp, requiredAsrModule, cancellationToken);
        if (python is null)
            throw new InvalidOperationException("未找到 Python，请先安装 Python 3.9-3.12 并加入 PATH");

        if (usePythonYtDlp && !await PythonModuleAvailableAsync(python, "yt_dlp", cancellationToken))
            throw new InvalidOperationException("未找到 yt-dlp，请执行：pip install yt-dlp");

        if (!await PythonModuleAvailableAsync(python, requiredAsrModule, cancellationToken))
            throw new InvalidOperationException(BuildMissingAsrModuleMessage(configuration.AsrEngine));

        var tempDir = HaloPixelCachePaths.CreateOperationDirectory("BiliAsrStream");
        var allCues = new List<SubtitleCue>();

        try
        {
            progress?.Report("ASR 流式：正在准备 B 站音频...");
            var audioPath = await DownloadAudioAsync(videoUrl, tempDir, python, ytdlp, usePythonYtDlp, configuration.BrowserProcessName, progress, cancellationToken);
            var duration = ffprobe is null
                ? null
                : await TryGetAudioDurationAsync(ffprobe, audioPath, tempDir, cancellationToken);

            if (duration is null || duration.Value <= TimeSpan.Zero)
            {
                progress?.Report("ASR 流式：无法读取音频时长，回退到整段识别");
                var cues = await FetchCuesAsync(configuration, progress, cancellationToken);
                if (cues.Count > 0)
                    yield return cues;
                yield break;
            }

            var scriptPath = Path.Combine(tempDir, "transcribe.py");
            await File.WriteAllTextAsync(scriptPath, AsrPythonScript, Encoding.UTF8, cancellationToken);
            var language = NormalizeLanguage(configuration.SourceLanguage);
            var engine = GetAsrEngineArgument(configuration.AsrEngine);
            var model = GetWhisperModelName(configuration.AsrEngine);
            var estimatedChunkCount = (int)Math.Ceiling(duration.Value.TotalSeconds / StreamingChunkSeconds);
            var chunkIndex = 0;
            var nextChunkStart = TimeSpan.Zero;

            while (nextChunkStart < duration.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkStart = nextChunkStart;
                var chunkEnd = await FindStreamingChunkEndAsync(ffmpeg, audioPath, tempDir, chunkStart, duration.Value, cancellationToken);
                var chunkSeconds = Math.Max(0.1, (chunkEnd - chunkStart).TotalSeconds);
                var chunkWavPath = Path.Combine(tempDir, $"chunk-{chunkIndex:000}.wav");
                progress?.Report($"ASR 流式：正在识别第 {chunkIndex + 1}/约{estimatedChunkCount} 段（{FormatSeconds(chunkStart)}-{FormatSeconds(chunkEnd)}）");

                await RunProcessAsync(
                    ffmpeg,
                    [
                        "-ss", chunkStart.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                        "-t", chunkSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", audioPath,
                        "-ac", "1",
                        "-ar", "16000",
                        "-sample_fmt", "s16",
                        "-y", chunkWavPath
                    ],
                    tempDir,
                    cancellationToken);

                var result = await RunProcessAsync(
                    python,
                    ["-u", scriptPath, chunkWavPath, language, engine, model],
                    tempDir,
                    cancellationToken,
                    progress,
                    environment: BuildPythonEnvironment(),
                    suppressResultProtocolProgress: true);

                var chunkCues = OffsetCues(ParseAsrOutput(result.Stdout), chunkStart);
                if (chunkCues.Count == 0)
                {
                    nextChunkStart = chunkEnd;
                    chunkIndex++;
                    continue;
                }

                allCues.AddRange(chunkCues);
                progress?.Report($"ASR 流式：第 {chunkIndex + 1}/约{estimatedChunkCount} 段已生成 {chunkCues.Count} 条字幕");
                yield return chunkCues;

                nextChunkStart = chunkEnd;
                chunkIndex++;
            }

            if (allCues.Count > 0)
                await WriteCachedSubtitleCuesAsync(subtitleCacheKey, videoUrl, configuration, allCues, cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Temporary files are best-effort cleanup.
            }
        }
    }

    private static string BuildSubtitleCacheKey(string videoUrl, BrowserTranslationConfiguration configuration)
    {
        var bvid = BilibiliVideoUrlHelper.ExtractBvid(videoUrl);
        if (string.IsNullOrWhiteSpace(bvid))
            bvid = "bilibili";

        bvid = Regex.Replace(bvid, @"[^A-Za-z0-9_-]", string.Empty);
        var engine = GetAsrEngineArgument(configuration.AsrEngine);
        var language = NormalizeLanguage(configuration.SourceLanguage);
        var model = GetModelCacheIdentity(configuration.AsrEngine);
        var identity = string.Join("|", videoUrl, engine, language, model, "subtitle-cache-v1");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..12];

        return $"{bvid}-{engine}-{language}-{hash}";
    }

    private static IReadOnlyList<SubtitleCue>? TryReadCachedSubtitleCues(string cacheKey, IProgress<string>? progress)
    {
        Directory.CreateDirectory(HaloPixelCachePaths.BrowserSubtitleAsrSubtitleRoot);
        var cachePath = Path.Combine(HaloPixelCachePaths.BrowserSubtitleAsrSubtitleRoot, $"{cacheKey}.json");
        if (!File.Exists(cachePath))
            return null;

        try
        {
            var cache = JsonSerializer.Deserialize<AsrSubtitleCacheFile>(
                File.ReadAllText(cachePath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var cues = FilterAsrCues(cache?.Cues
                .Where(cue => !string.IsNullOrWhiteSpace(cue.Text) && !IsLikelyAsrHallucination(cue.Text))
                .Select(cue => new SubtitleCue
                {
                    Start = TimeSpan.FromSeconds(Math.Max(0, cue.Start)),
                    End = TimeSpan.FromSeconds(Math.Max(cue.Start, cue.End)),
                    Text = cue.Text.Trim()
                })
                .ToList() ?? []);

            if (cues.Count == 0)
                return null;

            progress?.Report("ASR 兜底：已命中字幕缓存，跳过模型识别");
            return cues;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCachedSubtitleCuesAsync(
        string cacheKey,
        string videoUrl,
        BrowserTranslationConfiguration configuration,
        IReadOnlyList<SubtitleCue> cues,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(HaloPixelCachePaths.BrowserSubtitleAsrSubtitleRoot);
        var cache = new AsrSubtitleCacheFile
        {
            SourceUrl = videoUrl,
            CacheKey = cacheKey,
            AsrEngine = GetAsrEngineArgument(configuration.AsrEngine),
            Model = GetModelCacheIdentity(configuration.AsrEngine),
            Language = NormalizeLanguage(configuration.SourceLanguage),
            CachedAt = DateTimeOffset.Now,
            Cues = FilterAsrCues(cues)
                .Where(cue => !string.IsNullOrWhiteSpace(cue.Text) && !IsLikelyAsrHallucination(cue.Text))
                .Select(cue => new CachedSubtitleCue
                {
                    Start = cue.Start.TotalSeconds,
                    End = cue.End.TotalSeconds,
                    Text = cue.Text
                })
                .ToList()
        };

        var cachePath = Path.Combine(HaloPixelCachePaths.BrowserSubtitleAsrSubtitleRoot, $"{cacheKey}.json");
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(cachePath, json, Encoding.UTF8, cancellationToken);
    }

    private static async Task<TimeSpan?> TryGetAudioDurationAsync(string ffprobe, string audioPath, string tempDir, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(
                ffprobe,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", audioPath],
                tempDir,
                cancellationToken,
                ignoreExitCode: true);

            var value = result.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<QuietRange>> DetectQuietRangesAsync(
        string ffmpeg,
        string audioPath,
        string tempDir,
        TimeSpan searchStart,
        TimeSpan searchDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new List<string>
            {
                "-ss", searchStart.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", searchDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", audioPath,
                "-af", "silencedetect=noise=-35dB:d=0.25",
                "-f", "null",
                "-"
            };

            var result = await RunProcessAsync(
                ffmpeg,
                args,
                tempDir,
                cancellationToken,
                ignoreExitCode: true);

            return ParseQuietRanges(result.Stderr)
                .Select(range => new QuietRange(range.Start + searchStart, range.End + searchStart))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<QuietRange> ParseQuietRanges(string stderr)
    {
        var ranges = new List<QuietRange>();
        double? start = null;

        foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var startMatch = Regex.Match(line, @"silence_start:\s*(?<value>[0-9]+(?:\.[0-9]+)?)");
            if (startMatch.Success
                && double.TryParse(startMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var startSeconds))
            {
                start = startSeconds;
                continue;
            }

            var endMatch = Regex.Match(line, @"silence_end:\s*(?<value>[0-9]+(?:\.[0-9]+)?)");
            if (!endMatch.Success
                || !double.TryParse(endMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var endSeconds)
                || start is null)
            {
                continue;
            }

            if (endSeconds > start.Value)
                ranges.Add(new QuietRange(TimeSpan.FromSeconds(start.Value), TimeSpan.FromSeconds(endSeconds)));

            start = null;
        }

        return ranges;
    }

    private static async Task<TimeSpan> FindStreamingChunkEndAsync(
        string ffmpeg,
        string audioPath,
        string tempDir,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var targetEnd = start + TimeSpan.FromSeconds(StreamingChunkSeconds);
        if (targetEnd >= duration)
            return duration;

        var minEnd = start + TimeSpan.FromSeconds(MinimumStreamingChunkSeconds);
        var cut = await FindQuietCutPointAsync(ffmpeg, audioPath, tempDir, targetEnd, minEnd, duration, cancellationToken);
        if (cut is null || cut.Value <= start)
            return targetEnd;

        return cut.Value;
    }

    private static async Task<IReadOnlyList<StreamingChunk>> BuildStreamingChunksAsync(
        string ffmpeg,
        string audioPath,
        string tempDir,
        TimeSpan duration,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = new List<StreamingChunk>();
        var start = TimeSpan.Zero;

        while (start < duration)
        {
            var targetEnd = start + TimeSpan.FromSeconds(StreamingChunkSeconds);
            if (targetEnd >= duration)
            {
                chunks.Add(new StreamingChunk(start, duration));
                break;
            }

            var minEnd = start + TimeSpan.FromSeconds(MinimumStreamingChunkSeconds);
            var cut = await FindQuietCutPointAsync(ffmpeg, audioPath, tempDir, targetEnd, minEnd, duration, cancellationToken) ?? targetEnd;
            if (cut <= start)
                cut = targetEnd;

            chunks.Add(new StreamingChunk(start, cut));
            progress?.Report($"ASR 流式：已按低音量切点准备第 {chunks.Count} 段（{FormatSeconds(start)}-{FormatSeconds(cut)}）");
            start = cut;
        }

        return chunks;
    }

    private static async Task<TimeSpan?> FindQuietCutPointAsync(
        string ffmpeg,
        string audioPath,
        string tempDir,
        TimeSpan target,
        TimeSpan minEnd,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromSeconds(StreamingCutSearchWindowSeconds);
        var searchStart = target - window;
        var searchEnd = target + window;
        if (searchStart < minEnd)
            searchStart = minEnd;
        if (searchEnd > duration)
            searchEnd = duration;

        var searchDuration = searchEnd - searchStart;
        if (searchDuration <= TimeSpan.Zero)
            return null;

        var quietRanges = await DetectQuietRangesAsync(ffmpeg, audioPath, tempDir, searchStart, searchDuration, cancellationToken);
        return quietRanges
            .Select(range =>
            {
                var rangeStart = range.Start > searchStart ? range.Start : searchStart;
                var rangeEnd = range.End < searchEnd ? range.End : searchEnd;
                if (rangeEnd <= rangeStart)
                    return (Cut: (TimeSpan?)null, Distance: TimeSpan.MaxValue);

                var midpoint = rangeStart + TimeSpan.FromTicks((rangeEnd - rangeStart).Ticks / 2);
                return (Cut: (TimeSpan?)midpoint, Distance: (midpoint - target).Duration());
            })
            .Where(item => item.Cut is not null)
            .OrderBy(item => item.Distance)
            .Select(item => item.Cut)
            .FirstOrDefault();
    }

    private static string FormatSeconds(TimeSpan value)
    {
        return value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
    }

    private static IReadOnlyList<SubtitleCue> OffsetCues(IReadOnlyList<SubtitleCue> cues, TimeSpan offset)
    {
        return cues
            .Select(cue => new SubtitleCue
            {
                Start = cue.Start + offset,
                End = cue.End + offset,
                Text = cue.Text
            })
            .Where(cue => !string.IsNullOrWhiteSpace(cue.Text))
            .ToList();
    }

    private static async Task<string> DownloadAudioAsync(
        string videoUrl,
        string tempDir,
        string python,
        string? ytdlp,
        bool usePythonYtDlp,
        string browserProcessName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildAudioCacheKey(videoUrl);
        if (TryGetCachedAudioPath(cacheKey) is { } cachedAudioPath)
        {
            progress?.Report("ASR 兜底：已命中 B 站音频缓存");
            return cachedAudioPath;
        }

        progress?.Report("ASR 兜底：正在下载 B 站音频...");
        var outputTemplate = Path.Combine(tempDir, "audio.%(ext)s");
        var commonArgs = new List<string>
        {
            "-f", "ba",
            "--user-agent", UserAgent,
            "--referer", "https://www.bilibili.com/",
            "--add-header", "Origin:https://www.bilibili.com",
            "--add-header", "Accept-Language:zh-CN,zh;q=0.9,en;q=0.8",
            "-o", outputTemplate,
            "--no-playlist",
            videoUrl
        };

        Exception? lastError = null;
        foreach (var args in BuildYtDlpDownloadArgumentAttempts(commonArgs, browserProcessName))
        {
            try
            {
                if (usePythonYtDlp)
                    await RunProcessAsync(python, ["-m", "yt_dlp", .. args], tempDir, cancellationToken, progress);
                else
                    await RunProcessAsync(ytdlp!, args, tempDir, cancellationToken, progress);

                lastError = null;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DeletePartialAudioFiles(tempDir);
                progress?.Report($"ASR 兜底：B 站音频下载尝试失败，正在切换下载参数：{ex.Message}");
            }
        }

        if (lastError is not null)
            throw lastError;

        var audioPath = Directory.EnumerateFiles(tempDir, "audio.*")
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (audioPath is null)
            throw new InvalidOperationException("yt-dlp 未生成音频文件");

        return await MoveAudioToCacheAsync(videoUrl, cacheKey, audioPath, cancellationToken);
    }

    private static IEnumerable<IReadOnlyList<string>> BuildYtDlpDownloadArgumentAttempts(IReadOnlyList<string> commonArgs, string browserProcessName)
    {
        var cookieBrowser = ResolveYtDlpCookieBrowser(browserProcessName);
        if (!string.IsNullOrWhiteSpace(cookieBrowser))
            yield return ["--cookies-from-browser", cookieBrowser, .. commonArgs];

        yield return commonArgs;
    }

    private static string ResolveYtDlpCookieBrowser(string browserProcessName)
    {
        var normalized = (browserProcessName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.EndsWith(".exe", StringComparison.Ordinal))
            normalized = normalized[..^4];

        return normalized switch
        {
            "" => "chrome",
            "chrome" or "chromium" or "brave" or "opera" or "vivaldi" or "firefox" or "safari" or "whale" => normalized,
            "msedge" or "edge" or "microsoftedge" => "edge",
            _ => "chrome"
        };
    }

    private static void DeletePartialAudioFiles(string tempDir)
    {
        foreach (var path in Directory.EnumerateFiles(tempDir, "audio.*"))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private static string BuildAudioCacheKey(string videoUrl)
    {
        var bvid = BilibiliVideoUrlHelper.ExtractBvid(videoUrl);
        if (string.IsNullOrWhiteSpace(bvid))
            bvid = "bilibili";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoUrl)))
            .ToLowerInvariant()[..12];
        return $"{bvid}-{hash}";
    }

    private static string? TryGetCachedAudioPath(string cacheKey)
    {
        Directory.CreateDirectory(HaloPixelCachePaths.BrowserSubtitleAsrAudioRoot);
        return Directory.EnumerateFiles(HaloPixelCachePaths.BrowserSubtitleAsrAudioRoot, $"{cacheKey}.*")
            .Where(IsCachedAudioFile)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsCachedAudioFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".part", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ytdl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> MoveAudioToCacheAsync(string videoUrl, string cacheKey, string audioPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(HaloPixelCachePaths.BrowserSubtitleAsrAudioRoot);

        var extension = Path.GetExtension(audioPath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".audio";

        var cachePath = Path.Combine(HaloPixelCachePaths.BrowserSubtitleAsrAudioRoot, $"{cacheKey}{extension}");
        if (TryGetCachedAudioPath(cacheKey) is { } existingCachePath)
            return existingCachePath;

        try
        {
            File.Move(audioPath, cachePath, false);
        }
        catch (IOException)
        {
            if (TryGetCachedAudioPath(cacheKey) is { } existingCachePathAfterRace)
                return existingCachePathAfterRace;

            throw;
        }

        await WriteAudioCacheMetadataAsync(videoUrl, cacheKey, cachePath, cancellationToken);
        return cachePath;
    }

    private static async Task WriteAudioCacheMetadataAsync(string videoUrl, string cacheKey, string cachePath, CancellationToken cancellationToken)
    {
        var metadata = new
        {
            SourceUrl = videoUrl,
            CacheKey = cacheKey,
            FileName = Path.GetFileName(cachePath),
            CachedAt = DateTimeOffset.Now
        };
        var metadataPath = Path.Combine(HaloPixelCachePaths.BrowserSubtitleAsrAudioRoot, $"{cacheKey}.json");
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, json, Encoding.UTF8, cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> BuildPythonEnvironment()
    {
        var environment = new Dictionary<string, string>(HaloPixelCachePaths.CreatePythonModelCacheEnvironment())
        {
            ["PYTHONIOENCODING"] = "utf-8"
        };

        var hfToken = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (string.IsNullOrWhiteSpace(hfToken))
            hfToken = Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(hfToken))
        {
            environment["HF_TOKEN"] = hfToken;
            environment["HUGGINGFACE_HUB_TOKEN"] = hfToken;
        }

        var hfEndpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(hfEndpoint))
            environment["HF_ENDPOINT"] = hfEndpoint.Trim().TrimEnd('/');

        var cudaDllDirectories = FindCudaDllDirectories();
        if (cudaDllDirectories.Count > 0)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            environment["PATH"] = string.Join(Path.PathSeparator, cudaDllDirectories) + Path.PathSeparator + currentPath;
            environment["HALOPIXEL_CUDA_DLL_DIRS"] = string.Join(Path.PathSeparator, cudaDllDirectories);
        }

        return environment;
    }

    private static IReadOnlyList<string> FindCudaDllDirectories()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var candidates = new List<string>();
        AddPathDirectories(candidates, Environment.GetEnvironmentVariable("PATH"));
        AddIfExists(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Lenovo",
            "AIAgent",
            "Modules",
            "X-Engine",
            "cuda_v12.8"));
        AddIfExists(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Lenovo",
            "AIAgent",
            "Modules",
            "X-Engine",
            "cuda_v11.7"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddIfExists(candidates, Path.Combine(localAppData, "Programs", "NVIDIA GPU Computing Toolkit", "CUDA", "v12.8", "bin"));
        AddIfExists(candidates, Path.Combine(localAppData, "Programs", "NVIDIA GPU Computing Toolkit", "CUDA", "v12.6", "bin"));
        AddIfExists(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA GPU Computing Toolkit",
            "CUDA",
            "v12.8",
            "bin"));
        AddIfExists(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA GPU Computing Toolkit",
            "CUDA",
            "v12.6",
            "bin"));

        return candidates
            .Where(directory => File.Exists(Path.Combine(directory, "cublas64_12.dll")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        static void AddPathDirectories(List<string> directories, string? pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
                return;

            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddIfExists(directories, directory);
        }

        static void AddIfExists(List<string> directories, string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                directories.Add(directory);
        }
    }

    private static IReadOnlyList<SubtitleCue> ParseAsrOutput(string stdout)
    {
        var match = Regex.Match(stdout, "---RESULT---\\r?\\n(?<json>[\\s\\S]*?)\\r?\\n---END---");
        if (!match.Success)
            throw new InvalidOperationException("无法解析 ASR 输出，请确认 FunASR/SenseVoice 已正确安装");

        var segments = JsonSerializer.Deserialize<List<AsrSegment>>(
            match.Groups["json"].Value,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var cues = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text) && !IsLikelyAsrHallucination(segment.Text))
            .Select(segment => new SubtitleCue
            {
                Start = TimeSpan.FromSeconds(Math.Max(0, segment.Start)),
                End = TimeSpan.FromSeconds(Math.Max(segment.Start, segment.End)),
                Text = segment.Text.Trim()
            })
            .ToList();

        return FilterAsrCues(cues);
    }

    private static IReadOnlyList<SubtitleCue> FilterAsrCues(IEnumerable<SubtitleCue> cues)
    {
        var filtered = new List<SubtitleCue>();
        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            if (string.IsNullOrWhiteSpace(cue.Text) || IsLikelyAsrHallucination(cue.Text))
                continue;

            var normalized = NormalizeAsrFilterText(cue.Text);
            if (normalized.Length == 0)
                continue;

            var repeatedCount = 0;
            for (var index = filtered.Count - 1; index >= 0 && index >= filtered.Count - 3; index--)
            {
                if (NormalizeAsrFilterText(filtered[index].Text).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    repeatedCount++;
            }

            if (repeatedCount >= 2 && normalized.Length <= 24)
                continue;

            filtered.Add(cue);
        }

        return filtered;
    }

    private static bool IsLikelyAsrHallucination(string text)
    {
        var compact = NormalizeAsrFilterText(text);
        if (compact.Length == 0)
            return true;

        if ((compact.Contains("ご視聴", StringComparison.Ordinal) || compact.Contains("ご清聴", StringComparison.Ordinal))
            && (compact.Contains("ありがとう", StringComparison.Ordinal) || compact.Contains("有難う", StringComparison.Ordinal)))
        {
            return true;
        }

        if (compact.Contains("チャンネル登録", StringComparison.Ordinal)
            && (compact.Contains("お願い", StringComparison.Ordinal) || compact.Contains("よろしく", StringComparison.Ordinal)))
        {
            return true;
        }

        if ((compact.Contains("高評価", StringComparison.Ordinal) || compact.Contains("いいね", StringComparison.Ordinal))
            && compact.Contains("チャンネル登録", StringComparison.Ordinal))
        {
            return true;
        }

        if ((compact.Contains("感谢", StringComparison.Ordinal) || compact.Contains("感謝", StringComparison.Ordinal)
             || compact.Contains("谢谢", StringComparison.Ordinal) || compact.Contains("謝謝", StringComparison.Ordinal))
            && (compact.Contains("观看", StringComparison.Ordinal) || compact.Contains("觀看", StringComparison.Ordinal)
                || compact.Contains("收看", StringComparison.Ordinal) || compact.Contains("订阅", StringComparison.Ordinal)
                || compact.Contains("訂閱", StringComparison.Ordinal)))
        {
            return true;
        }

        if ((compact.Contains("点赞", StringComparison.Ordinal) || compact.Contains("點贊", StringComparison.Ordinal)
             || compact.Contains("点个赞", StringComparison.Ordinal))
            && (compact.Contains("订阅", StringComparison.Ordinal) || compact.Contains("訂閱", StringComparison.Ordinal)))
        {
            return true;
        }

        var normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim().Trim('.', '!', '?').ToLowerInvariant();
        return (normalized.Contains("thanks", StringComparison.Ordinal) || normalized.Contains("thank you", StringComparison.Ordinal))
               && (normalized.Contains("watching", StringComparison.Ordinal) || normalized.Contains("subscribe", StringComparison.Ordinal));
    }

    private static string NormalizeAsrFilterText(string text)
    {
        return Regex.Replace(text ?? string.Empty, @"[\s　。.!！?？…、,，:：;；""'“”‘’（）()\[\]【】]+", string.Empty).Trim();
    }

    private static string NormalizeVideoUrl(string input)
    {
        return BilibiliVideoUrlHelper.NormalizeVideoUrl(input);
    }

    private static string NormalizeLanguage(string language)
    {
        var normalized = language.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "auto")
            return "auto";
        if (normalized.StartsWith("zh", StringComparison.Ordinal))
            return "zh";
        if (normalized.StartsWith("ja", StringComparison.Ordinal) || normalized.StartsWith("jp", StringComparison.Ordinal))
            return "ja";
        if (normalized.StartsWith("en", StringComparison.Ordinal))
            return "en";
        return "auto";
    }

    private static string GetRequiredPythonModule(BrowserSubtitleAsrEngine engine)
    {
        return engine == BrowserSubtitleAsrEngine.SenseVoiceSmall
            ? "funasr"
            : "faster_whisper";
    }

    private static string BuildMissingAsrModuleMessage(BrowserSubtitleAsrEngine engine)
    {
        return engine == BrowserSubtitleAsrEngine.SenseVoiceSmall
            ? "未找到 SenseVoice/FunASR，请执行：pip install funasr torch torchaudio"
            : "未找到 faster-whisper，请执行：pip install faster-whisper";
    }

    private static string GetAsrEngineArgument(BrowserSubtitleAsrEngine engine)
    {
        return engine switch
        {
            BrowserSubtitleAsrEngine.SenseVoiceSmall => "sensevoice",
            BrowserSubtitleAsrEngine.KotobaWhisperJapanese => "kotoba",
            _ => "whisper"
        };
    }

    private static string GetAsrEngineDisplayName(BrowserSubtitleAsrEngine engine)
    {
        return engine switch
        {
            BrowserSubtitleAsrEngine.SenseVoiceSmall => "SenseVoiceSmall",
            BrowserSubtitleAsrEngine.KotobaWhisperJapanese => "Kotoba-Whisper",
            _ => "Whisper large-v3-turbo"
        };
    }

    private static string GetWhisperModelName(BrowserSubtitleAsrEngine engine)
    {
        return engine == BrowserSubtitleAsrEngine.KotobaWhisperJapanese
            ? "kotoba-tech/kotoba-whisper-v2.0-faster"
            : "large-v3-turbo";
    }

    private static string GetModelCacheIdentity(BrowserSubtitleAsrEngine engine)
    {
        return engine == BrowserSubtitleAsrEngine.SenseVoiceSmall
            ? "funasr/sensevoice-small"
            : GetWhisperModelName(engine);
    }

    private static async Task<string?> ResolvePythonPathAsync(bool requireYtDlpModule, string requiredAsrModule, CancellationToken cancellationToken)
    {
        var candidates = ResolveExecutableCandidates(["python.exe", "python3.exe", "py.exe", "python", "python3", "py"]).ToList();
        foreach (var candidate in candidates)
        {
            var hasAsr = await PythonModuleAvailableAsync(candidate, requiredAsrModule, cancellationToken);
            if (!hasAsr)
                continue;

            if (requireYtDlpModule && !await PythonModuleAvailableAsync(candidate, "yt_dlp", cancellationToken))
                continue;

            return candidate;
        }

        return candidates.FirstOrDefault();
    }

    private static string? ResolveExecutable(IEnumerable<string> names)
        => ResolveExecutableCandidates(names).FirstOrDefault();

    private static IEnumerable<string> ResolveExecutableCandidates(IEnumerable<string> names)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(GetExtraSearchDirectories())
            .Where(path => !path.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            foreach (var directory in paths)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && seen.Add(candidate))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GetExtraSearchDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var localPythonRoot = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(localPythonRoot))
        {
            foreach (var pythonDir in Directory.EnumerateDirectories(localPythonRoot, "Python*", SearchOption.TopDirectoryOnly))
            {
                yield return pythonDir;
                yield return Path.Combine(pythonDir, "Scripts");
            }
        }

        yield return Path.Combine(programFiles, "ffmpeg", "bin");
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");

        var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            foreach (var ffmpegBin in Directory.EnumerateDirectories(wingetPackages, "Gyan.FFmpeg_*", SearchOption.TopDirectoryOnly)
                         .SelectMany(path => Directory.EnumerateDirectories(path, "ffmpeg-*", SearchOption.TopDirectoryOnly))
                         .Select(path => Path.Combine(path, "bin")))
            {
                yield return ffmpegBin;
            }

            foreach (var ffmpegBin in Directory.EnumerateDirectories(wingetPackages, "yt-dlp.FFmpeg_*", SearchOption.TopDirectoryOnly)
                         .SelectMany(path => Directory.EnumerateDirectories(path, "ffmpeg-*", SearchOption.TopDirectoryOnly))
                         .Select(path => Path.Combine(path, "bin")))
            {
                yield return ffmpegBin;
            }

            foreach (var ytDlpDir in Directory.EnumerateDirectories(wingetPackages, "yt-dlp.yt-dlp_*", SearchOption.TopDirectoryOnly))
            {
                yield return ytDlpDir;
            }
        }

        var pythonRoot = Path.Combine(appData, "Python");
        if (Directory.Exists(pythonRoot))
        {
            foreach (var scripts in Directory.EnumerateDirectories(pythonRoot, "Python*", SearchOption.TopDirectoryOnly)
                         .Select(path => Path.Combine(path, "Scripts")))
            {
                yield return scripts;
            }
        }
    }

    private static async Task<bool> PythonModuleAvailableAsync(string python, string moduleName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(python, ["-c", $"import {moduleName}"], Environment.CurrentDirectory, cancellationToken, ignoreExitCode: true);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool suppressResultProtocolProgress = false,
        bool ignoreExitCode = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data) && ShouldReportProcessLine(e.Data, suppressResultProtocolProgress))
                progress?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stderr.AppendLine(e.Data);
            if (!string.IsNullOrWhiteSpace(e.Data) && ShouldReportProcessLine(e.Data, suppressResultProtocolProgress))
                progress?.Report(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Process may already be gone.
            }

            throw;
        }

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (result.ExitCode != 0 && !ignoreExitCode)
            throw new InvalidOperationException(BuildProcessErrorMessage(fileName, result));

        return result;
    }

    private static bool ShouldReportProcessLine(string line, bool suppressResultProtocolProgress)
    {
        if (!suppressResultProtocolProgress)
            return true;

        var trimmed = line.TrimStart();
        return trimmed is not ("---RESULT---" or "---END---")
               && !trimmed.StartsWith("[{", StringComparison.Ordinal)
               && !trimmed.StartsWith("{\"", StringComparison.Ordinal);
    }

    private static string BuildProcessErrorMessage(string fileName, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        details = details.Trim();
        if (details.Length > 500)
            details = details[^500..];

        return $"{Path.GetFileName(fileName)} 执行失败：{details}";
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record QuietRange(TimeSpan Start, TimeSpan End);

    private sealed record StreamingChunk(TimeSpan Start, TimeSpan End);

    private sealed class AsrSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class AsrSubtitleCacheFile
    {
        public string SourceUrl { get; set; } = string.Empty;
        public string CacheKey { get; set; } = string.Empty;
        public string AsrEngine { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public DateTimeOffset CachedAt { get; set; }
        public List<CachedSubtitleCue> Cues { get; set; } = [];
    }

    private sealed class CachedSubtitleCue
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private const string AsrPythonScript = """
import json
import os
import re
import sys
import time
import inspect

audio_file = sys.argv[1]
language = sys.argv[2] if len(sys.argv) > 2 else "auto"
asr_engine = sys.argv[3] if len(sys.argv) > 3 else "sensevoice"
whisper_model_name = sys.argv[4] if len(sys.argv) > 4 else "large-v3-turbo"

def configure_windows_cuda_dll_search_path():
    if os.name != "nt" or not hasattr(os, "add_dll_directory"):
        return

    directories = []
    for value in (os.environ.get("HALOPIXEL_CUDA_DLL_DIRS", ""), os.environ.get("PATH", "")):
        directories.extend([item for item in value.split(os.pathsep) if item])

    added = set()
    for directory in directories:
        if directory in added or not os.path.isdir(directory):
            continue
        if not os.path.exists(os.path.join(directory, "cublas64_12.dll")):
            continue
        try:
            os.add_dll_directory(directory)
            added.add(directory)
            print(f"ASR device: added CUDA DLL directory {directory}", flush=True)
        except Exception as exc:
            print(f"ASR device: failed to add CUDA DLL directory {directory}: {exc}", flush=True)

configure_windows_cuda_dll_search_path()

def clean_text(raw):
    return re.sub(r"<\|[^|]+\|>", "", raw or "").strip()

def compact_filter_text(raw):
    return re.sub(r"[\s　。.!！?？…、,，:：;；\"'“”‘’（）()\[\]【】]+", "", raw or "").strip()

def is_likely_asr_hallucination(raw):
    compact = compact_filter_text(raw)
    if not compact:
        return True
    if ("ご視聴" in compact or "ご清聴" in compact) and ("ありがとう" in compact or "有難う" in compact):
        return True
    if "チャンネル登録" in compact and ("お願い" in compact or "よろしく" in compact):
        return True
    if ("高評価" in compact or "いいね" in compact) and "チャンネル登録" in compact:
        return True
    if ("感谢" in compact or "感謝" in compact or "谢谢" in compact or "謝謝" in compact) and (
        "观看" in compact or "觀看" in compact or "收看" in compact or "订阅" in compact or "訂閱" in compact
    ):
        return True
    if ("点赞" in compact or "點贊" in compact or "点个赞" in compact) and ("订阅" in compact or "訂閱" in compact):
        return True
    lower = re.sub(r"\s+", " ", raw or "").strip(" .!?").lower()
    return ("thanks" in lower or "thank you" in lower) and ("watching" in lower or "subscribe" in lower)

def join_token(text, token):
    if not text:
        return token
    if text[-1].isascii() and text[-1].isalnum() and token[0].isascii() and token[0].isalnum():
        return text + " " + token
    return text + token

def split_words(words, timestamps):
    segments = []
    text = ""
    start = None
    end = None
    end_punc = set("。！？!?，,、；;：:.")
    max_chars = 34
    max_duration = 4.5

    for word, timestamp in zip(words, timestamps):
        token = clean_text(str(word))
        if not token or len(timestamp) < 2:
            continue

        token_start = max(0.0, float(timestamp[0]) / 1000.0)
        token_end = max(token_start + 0.1, float(timestamp[1]) / 1000.0)

        if start is None:
            start = token_start

        candidate = join_token(text, token)
        if text and (len(candidate) > max_chars or token_start - start >= max_duration):
            segments.append({"start": round(start, 3), "end": round(max(end or start, start + 0.5), 3), "text": text})
            text = token
            start = token_start
        else:
            text = candidate

        end = token_end
        if text and token[-1] in end_punc and len(text) >= 8:
            segments.append({"start": round(start, 3), "end": round(max(end, start + 0.5), 3), "text": text})
            text = ""
            start = None
            end = None

    if text and start is not None:
        segments.append({"start": round(start, 3), "end": round(max(end or start, start + 0.5), 3), "text": text})

    return segments

def split_whisper_words(words):
    segments = []
    text = ""
    start = None
    end = None
    end_punc = set("。！？!?，,、；;：:.")
    max_chars = 34
    max_duration = 4.5

    for word in words:
        token = clean_text(str(getattr(word, "word", ""))).strip()
        token_start = getattr(word, "start", None)
        token_end = getattr(word, "end", None)
        if not token or token_start is None or token_end is None:
            continue

        token_start = max(0.0, float(token_start))
        token_end = max(token_start + 0.1, float(token_end))

        if start is None:
            start = token_start

        candidate = join_token(text, token)
        if text and (len(candidate) > max_chars or token_start - start >= max_duration):
            segments.append({"start": round(start, 3), "end": round(max(end or start, start + 0.5), 3), "text": text})
            text = token
            start = token_start
        else:
            text = candidate

        end = token_end
        if text and token[-1] in end_punc and len(text) >= 8:
            segments.append({"start": round(start, 3), "end": round(max(end, start + 0.5), 3), "text": text})
            text = ""
            start = None
            end = None

    if text and start is not None:
        segments.append({"start": round(start, 3), "end": round(max(end or start, start + 0.5), 3), "text": text})

    return segments

def normalize_whisper_language(value, engine):
    normalized = (value or "").strip().lower()
    if engine == "kotoba" and (not normalized or normalized == "auto"):
        return "ja"
    if not normalized or normalized == "auto":
        return None
    if normalized.startswith("zh"):
        return "zh"
    if normalized.startswith("ja") or normalized.startswith("jp"):
        return "ja"
    if normalized.startswith("en"):
        return "en"
    return None

def resolve_sensevoice_device():
    override = os.environ.get("HALOPIXEL_ASR_DEVICE", "auto").strip().lower()
    if override == "cpu":
        return "cpu"
    if override in ("cuda", "gpu") or override.startswith("cuda"):
        return "cuda:0"
    try:
        import torch
        if torch.cuda.is_available():
            return "cuda:0"
    except Exception:
        pass
    return "cpu"

def resolve_whisper_device():
    override = os.environ.get("HALOPIXEL_ASR_DEVICE", "auto").strip().lower()
    if override == "cpu":
        return "cpu", "int8"

    def cuda_runtime_available():
        if os.name != "nt":
            return True
        try:
            import ctypes
            ctypes.WinDLL("cublas64_12.dll")
            return True
        except Exception:
            return False

    def cuda_compute_type():
        try:
            import ctranslate2
            supported = set(ctranslate2.get_supported_compute_types("cuda"))
            if "float16" in supported:
                return "float16"
            if "int8_float16" in supported:
                return "int8_float16"
            if "int8" in supported:
                return "int8"
        except Exception:
            pass
        return "float16"

    if override in ("cuda", "gpu") or override.startswith("cuda"):
        if cuda_runtime_available():
            return "cuda", cuda_compute_type()
        print("ASR device: CUDA device found but cublas64_12.dll is unavailable; using CPU", flush=True)
        return "cpu", "int8"

    try:
        import ctranslate2
        if ctranslate2.get_cuda_device_count() > 0 and cuda_runtime_available():
            return "cuda", cuda_compute_type()
        if ctranslate2.get_cuda_device_count() > 0:
            print("ASR device: CUDA device found but cublas64_12.dll is unavailable; using CPU", flush=True)
    except Exception:
        pass

    return "cpu", "int8"

def normalize_segments(segments):
    cleaned = []
    for segment in segments:
        text = clean_text(segment.get("text", ""))
        if not text or is_likely_asr_hallucination(text):
            continue
        start = max(0.0, float(segment.get("start", 0.0)))
        end = max(start + 0.5, float(segment.get("end", start + 0.5)))
        cleaned.append({"start": round(start, 3), "end": round(end, 3), "text": text})

    cleaned.sort(key=lambda item: (item["start"], item["end"]))
    merged = []
    for segment in cleaned:
        duration = segment["end"] - segment["start"]
        if merged and (len(segment["text"]) <= 2 or duration < 0.75) and segment["start"] - merged[-1]["end"] <= 1.0:
            merged[-1]["text"] = join_token(merged[-1]["text"], segment["text"])
            merged[-1]["end"] = max(merged[-1]["end"], segment["end"])
            continue
        merged.append(segment)

    for index in range(len(merged) - 1):
        current = merged[index]
        following = merged[index + 1]
        if current["end"] <= following["start"]:
            continue
        if following["start"] - current["start"] >= 0.5:
            current["end"] = following["start"]
        else:
            following["start"] = min(current["end"], max(following["start"], following["end"] - 0.5))

    return merged

def filter_transcribe_kwargs(model, kwargs):
    try:
        allowed = set(inspect.signature(model.transcribe).parameters.keys())
        return {key: value for key, value in kwargs.items() if key in allowed}
    except Exception:
        return kwargs

def is_low_confidence_whisper_entry(entry):
    no_speech_prob = getattr(entry, "no_speech_prob", None)
    avg_logprob = getattr(entry, "avg_logprob", None)
    compression_ratio = getattr(entry, "compression_ratio", None)
    try:
        if no_speech_prob is not None and avg_logprob is not None and float(no_speech_prob) > 0.55 and float(avg_logprob) < -1.0:
            return True
    except Exception:
        pass
    try:
        if compression_ratio is not None and float(compression_ratio) > 2.4:
            return True
    except Exception:
        pass
    return False

def emit(segments):
    print("---RESULT---")
    print(json.dumps(normalize_segments(segments), ensure_ascii=False))
    print("---END---")

def run_sensevoice():
    from funasr import AutoModel
    device = resolve_sensevoice_device()
    print(f"ASR device: SenseVoice {device}", flush=True)
    try:
        print(f"ASR model: loading/downloading iic/SenseVoiceSmall; cache={os.environ.get('MODELSCOPE_CACHE', '')}", flush=True)
        model = AutoModel(
            model="iic/SenseVoiceSmall",
            trust_remote_code=True,
            vad_model="fsmn-vad",
            vad_kwargs={"max_single_segment_time": 30000},
            device=device,
            disable_update=True,
        )
    except Exception as exc:
        if device == "cpu":
            raise
        print(f"ASR device: SenseVoice CUDA unavailable, fallback to CPU ({exc})", flush=True)
        print(f"ASR model: loading/downloading iic/SenseVoiceSmall; cache={os.environ.get('MODELSCOPE_CACHE', '')}", flush=True)
        model = AutoModel(
            model="iic/SenseVoiceSmall",
            trust_remote_code=True,
            vad_model="fsmn-vad",
            vad_kwargs={"max_single_segment_time": 30000},
            device="cpu",
            disable_update=True,
        )
    result = model.generate(
        input=audio_file,
        language=language,
        use_itn=True,
        output_timestamp=True,
        batch_size_s=60,
        merge_vad=False,
    )
    segments = []
    fallback_start = 0.0
    for entry in result:
        text = clean_text(entry.get("text", ""))
        if not text:
            continue

        words = entry.get("words") or []
        timestamps = entry.get("timestamp") or []
        timed_segments = split_words(words, timestamps)
        if timed_segments:
            segments.extend(timed_segments)
            fallback_start = max(fallback_start, timed_segments[-1]["end"])
            continue

        start = entry.get("start", None)
        end = entry.get("end", None)
        if start is None or end is None:
            start_s = fallback_start
            end_s = start_s + 3.0
            fallback_start = end_s
        else:
            start_s = float(start) / 1000.0
            end_s = float(end) / 1000.0
        segments.append({"start": round(start_s, 3), "end": round(max(end_s, start_s + 0.5), 3), "text": text})
    return segments

def format_bytes(value):
    units = ["B", "KB", "MB", "GB"]
    size = float(value or 0)
    for unit in units:
        if size < 1024 or unit == units[-1]:
            return f"{size:.1f}{unit}"
        size /= 1024

def directory_size(path):
    if not path or not os.path.isdir(path):
        return 0
    total = 0
    for root, _, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total

def resolve_whisper_repo(model_name):
    aliases = {
        "large-v3-turbo": "h2oai/faster-whisper-large-v3-turbo",
        "small": "Systran/faster-whisper-small",
        "medium": "Systran/faster-whisper-medium",
        "large-v3": "Systran/faster-whisper-large-v3",
    }
    return aliases.get(model_name, model_name)

def resolve_whisper_model_path(model_name):
    if os.path.isdir(model_name):
        return model_name

    import urllib.error
    import urllib.parse
    import urllib.request

    repo_id = resolve_whisper_repo(model_name)
    endpoint = (os.environ.get("HF_ENDPOINT") or "https://huggingface.co").rstrip("/")
    hf_home = os.environ.get("HF_HOME") or os.environ.get("HUGGINGFACE_HUB_CACHE") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")
    model_dir = os.path.join(hf_home, "direct", repo_id.replace("/", "--"))
    token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_HUB_TOKEN") or None
    required_files = ["config.json", "tokenizer.json", "vocabulary.json", "preprocessor_config.json", "model.bin"]
    os.makedirs(model_dir, exist_ok=True)
    complete_marker = os.path.join(model_dir, "download-complete.json")

    def has_complete_marker():
        if not os.path.isfile(complete_marker):
            return False
        for filename in required_files:
            path = os.path.join(model_dir, filename)
            if not os.path.isfile(path) or os.path.getsize(path) <= 0:
                return False
        return True

    if has_complete_marker():
        print(f"ASR model: cached {repo_id}; path={model_dir}", flush=True)
        return model_dir

    def build_url(filename):
        quoted_repo = "/".join(urllib.parse.quote(part) for part in repo_id.split("/"))
        return f"{endpoint}/{quoted_repo}/resolve/main/{urllib.parse.quote(filename)}"

    def remote_file_size(filename):
        headers = {"User-Agent": "HaloPixelToolBox/ASR"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(build_url(filename), headers=headers, method="HEAD")
        with urllib.request.urlopen(request, timeout=30) as response:
            for key in ("X-Linked-Size", "x-linked-size", "Content-Length", "content-length"):
                value = response.headers.get(key)
                if value:
                    try:
                        return int(value)
                    except Exception:
                        pass
        return None

    def expected_size_from_headers(response, resume_from):
        content_range = response.headers.get("Content-Range") or response.headers.get("content-range")
        if content_range and "/" in content_range:
            try:
                return int(content_range.rsplit("/", 1)[1])
            except Exception:
                pass
        content_length = response.headers.get("Content-Length") or response.headers.get("content-length")
        if content_length:
            try:
                return int(content_length) + resume_from
            except Exception:
                pass
        return None

    def download_file(filename):
        path = os.path.join(model_dir, filename)
        partial = path + ".partial"
        expected_size = remote_file_size(filename)
        if os.path.isfile(path) and os.path.getsize(path) > 0:
            current_size = os.path.getsize(path)
            if expected_size is None or current_size == expected_size:
                print(f"ASR model: cached {filename} ({format_bytes(current_size)})", flush=True)
                return

            if current_size < expected_size:
                partial_size = os.path.getsize(partial) if os.path.isfile(partial) else 0
                print(f"ASR model: incomplete {filename} {format_bytes(current_size)}/{format_bytes(expected_size)}; resume download", flush=True)
                if current_size > partial_size:
                    if os.path.isfile(partial):
                        os.remove(partial)
                    os.replace(path, partial)
                else:
                    os.remove(path)
            else:
                print(f"ASR model: invalid oversized {filename} {format_bytes(current_size)}/{format_bytes(expected_size)}; redownload", flush=True)
                os.remove(path)

        resume_from = os.path.getsize(partial) if os.path.isfile(partial) else 0
        url = build_url(filename)
        headers = {"User-Agent": "HaloPixelToolBox/ASR"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if resume_from > 0:
            headers["Range"] = f"bytes={resume_from}-"

        print(f"ASR model: downloading {filename} from {endpoint}; resume={format_bytes(resume_from)}", flush=True)
        request = urllib.request.Request(url, headers=headers)
        started = last_report = time.time()
        downloaded = resume_from
        with urllib.request.urlopen(request, timeout=30) as response:
            total_size = expected_size_from_headers(response, resume_from)
            if total_size is None:
                total_size = expected_size
            mode = "ab" if resume_from > 0 and response.status == 206 else "wb"
            if mode == "wb":
                downloaded = 0
            with open(partial, mode) as output:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    output.write(chunk)
                    downloaded += len(chunk)
                    now = time.time()
                    if now - last_report >= 5:
                        suffix = f"/{format_bytes(total_size)}" if total_size else ""
                        speed = (downloaded - resume_from) / max(0.1, now - started)
                        print(f"ASR model: {filename} {format_bytes(downloaded)}{suffix} at {format_bytes(speed)}/s", flush=True)
                        last_report = now

        if total_size is not None and downloaded != total_size:
            print(f"ASR model: incomplete download {filename} {format_bytes(downloaded)}/{format_bytes(total_size)}; will resume next time", flush=True)
            raise RuntimeError(f"{filename} incomplete: downloaded {downloaded} of {total_size} bytes")

        os.replace(partial, path)
        print(f"ASR model: ready file {filename} ({format_bytes(os.path.getsize(path))})", flush=True)

    print(f"ASR model: resolving {repo_id}; endpoint={endpoint}; path={model_dir}", flush=True)
    for filename in required_files:
        try:
            download_file(filename)
        except urllib.error.HTTPError as exc:
            if filename == "preprocessor_config.json" and exc.code == 404:
                continue
            print(f"ASR model: download failed {filename}: HTTP {exc.code} {exc.reason}", flush=True)
            raise
        except Exception as exc:
            print(f"ASR model: download failed {filename}: {exc}", flush=True)
            raise

    with open(complete_marker, "w", encoding="utf-8") as marker:
        json.dump({
            "repo_id": repo_id,
            "endpoint": endpoint,
            "completed_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "files": {filename: os.path.getsize(os.path.join(model_dir, filename)) for filename in required_files if os.path.isfile(os.path.join(model_dir, filename))}
        }, marker, ensure_ascii=False, indent=2)

    print(f"ASR model: ready {repo_id}; path={model_dir}", flush=True)
    return model_dir

def run_whisper(model_name, engine):
    from faster_whisper import WhisperModel
    model_path = resolve_whisper_model_path(model_name)

    def transcribe_once(device, compute_type):
        print(f"ASR device: Whisper {device}/{compute_type}", flush=True)
        print(f"ASR model: loading {model_path}", flush=True)
        model = WhisperModel(
            model_path,
            device=device,
            compute_type=compute_type,
            cpu_threads=max(1, os.cpu_count() or 4),
            num_workers=1,
        )
        transcribe_kwargs = filter_transcribe_kwargs(model, {
            "language": normalize_whisper_language(language, engine),
            "task": "transcribe",
            "beam_size": 1,
            "vad_filter": True,
            "vad_parameters": {"min_silence_duration_ms": 500, "speech_pad_ms": 200},
            "word_timestamps": True,
            "condition_on_previous_text": False,
            "without_timestamps": False,
            "temperature": 0.0,
            "no_speech_threshold": 0.45,
            "log_prob_threshold": -1.0,
            "compression_ratio_threshold": 2.4,
            "hallucination_silence_threshold": 2.0,
        })
        segments_iter, info = model.transcribe(audio_file, **transcribe_kwargs)

        segments = []
        for entry in segments_iter:
            if is_low_confidence_whisper_entry(entry):
                continue

            text = clean_text(getattr(entry, "text", ""))
            if not text or is_likely_asr_hallucination(text):
                continue

            word_segments = split_whisper_words(getattr(entry, "words", None) or [])
            if word_segments:
                segments.extend(word_segments)
                continue

            start_s = max(0.0, float(getattr(entry, "start", 0.0)))
            end_s = max(start_s + 0.5, float(getattr(entry, "end", start_s + 3.0)))
            segments.append({"start": round(start_s, 3), "end": round(end_s, 3), "text": text})
        return segments

    device, compute_type = resolve_whisper_device()
    if device == "cpu":
        return transcribe_once("cpu", "int8")

    try:
        return transcribe_once(device, compute_type)
    except Exception as exc:
        print(f"ASR device: Whisper CUDA unavailable, fallback to CPU ({exc})", flush=True)
        return transcribe_once("cpu", "int8")

try:
    if asr_engine == "sensevoice":
        emit(run_sensevoice())
    else:
        emit(run_whisper(whisper_model_name, asr_engine))
except Exception as exc:
    engine_name = "SenseVoice/FunASR" if asr_engine == "sensevoice" else "Whisper"
    raise RuntimeError(engine_name + " 识别失败：" + str(exc))
""";
}

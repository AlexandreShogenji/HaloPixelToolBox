using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Models;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Services;

public enum VoiceAgentPhase
{
    Stopped,
    Starting,
    LoadingModel,
    ListeningForWake,
    Acknowledging,
    ListeningForCommand,
    Recognizing,
    Executing,
    CoolingDown,
    Stopping,
    Error
}

public sealed record VoiceAgentSnapshot(
    VoiceAgentPhase Phase,
    string Status,
    string Detail,
    string LastTranscript,
    string LastResponse,
    bool IsRunning)
{
    public static VoiceAgentSnapshot Initial { get; } = new(
        VoiceAgentPhase.Stopped,
        "语音 Agent 未启动",
        "先检测语音环境，并在上方 DSH 配置中安装 PixelBar 插件。",
        string.Empty,
        string.Empty,
        false);
}

public sealed record VoiceAgentOptions(
    string PythonPath,
    string FfmpegPath,
    string InputDevice,
    int SensitivityIndex,
    string DshExecutablePath,
    string DshHomePath,
    string DshProfileName,
    int CommandTimeoutSeconds)
{
    public int CommandSilenceMilliseconds { get; init; } = 1800;
    public int CommandMaxSpeechSeconds { get; init; } = 60;

    public static VoiceAgentOptions FromProfile() => new(
        DisplayFeatureProfile.VoiceAgentPythonPath,
        DisplayFeatureProfile.VoiceAgentFfmpegPath,
        DisplayFeatureProfile.VoiceAgentInputDevice,
        DisplayFeatureProfile.VoiceAgentSensitivityIndex,
        DisplayFeatureProfile.DshExecutablePath,
        DisplayFeatureProfile.DshHomePath,
        DisplayFeatureProfile.DshProfileName,
        DisplayFeatureProfile.VoiceAgentCommandTimeoutSeconds)
    {
        CommandSilenceMilliseconds = DisplayFeatureProfile.VoiceAgentCommandSilenceMilliseconds,
        CommandMaxSpeechSeconds = DisplayFeatureProfile.VoiceAgentCommandMaxSpeechSeconds
    };
}

public sealed record VoiceAgentEnvironmentResult(
    bool Success,
    string Message,
    string Detail,
    string PythonPath,
    string FfmpegPath);

/// <summary>
/// Owns the long-running local microphone/ASR worker and forwards one recognized command at a
/// time to the shared DSH device session. The Python process emits JSONL only; UI code observes
/// immutable snapshots and never reads process streams directly.
/// </summary>
public sealed class VoiceAgentService : IDisposable
{
    private const int WorkerStartupTimeoutSeconds = 120;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim workerInputGate = new(1, 1);
    private readonly SemaphoreSlim taskSpeechGate = new(1, 1);
    private long taskPromptVersion;
    private long attentionPromptSequence;
    private sealed record SpokenTaskContext(long Version, DshTaskSnapshot Snapshot);
    private SpokenTaskContext? spokenTaskContext;
    private readonly object snapshotGate = new();
    private readonly object snapshotNotificationGate = new();
    private Process? workerProcess;
    private Process? terminalWorkerProcess;
    private CancellationTokenSource? workerCancellation;
    private Task? workerEventPumpTask;
    private Task? workerErrorPumpTask;
    private Task? workerMonitorTask;
    private TaskCompletionSource<bool>? readySource;
    private VoiceAgentOptions? activeOptions;
    private string? activeEnvironmentFingerprint;
    private VoiceAgentSnapshot current = VoiceAgentSnapshot.Initial;
    private bool stopRequested;
    private bool disposed;

    public event EventHandler<VoiceAgentSnapshot>? StatusChanged;

    public VoiceAgentSnapshot Current
    {
        get
        {
            lock (snapshotGate)
                return current;
        }
    }

    public bool IsRunning => Current.IsRunning;

    public async Task<VoiceAgentEnvironmentResult> TestEnvironmentAsync(
        VoiceAgentOptions options,
        CancellationToken cancellationToken = default)
        => await CheckEnvironmentAsync(options, runModelSelfTest: true, cancellationToken);

    private async Task<VoiceAgentEnvironmentResult> CheckEnvironmentAsync(
        VoiceAgentOptions options,
        bool runModelSelfTest,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveVoiceAssetPath("voice_agent_host.py");
        var replyPath = ResolveVoiceAssetPath("reply-xiaoxiao-loud.wav");
        var processingReplyPath = ResolveVoiceAssetPath("reply-received-xiaoxiao.wav");
        if (scriptPath is null)
            return EnvironmentFailure("缺少语音 Agent worker", "未找到 Assets\\VoiceAgent\\voice_agent_host.py");
        if (replyPath is null)
            return EnvironmentFailure("缺少唤醒回复音频", "未找到 Assets\\VoiceAgent\\reply-xiaoxiao-loud.wav");
        if (processingReplyPath is null)
            return EnvironmentFailure("缺少命令确认音频", "未找到 Assets\\VoiceAgent\\reply-received-xiaoxiao.wav");

        var modelRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "modelscope", "hub", "models", "iic");
        var modelPath = Path.Combine(modelRoot, "SenseVoiceSmall");
        var vadModelPath = Path.Combine(modelRoot, "speech_fsmn_vad_zh-cn-16k-common-pytorch");
        if (!Directory.Exists(modelPath) || !Directory.Exists(vadModelPath))
        {
            return EnvironmentFailure(
                "未找到本地语音模型",
                $"请确认 SenseVoiceSmall 与 FSMN VAD 已缓存到 {modelRoot}");
        }

        var python = await ResolvePythonAsync(options.PythonPath, cancellationToken);
        if (python is null)
        {
            return EnvironmentFailure(
                "未找到兼容的 Python",
                "需要能导入 funasr、numpy 与 torch 的 Python 3.11 环境；可在设置中填写完整 python.exe 路径。");
        }

        var ffmpeg = string.IsNullOrWhiteSpace(options.FfmpegPath) ? "ffmpeg" : options.FfmpegPath.Trim().Trim('"');
        var ffmpegCheck = await RunProcessAsync(
            ffmpeg,
            ["-version"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (!ffmpegCheck.Success)
        {
            return EnvironmentFailure(
                "FFmpeg 不可用",
                FirstNonEmptyLine(ffmpegCheck.StandardError)
                ?? FirstNonEmptyLine(ffmpegCheck.StandardOutput)
                ?? "请安装 FFmpeg 或填写 ffmpeg.exe 的完整路径。",
                python,
                ffmpeg);
        }

        if (runModelSelfTest)
        {
            var fingerprint = BuildVoiceEnvironmentFingerprint(
                scriptPath, replyPath, processingReplyPath, modelPath, vadModelPath);
            if (CanReuseLoadedEnvironment(options, python, ffmpeg, fingerprint))
            {
                return new VoiceAgentEnvironmentResult(
                    true,
                    "本地语音环境可用",
                    $"正在监听的 worker 已完成模型加载，本次复用已验证模型。Python：{python} · FFmpeg：{ffmpeg}",
                    python,
                    ffmpeg);
            }

            var selfTest = await RunProcessAsync(
                python,
                [
                    scriptPath,
                    "--self-test",
                    "--ffmpeg", ffmpeg,
                    "--reply", replyPath,
                    "--processing-reply", processingReplyPath,
                    "--model-path", modelPath,
                    "--vad-model-path", vadModelPath
                ],
                TimeSpan.FromSeconds(120),
                cancellationToken);
            if (!selfTest.Success)
            {
                return EnvironmentFailure(
                    "语音环境自检失败",
                    LastUsefulLine(selfTest.StandardError)
                    ?? LastUsefulLine(selfTest.StandardOutput)
                    ?? selfTest.Message,
                    python,
                    ffmpeg);
            }

            return new VoiceAgentEnvironmentResult(
                true,
                "本地语音环境可用",
                BuildSelfTestDetail(selfTest.StandardOutput, python, ffmpeg),
                python,
                ffmpeg);
        }

        return new VoiceAgentEnvironmentResult(
            true,
            "语音环境可用",
            $"Python：{python} · FFmpeg：{ffmpeg}",
            python,
            ffmpeg);
    }

    public async Task<bool> StartFromProfileAsync(CancellationToken cancellationToken = default)
        => await StartAsync(VoiceAgentOptions.FromProfile(), cancellationToken);

    public async Task<bool> StartAsync(
        VoiceAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (workerProcess is { HasExited: false })
                return true;
            if (workerProcess is not null)
            {
                RequestWorkerStop();
                await CleanupWorkerAsync(workerProcess);
            }

            UpdateSnapshot(
                VoiceAgentPhase.Starting,
                "正在检查语音环境",
                "首次加载本地 SenseVoice 模型时会同时预热 DSH。",
                isRunning: true);

            // The worker performs the real model load and reports its ready state. Running the
            // full self-test here would load the 900 MB model twice on every start.
            var environment = await CheckEnvironmentAsync(options, runModelSelfTest: false, cancellationToken);
            if (!environment.Success)
            {
                UpdateSnapshot(VoiceAgentPhase.Error, environment.Message, environment.Detail, isRunning: false);
                return false;
            }

            var scriptPath = ResolveVoiceAssetPath("voice_agent_host.py")!;
            var replyPath = ResolveVoiceAssetPath("reply-xiaoxiao-loud.wav")!;
            var processingReplyPath = ResolveVoiceAssetPath("reply-received-xiaoxiao.wav")!;
            var sensitivity = GetSensitivitySettings(options.SensitivityIndex);
            var modelRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "modelscope", "hub", "models", "iic");
            var environmentFingerprint = BuildVoiceEnvironmentFingerprint(
                scriptPath,
                replyPath,
                processingReplyPath,
                Path.Combine(modelRoot, "SenseVoiceSmall"),
                Path.Combine(modelRoot, "speech_fsmn_vad_zh-cn-16k-common-pytorch"));

            var startInfo = new ProcessStartInfo
            {
                FileName = environment.PythonPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[]
            {
                scriptPath,
                "--ffmpeg", environment.FfmpegPath,
                "--input-device", options.InputDevice,
                "--reply", replyPath,
                "--processing-reply", processingReplyPath,
                "--input-gain-db", sensitivity.InputGainDb.ToString(CultureInfo.InvariantCulture),
                "--threshold-dbfs", sensitivity.ThresholdDbfs.ToString(CultureInfo.InvariantCulture),
                "--command-silence-ms", Math.Clamp(options.CommandSilenceMilliseconds, 800, 4000).ToString(CultureInfo.InvariantCulture),
                "--command-max-seconds", Math.Clamp(options.CommandMaxSpeechSeconds, 15, 120).ToString(CultureInfo.InvariantCulture),
                "--model-path", Path.Combine(modelRoot, "SenseVoiceSmall"),
                "--vad-model-path", Path.Combine(modelRoot, "speech_fsmn_vad_zh-cn-16k-common-pytorch")
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                UpdateSnapshot(VoiceAgentPhase.Error, "语音 Agent 启动失败", "无法创建 Python worker 进程。", isRunning: false);
                return false;
            }

            activeOptions = options with
            {
                PythonPath = environment.PythonPath,
                FfmpegPath = environment.FfmpegPath
            };
            activeEnvironmentFingerprint = environmentFingerprint;
            Interlocked.Exchange(ref taskPromptVersion, 0);
            Interlocked.Exchange(ref attentionPromptSequence, 0);
            Volatile.Write(ref spokenTaskContext, null);
            workerProcess = process;
            terminalWorkerProcess = null;
            workerCancellation = new CancellationTokenSource();
            readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopRequested = false;
            workerErrorPumpTask = PumpStandardErrorAsync(process, workerCancellation.Token);
            workerEventPumpTask = PumpWorkerEventsAsync(process, workerCancellation.Token);
            workerMonitorTask = MonitorWorkerExitAsync(process, workerCancellation);
            var deviceSessionTask = App.DshSessions.PrepareDeviceSessionAsync(workerCancellation.Token);

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(WorkerStartupTimeoutSeconds));
            try
            {
                await Task.WhenAll(readySource.Task, deviceSessionTask).WaitAsync(startupTimeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                RequestWorkerStop();
                TryKill(process);
                await CleanupWorkerAsync(process);
                UpdateSnapshot(
                    VoiceAgentPhase.Error,
                    "语音 Agent 启动超时",
                    $"语音环境或 DSH 会话服务在 {WorkerStartupTimeoutSeconds} 秒内未准备完成。",
                    isRunning: false);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            RequestWorkerStop();
            await CleanupWorkerAsync(workerProcess);
            UpdateSnapshot(VoiceAgentPhase.Stopped, "语音监听启动已取消", "麦克风当前未监听。", isRunning: false);
            throw;
        }
        catch (Exception exception)
        {
            RequestWorkerStop();
            await CleanupWorkerAsync(workerProcess);
            UpdateSnapshot(VoiceAgentPhase.Error, "语音 Agent 启动失败", exception.Message, isRunning: false);
            return false;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var process = workerProcess;
            RequestWorkerStop();
            if (process is null || process.HasExited)
            {
                await CleanupWorkerAsync(process);
                UpdateSnapshot(VoiceAgentPhase.Stopped, "语音 Agent 已停止", "麦克风当前未监听。", isRunning: false);
                return;
            }

            workerCancellation?.Cancel();
            UpdateSnapshot(VoiceAgentPhase.Stopping, "正在停止语音 Agent", "正在释放麦克风与识别模型。", isRunning: true);
            try
            {
                await WriteWorkerControlAsync(process, new { action = "stop" }, cancellationToken, allowStopping: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
            }
            catch
            {
                TryKill(process);
            }

            await CleanupWorkerAsync(process);
            UpdateSnapshot(VoiceAgentPhase.Stopped, "语音 Agent 已停止", "麦克风当前未监听。", isRunning: false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<bool> PlayAcknowledgementAsync(CancellationToken cancellationToken = default)
    {
        var replyPath = ResolveVoiceAssetPath("reply-xiaoxiao-loud.wav");
        if (replyPath is null)
            return false;

        return await Task.Run(
            () => PlaySound(replyPath, IntPtr.Zero, SoundFilename | SoundNoDefault | SoundSync),
            cancellationToken);
    }

    public Task NotifyTaskAsync(string cue, CancellationToken cancellationToken = default)
        => SendTaskNotificationAsync(cue, string.Empty, false, false, cancellationToken);

    public Task NotifyTaskAsync(string cue, string text, bool listenAfter, CancellationToken cancellationToken = default)
        => SendTaskNotificationAsync(cue, text, listenAfter, false, cancellationToken);

    public Task NotifyTaskAsync(string cue, string text, bool listenAfter, DshTaskSnapshot expectedContext,
        CancellationToken cancellationToken = default)
        => SendTaskNotificationAsync(cue, text, listenAfter, false, cancellationToken, expectedContext);

    public async Task InvalidateTaskPromptAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref taskPromptVersion);
        await taskSpeechGate.WaitAsync(cancellationToken);
        try
        {
            if (version != Interlocked.Read(ref taskPromptVersion)) return;
            var process = workerProcess;
            if (process is not null && IsActiveWorker(process, cancellationToken))
                await WriteWorkerControlAsync(process, new { action = "replace_prompt", version }, cancellationToken);
        }
        finally { taskSpeechGate.Release(); }
    }

    private async Task SendTaskNotificationAsync(string cue, string text, bool listenAfter, bool completeTurn,
        CancellationToken cancellationToken, DshTaskSnapshot? expectedContext = null)
    {
        if (cue is not ("task_started" or "task_progress" or "approval_required" or "input_required"
            or "task_completed" or "task_failed" or "task_cancelled")) return;
        var process = workerProcess;
        if (process is not null && IsActiveWorker(process, cancellationToken)
            && readySource?.Task.IsCompletedSuccessfully == true)
        {
            var taskSnapshot = CaptureTaskContext(expectedContext ?? App.DshTasks.Current);
            if (expectedContext is not null && !SameTaskContext(taskSnapshot, App.DshTasks.Current)) return;
            var version = string.IsNullOrWhiteSpace(text) && !listenAfter && !completeTurn
                ? 0 : Interlocked.Increment(ref taskPromptVersion);
            await taskSpeechGate.WaitAsync(cancellationToken);
            try
            {
                if (version != 0 && version != Interlocked.Read(ref taskPromptVersion)) return;
                if (expectedContext is not null && !SameTaskContext(taskSnapshot, App.DshTasks.Current)) return;
                if (version != 0) Volatile.Write(ref spokenTaskContext, new SpokenTaskContext(version, taskSnapshot));
                // Queue each logical prompt atomically. The worker splits it during synthesis;
                // separate queue entries could drop the final resume instruction on overflow.
                if (listenAfter) Interlocked.Increment(ref attentionPromptSequence);
                await WriteWorkerControlAsync(process, new
                {
                    action = "notify", id = Guid.NewGuid().ToString("D"), cue, text = BoundSpokenPrompt(text), version,
                    listen_after = listenAfter ? "command" : "wake",
                    complete_turn = completeTurn || cue is "task_completed" or "task_failed" or "task_cancelled"
                }, cancellationToken);
            }
            finally { taskSpeechGate.Release(); }
            return;
        }
        // Before ASR is ready there is no capture to suppress. Use the same default playback
        // device as the existing acknowledgement instead of changing system audio settings.
        var tone = CreateTaskToneFile(cue);
        await Task.Run(() => PlaySound(tone, IntPtr.Zero, SoundFilename | SoundNoDefault | SoundSync), cancellationToken);
    }

    private static string BoundSpokenPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        const int maximumCharacters = 60000; // Below the worker's 64 KiB character limit, including the notice.
        text = text.Trim();
        if (text.Length <= maximumCharacters) return text;
        var chunk = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (chunk.Length + rune.Utf16SequenceLength > maximumCharacters) break;
            chunk.Append(rune.ToString());
        }
        return chunk + "。内容过长，本次播报到这里，剩余内容保留在会话中。";
    }

    private static DshTaskSnapshot CaptureTaskContext(DshTaskSnapshot snapshot) => snapshot with
    {
        PendingInteractions = Array.AsReadOnly(snapshot.PendingInteractions.Select(DshTaskInteractionIdentity.Capture).ToArray()),
        VoiceAnswers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            snapshot.VoiceAnswers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
    };

    private static bool SameTaskContext(DshTaskSnapshot expected, DshTaskSnapshot actual)
        => expected.SessionId == actual.SessionId && expected.IsMonitoring == actual.IsMonitoring
            && expected.State == actual.State && expected.Detail == actual.Detail
            && expected.VoiceAnswerRevision == actual.VoiceAnswerRevision
            && expected.PendingInteractions.Count == actual.PendingInteractions.Count
            && expected.PendingInteractions.All(item => actual.PendingInteractions.Any(p => DshTaskInteractionIdentity.Matches(item, p)));

    private async Task WriteWorkerControlAsync(Process process, object message, CancellationToken token, bool allowStopping = false)
    {
        await workerInputGate.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(workerProcess, process) || process.HasExited || (!allowStopping && !IsActiveWorker(process, token)))
                return;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        finally { workerInputGate.Release(); }
    }

    private static string CreateTaskToneFile(string cue)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HaloPixelToolBox", "TaskCues");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, cue + ".wav");
        if (File.Exists(file)) return file;
        var notes = cue switch
        {
            "approval_required" or "input_required" => new[] { 880d, 660d, 880d },
            "task_completed" => new[] { 660d, 880d, 1100d },
            "task_failed" => new[] { 660d, 440d, 330d },
            "task_cancelled" => new[] { 660d, 440d },
            _ => new[] { 660d, 880d }
        };
        const int sampleRate = 16000;
        const int noteSamples = 2240;
        const int pauseSamples = 640;
        var sampleCount = notes.Length * (noteSamples + pauseSamples);
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + sampleCount * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(sampleCount * 2);
            foreach (var frequency in notes)
                for (var i = 0; i < noteSamples + pauseSamples; i++)
                {
                    var envelope = i < noteSamples ? Math.Min(1d, Math.Min(i / 160d, (noteSamples - i) / 160d)) : 0;
                    writer.Write((short)(6000 * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate)));
                }
        }
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, memory.ToArray());
        File.Move(temporary, file, true);
        return file;
    }

    private async Task PumpWorkerEventsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = GetString(root, "type");
                    if (string.Equals(type, "state", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleWorkerState(process, cancellationToken, GetString(root, "state"), GetString(root, "message"));
                    }
                    else if (string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = GetString(root, "text")?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var hasVersion = root.TryGetProperty("prompt_version", out var versionProperty)
                                && versionProperty.ValueKind == JsonValueKind.Number && versionProperty.TryGetInt64(out _);
                            var version = hasVersion ? versionProperty.GetInt64() : -1;
                            var fromPrompt = root.TryGetProperty("from_prompt", out var fromPromptProperty)
                                && fromPromptProperty.ValueKind == JsonValueKind.True;
                            var context = Volatile.Read(ref spokenTaskContext);
                            var taskAtReceipt = CaptureTaskContext(App.DshTasks.Current);
                            if ((hasVersion && version != Interlocked.Read(ref taskPromptVersion))
                                || (taskAtReceipt.NeedsAttention && (!hasVersion || version <= 0 || context?.Version != version)))
                            {
                                var task = App.DshTasks.Current;
                                var warning = "问题已经变化，这段回答没有提交。"
                                    + (task.NeedsAttention ? task.Detail : "请重新说出指令。");
                                UpdateSnapshot(VoiceAgentPhase.CoolingDown, "旧问题的语音回答未提交", warning,
                                    lastTranscript: text, lastResponse: warning, isRunning: true,
                                    expectedWorker: process, workerToken: cancellationToken);
                                await DshTaskFeedback.PublishVoiceReplyAsync(warning, cancellationToken, isTaskReply: true);
                                await SendTaskNotificationAsync("input_required", warning, task.NeedsAttention, true, cancellationToken);
                            }
                            else await ExecuteCommandAsync(process, text,
                                (fromPrompt || taskAtReceipt.NeedsAttention) && context?.Version == version
                                    ? context.Snapshot : taskAtReceipt, cancellationToken);
                        }
                    }
                    else if (string.Equals(type, "notification", StringComparison.OrdinalIgnoreCase)
                        && GetString(root, "stage") == "failed")
                    {
                        UpdateSnapshot(VoiceAgentPhase.CoolingDown, "语音提示未完整播放",
                            GetString(root, "error") ?? "请查看字幕屏或会话中的任务提示。",
                            isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                    }
                    else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        var message = GetString(root, "message") ?? "Python worker 返回未知错误";
                        if (UpdateSnapshot(VoiceAgentPhase.Error, "语音 Agent 运行失败", message,
                            isRunning: false, expectedWorker: process, workerToken: cancellationToken))
                            readySource?.TrySetException(new InvalidOperationException(message));
                    }
                }
                catch (JsonException exception)
                {
                    UpdateSnapshot(
                        VoiceAgentPhase.Error,
                        "语音 Agent 协议错误",
                        $"无法解析 worker 输出：{exception.Message}",
                        isRunning: true,
                        expectedWorker: process,
                        workerToken: cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (UpdateSnapshot(VoiceAgentPhase.Error, "语音 Agent 通信中断", exception.Message,
                isRunning: false, expectedWorker: process, workerToken: cancellationToken))
            {
                readySource?.TrySetException(exception);
            }
        }
    }

    private async Task ExecuteCommandAsync(Process process, string command, DshTaskSnapshot expectedTask,
        CancellationToken cancellationToken)
    {
        var options = activeOptions;
        if (options is null || !IsActiveWorker(process, cancellationToken))
            return;

        UpdateSnapshot(
            VoiceAgentPhase.Executing,
            "正在确认指令与目标会话",
            command,
            lastTranscript: command,
            isRunning: true,
            expectedWorker: process,
            workerToken: cancellationToken);
        var attentionAtStart = Interlocked.Read(ref attentionPromptSequence);
        var notificationOwnsResume = false;
        try
        {
            var routed = await App.DshTasks.RouteVoiceAsync(command, expectedTask, cancellationToken);
            if (routed.Handled)
            {
                UpdateSnapshot(VoiceAgentPhase.CoolingDown, "任务交互已处理", routed.Message,
                    lastTranscript: command, lastResponse: routed.Message, isRunning: true,
                    expectedWorker: process, workerToken: cancellationToken);
                // The monitor can discover the next question while this route is completing.
                // Its current question already owns playback/listening; do not replace it with
                // an older "submitted" reply or a second copy of the same options.
                if (Interlocked.Read(ref attentionPromptSequence) != attentionAtStart
                    && App.DshTasks.Current.NeedsAttention)
                {
                    notificationOwnsResume = true;
                    return;
                }
                var task = App.DshTasks.Current;
                var needsNextAnswer = task.NeedsAttention && !routed.ListenForReply;
                var reply = needsNextAnswer ? task.Detail : routed.Message;
                var listenForReply = routed.ListenForReply || needsNextAnswer;
                await DshTaskFeedback.PublishVoiceReplyAsync(reply, cancellationToken, isTaskReply: true);
                await SendTaskNotificationAsync(listenForReply ? "input_required" : "task_progress",
                    reply, listenForReply, true, cancellationToken);
                notificationOwnsResume = true;
                return;
            }
            // Only positively identified device commands may enter its fixed persona.
            if (!DshTaskVoiceParser.IsDeviceCommand(command))
            {
                UpdateSnapshot(VoiceAgentPhase.CoolingDown, "请确认会话去向",
                    "请说明是控制音箱，还是新建或继续 DSH 任务。",
                    lastTranscript: command, lastResponse: "未确认目标会话，未发送指令。", isRunning: true,
                    expectedWorker: process, workerToken: cancellationToken);
                const string clarification = "未发送指令。请说明是控制音箱，还是新建或继续 DSH 任务。";
                await DshTaskFeedback.PublishVoiceReplyAsync(clarification, cancellationToken, isTaskReply: true);
                await SendTaskNotificationAsync("input_required", clarification, true, true, cancellationToken);
                notificationOwnsResume = true;
                return;
            }
            var result = await App.DshSessions.ExecuteDeviceCommandAsync(
                command,
                Math.Clamp(options.CommandTimeoutSeconds, 30, 300),
                cancellationToken);
            var response = result.Success
                ? result.FinalText
                : result.Message;
            var status = result.Success
                ? result.CalledTools.Count > 0 ? "DSH 已完成命令" : "DSH 已回复"
                : "DSH 执行失败";
            UpdateSnapshot(
                result.Success ? VoiceAgentPhase.CoolingDown : VoiceAgentPhase.Error,
                status,
                string.IsNullOrWhiteSpace(response) ? result.Message : response,
                lastTranscript: command,
                lastResponse: response,
                isRunning: true,
                expectedWorker: process,
                workerToken: cancellationToken);
            var spokenResponse = string.IsNullOrWhiteSpace(response) ? result.Message : response;
            if (Interlocked.Read(ref attentionPromptSequence) != attentionAtStart
                && App.DshTasks.Current.NeedsAttention)
            {
                // A task question arrived while the device command was running. Its prompt
                // owns direct listening; an older device result must not cancel that question.
                notificationOwnsResume = true;
                return;
            }
            await DshTaskFeedback.PublishVoiceReplyAsync(spokenResponse, cancellationToken);
            await SendTaskNotificationAsync(result.Success ? "task_completed" : "task_failed",
                spokenResponse, false, true, cancellationToken);
            notificationOwnsResume = true;
        }
        catch (OperationCanceledException)
        {
            UpdateSnapshot(VoiceAgentPhase.Error, "已停止等待 DSH 结果",
                "已接受的设备指令可能仍在执行，请查看音箱控制会话。",
                lastTranscript: command, isRunning: true, expectedWorker: process, workerToken: cancellationToken);
        }
        catch (Exception exception)
        {
            UpdateSnapshot(VoiceAgentPhase.Error, "DSH 执行失败", exception.Message,
                lastTranscript: command, isRunning: true, expectedWorker: process, workerToken: cancellationToken);
            try
            {
                var failure = "这次指令未能确认完成，请查看会话状态后重试。";
                await DshTaskFeedback.PublishVoiceReplyAsync(failure, cancellationToken, isTaskReply: true);
                await SendTaskNotificationAsync("task_failed", failure, false, true, cancellationToken);
                notificationOwnsResume = true;
            }
            catch (Exception playbackException) { Debug.WriteLine($"[VoiceAgent] {playbackException.Message}"); }
        }
        finally
        {
            if (!notificationOwnsResume && IsActiveWorker(process, cancellationToken))
            {
                try
                {
                    await WriteWorkerControlAsync(process, new { action = "resume" }, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    UpdateSnapshot(VoiceAgentPhase.Error, "无法恢复唤醒监听", exception.Message,
                        isRunning: false, expectedWorker: process, workerToken: cancellationToken);
                }
            }
        }
    }

    private void HandleWorkerState(Process process, CancellationToken cancellationToken, string? state, string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "语音 worker 状态已更新。" : message;
        switch (state?.ToLowerInvariant())
        {
            case "loading":
                UpdateSnapshot(VoiceAgentPhase.LoadingModel, "正在加载本地语音模型", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "ready":
                if (UpdateSnapshot(VoiceAgentPhase.ListeningForWake, "正在等待“花再花再”", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken))
                    readySource?.TrySetResult(true);
                break;
            case "wake_detected":
                UpdateSnapshot(VoiceAgentPhase.Acknowledging, "已唤醒，正在回复“我在”", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "speaking_task":
                UpdateSnapshot(VoiceAgentPhase.Acknowledging, "正在播报任务提示", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "listening_command":
                UpdateSnapshot(VoiceAgentPhase.ListeningForCommand, "请说出设备或 DSH 指令", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "recognizing":
                UpdateSnapshot(VoiceAgentPhase.Recognizing, "正在识别指令", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "command_rejected":
                UpdateSnapshot(VoiceAgentPhase.CoolingDown, "本次指令未提交", detail,
                    lastResponse: detail, isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "waiting_resume":
                UpdateSnapshot(VoiceAgentPhase.Executing, "正在等待 DSH 完成", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
            case "cooldown":
                UpdateSnapshot(VoiceAgentPhase.CoolingDown, "正在避免音箱回声误触发", detail,
                    isRunning: true, expectedWorker: process, workerToken: cancellationToken);
                break;
        }
    }

    private async Task PumpStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                if (!string.IsNullOrWhiteSpace(line))
                    Debug.WriteLine($"[VoiceAgent] {line}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MonitorWorkerExitAsync(Process process, CancellationTokenSource cancellation)
    {
        try
        {
            await process.WaitForExitAsync();
            if (MarkWorkerTerminal(process))
            {
                // Cancel the command token before publishing a terminal state. A DSH
                // run belongs to this worker and must not continue after it exits.
                cancellation.Cancel();
                readySource?.TrySetException(new InvalidOperationException($"语音 worker 已退出（{process.ExitCode}）"));
                UpdateSnapshot(
                    VoiceAgentPhase.Error,
                    "语音 Agent 意外停止",
                    $"Python worker 退出码：{process.ExitCode}",
                    isRunning: false,
                    expectedWorker: process,
                    allowExitedWorker: true);
            }
        }
        catch (Exception exception)
        {
            if (MarkWorkerTerminal(process))
            {
                cancellation.Cancel();
                UpdateSnapshot(VoiceAgentPhase.Error, "语音 Agent 状态检查失败", exception.Message,
                    isRunning: false, expectedWorker: process, allowExitedWorker: true);
            }
        }
    }

    private bool MarkWorkerTerminal(Process process)
    {
        lock (snapshotGate)
        {
            if (stopRequested || !ReferenceEquals(workerProcess, process))
                return false;
            terminalWorkerProcess = process;
            return true;
        }
    }

    private void RequestWorkerStop()
    {
        lock (snapshotGate)
            stopRequested = true;
    }

    private bool IsActiveWorker(Process process, CancellationToken cancellationToken)
    {
        if (stopRequested || cancellationToken.IsCancellationRequested
            || !ReferenceEquals(workerProcess, process)
            || ReferenceEquals(terminalWorkerProcess, process))
            return false;
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool CanReuseLoadedEnvironment(VoiceAgentOptions options, string python, string ffmpeg, string? fingerprint)
    {
        lock (snapshotGate)
        {
            if (workerProcess is not { } process || activeOptions is not { } active
                || workerCancellation is not { } cancellation
                || !IsActiveWorker(process, cancellation.Token)
                || readySource?.Task.IsCompletedSuccessfully != true
                || fingerprint is null || activeEnvironmentFingerprint is null
                || !string.Equals(fingerprint, activeEnvironmentFingerprint, StringComparison.Ordinal)
                || !current.IsRunning
                || current.Phase is VoiceAgentPhase.Starting or VoiceAgentPhase.LoadingModel
                    or VoiceAgentPhase.Stopped or VoiceAgentPhase.Stopping or VoiceAgentPhase.Error)
                return false;

            static string NormalizeExecutable(string value)
                => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));

            return string.Equals(NormalizeExecutable(python), NormalizeExecutable(active.PythonPath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeExecutable(ffmpeg), NormalizeExecutable(active.FfmpegPath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(options.InputDevice.Trim(), active.InputDevice.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? BuildVoiceEnvironmentFingerprint(
        string scriptPath,
        string replyPath,
        string processingReplyPath,
        string modelPath,
        string vadModelPath)
    {
        try
        {
            // Only read metadata, even for the large model weights. Include model
            // config/tokenizer files as well as weights so an in-place update
            // cannot reuse the old worker's validation accidentally.
            var paths = new[] { scriptPath, replyPath, processingReplyPath }
                .Concat(Directory.EnumerateFiles(modelPath, "*", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(vadModelPath, "*", SearchOption.AllDirectories))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            var fingerprint = new StringBuilder();
            foreach (var path in paths)
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                    return null;
                fingerprint.Append(path).Append('|')
                    .Append(file.Length).Append('|')
                    .Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return fingerprint.ToString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[VoiceAgent] 无法核对已加载环境文件：{exception.Message}");
            return null;
        }
    }

    private async Task<string?> ResolvePythonAsync(string configuredPath, CancellationToken cancellationToken)
    {
        var localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(localPrograms, "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(localPrograms, "Programs", "Python", "Python312", "python.exe"),
            "python3.11",
            "python"
        };
        foreach (var rawCandidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Environment.ExpandEnvironmentVariables(rawCandidate.Trim().Trim('"'));
            var result = await RunProcessAsync(
                candidate,
                ["-c", "import importlib.util,sys; assert all(importlib.util.find_spec(x) for x in ('funasr','numpy','torch')); print(sys.executable)"],
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (result.Success)
                return FirstNonEmptyLine(result.StandardOutput) ?? candidate;
        }

        return null;
    }

    private bool UpdateSnapshot(
        VoiceAgentPhase phase,
        string status,
        string detail,
        string? lastTranscript = null,
        string? lastResponse = null,
        bool? isRunning = null,
        Process? expectedWorker = null,
        CancellationToken workerToken = default,
        bool allowExitedWorker = false)
    {
        // Keep notifications in the same order as snapshot writes. A command
        // completion must not enqueue an older running state after an exit error.
        lock (snapshotNotificationGate)
        {
            VoiceAgentSnapshot next;
            lock (snapshotGate)
            {
                if (expectedWorker is not null
                    && (stopRequested || !ReferenceEquals(workerProcess, expectedWorker)
                        || (!allowExitedWorker && !IsActiveWorker(expectedWorker, workerToken))))
                    return false;
                next = current with
                {
                    Phase = phase,
                    Status = status,
                    Detail = detail,
                    LastTranscript = lastTranscript ?? current.LastTranscript,
                    LastResponse = lastResponse ?? current.LastResponse,
                    IsRunning = isRunning ?? current.IsRunning
                };
                // Repeated background wake checks do not change the UI. Keep
                // the current identity so queued notifications stay valid.
                if (next == current)
                    return true;
                current = next;
            }

            var handlers = StatusChanged;
            if (handlers is null)
                return true;
            foreach (EventHandler<VoiceAgentSnapshot> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, next);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[VoiceAgent] 状态订阅者处理失败：{exception.Message}");
                }
            }
            return true;
        }
    }

    private async Task CleanupWorkerAsync(Process? process)
    {
        var cancellation = workerCancellation;
        cancellation?.Cancel();
        if (process is not null)
            TryKill(process);

        var backgroundTasks = new[]
        {
            workerEventPumpTask,
            workerErrorPumpTask,
            workerMonitorTask
        }.Where(task => task is not null).Cast<Task>().Distinct().ToArray();
        if (backgroundTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(backgroundTasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("[VoiceAgent] 等待语音后台任务退出超时。");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[VoiceAgent] 语音后台任务退出异常：{exception.Message}");
            }
        }

        cancellation?.Dispose();
        workerCancellation = null;
        workerEventPumpTask = null;
        workerErrorPumpTask = null;
        workerMonitorTask = null;
        if (ReferenceEquals(workerProcess, process))
        {
            workerProcess = null;
            terminalWorkerProcess = null;
        }
        activeOptions = null;
        activeEnvironmentFingerprint = null;
        readySource = null;
        process?.Dispose();
    }

    private static VoiceAgentEnvironmentResult EnvironmentFailure(
        string message,
        string detail,
        string pythonPath = "",
        string ffmpegPath = "")
        => new(false, message, detail, pythonPath, ffmpegPath);

    private static string? ResolveVoiceAssetPath(string fileName)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceAgent", fileName);
        if (File.Exists(packaged))
            return packaged;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 9 && directory is not null; level++, directory = directory.Parent)
        {
            var source = Path.Combine(
                directory.FullName,
                "src",
                "HaloPixelToolBox",
                "Assets",
                "VoiceAgent",
                fileName);
            if (File.Exists(source))
                return source;
        }

        return null;
    }

    private static VoiceSensitivitySettings GetSensitivitySettings(int index) => Math.Clamp(index, 0, 2) switch
    {
        0 => new(6, -48),
        2 => new(12, -58),
        _ => new(10, -52)
    };

    private static async Task<ProcessRunResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(startInfo);
            if (process is null)
                return ProcessRunResult.Failed("无法创建进程");

            // Keep draining until the owned process is reaped. Cancel the wait,
            // rather than the pipe readers, so cancellation cannot leave a child
            // running or unobserved read tasks behind.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
                var output = await outputTask.WaitAsync(timeoutSource.Token);
                var error = await errorTask.WaitAsync(timeoutSource.Token);
                return new ProcessRunResult(
                    process.ExitCode == 0,
                    process.ExitCode == 0 ? "命令执行成功" : $"退出码 {process.ExitCode}",
                    output,
                    error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await KillAndDrainProcessAsync(process, outputTask, errorTask);
                return ProcessRunResult.Failed($"进程在 {timeout.TotalSeconds:0} 秒后超时");
            }
            catch
            {
                await KillAndDrainProcessAsync(process, outputTask, errorTask);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ProcessRunResult.Failed(exception.Message);
        }
    }

    private static async Task KillAndDrainProcessAsync(Process process, Task<string> outputTask, Task<string> errorTask)
    {
        TryKill(process);
        var cleanupTask = Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
        try
        {
            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[VoiceAgent] 回收环境检测进程：{exception.Message}");
            // Pipe disposal can fault after the bounded wait. Observe that eventual
            // result without extending cancellation or application shutdown.
            _ = cleanupTask.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstNonEmptyLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string? LastUsefulLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => !line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase));

    private static string BuildSelfTestDetail(string output, string python, string ffmpeg)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(GetString(root, "type"), "state", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(GetString(root, "state"), "ready", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var modelSeconds = root.TryGetProperty("model_load_seconds", out var modelElement)
                    && modelElement.TryGetDouble(out var modelValue)
                    ? $"模型加载 {modelValue:0.0} 秒"
                    : "模型加载成功";
                var replySeconds = root.TryGetProperty("reply_seconds", out var replyElement)
                    && replyElement.TryGetDouble(out var replyValue)
                    ? $"提示音 {replyValue:0.00} 秒"
                    : "提示音可用";
                return $"{modelSeconds} · {replySeconds} · Python：{python} · FFmpeg：{ffmpeg}";
            }
            catch (JsonException)
            {
            }
        }

        return $"模型与提示音检查通过 · Python：{python} · FFmpeg：{ffmpeg}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        var stopped = false;
        try
        {
            stopped = StopAsync().Wait(TimeSpan.FromSeconds(20));
        }
        catch
        {
            if (workerProcess is not null)
                TryKill(workerProcess);
        }
        if (!stopped)
        {
            RequestWorkerStop();
            workerCancellation?.Cancel();
            if (workerProcess is not null)
                TryKill(workerProcess);
        }
        if (stopped)
            lifecycleGate.Dispose();
    }

    private const uint SoundSync = 0x0000;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundFilename = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

    private sealed record ProcessRunResult(bool Success, string Message, string StandardOutput, string StandardError)
    {
        public static ProcessRunResult Failed(string message) => new(false, message, string.Empty, string.Empty);
    }

    private sealed record VoiceSensitivitySettings(double InputGainDb, double ThresholdDbfs);
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

public sealed partial class DshIntegrationService : IDisposable
{
    private const string PluginPackageName = "dsh-halo-pixelbar-tools";
    private const string PluginVersion = "0.7.0";
    private const string PluginBundleFileName = "dsh-halo-pixelbar-tools-0.7.0.tgz";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly object preparedHeadlessProcessGate = new();
    private PreparedDshProcess? preparedHeadlessProcess;
    private bool disposed;

    public string FindLikelyExecutable(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (File.Exists(expanded))
                return expanded;
        }

        var names = new[]
        {
            "dsh.cmd", "dsh.exe", "dsh.ps1", "dsh",
            "npx.cmd", "npx.exe", "npx.ps1", "npx"
        };
        foreach (var directory in EnumerateCommandDirectories())
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(configuredPath) ? "npx" : configuredPath.Trim();
    }

    public async Task<DshOperationResult> TestDshAsync(
        string executable,
        string? dshHome,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var result = await RunDshAsync(
            executable,
            dshHome,
            ["--version"],
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120)),
            cancellationToken);
        if (!result.Success)
            return result;

        var version = FirstNonEmptyLine(result.StandardOutput) ?? "版本未知";
        return result with { Message = $"DSH 可用：{version}" };
    }

    public async Task<DshOperationResult> InstallOrUpdatePluginAsync(
        string executable,
        string? dshHome,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProfileName(profileName))
            return DshOperationResult.Failed("Profile 名称只能包含字母、数字、点、下划线和连字符");

        var bundlePath = ResolvePluginBundlePath();
        if (bundlePath is null)
            return DshOperationResult.Failed($"未找到内置插件包 {PluginBundleFileName}");

        string cachedBundlePath;
        try
        {
            var cacheDirectory = Path.Combine(
                ResolveDshHome(dshHome),
                "plugins-cache",
                PluginPackageName,
                PluginVersion);
            Directory.CreateDirectory(cacheDirectory);
            cachedBundlePath = Path.Combine(cacheDirectory, PluginBundleFileName);
            if (!Path.GetFullPath(bundlePath).Equals(
                    Path.GetFullPath(cachedBundlePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(bundlePath, cachedBundlePath, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            return DshOperationResult.Failed($"复制内置插件包到 DSH 缓存失败：{exception.Message}");
        }

        var existingProfile = await RunDshAsync(
            executable,
            dshHome,
            ["--profile", profileName, "--dump-config"],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        if (!existingProfile.Success)
        {
            var created = await RunDshAsync(
                executable,
                dshHome,
                ["--profile", profileName, "--from-default-profile", "web", "--dump-config"],
                TimeSpan.FromSeconds(90),
                cancellationToken);
            if (!created.Success)
                return created with { Message = $"创建 DSH Web profile 失败：{created.Message}" };
        }

        var originalDependencyReference = ReadProfilePluginDependencyReference(dshHome, profileName);
        var installedVersion = ReadInstalledPluginVersion(dshHome, profileName);
        var hasExistingPlugin = originalDependencyReference is not null || installedVersion is not null;
        if (hasExistingPlugin
            && !string.Equals(installedVersion, PluginVersion, StringComparison.OrdinalIgnoreCase))
        {
            var removed = await RunDshAsync(
                executable,
                dshHome,
                ["plugin", "--profile", profileName, "remove", PluginPackageName],
                InstallTimeout,
                cancellationToken);
            if (!removed.Success)
            {
                return removed with
                {
                    Message = $"移除旧版 PixelBar 插件失败：{removed.Message}"
                };
            }
        }

        var installed = await RunDshAsync(
            executable,
            dshHome,
            ["plugin", "--profile", profileName, "add", cachedBundlePath],
            InstallTimeout,
            cancellationToken);
        if (!installed.Success)
        {
            var rollbackMessage = await TryRestorePluginDependencyAsync(
                executable,
                dshHome,
                profileName,
                originalDependencyReference,
                cancellationToken);
            return installed with
            {
                Message = $"插件安装失败：{installed.Message}；{rollbackMessage}"
            };
        }

        var verified = await RunDshAsync(
            executable,
            dshHome,
            ["--profile", profileName, "--dump-config"],
            TimeSpan.FromSeconds(60),
            cancellationToken);
        if (!verified.Success
            || !verified.StandardOutput.Contains("halo-pixelbar-tools", StringComparison.OrdinalIgnoreCase))
        {
            return DshOperationResult.Failed("DSH 已执行安装，但组合配置中未发现 halo-pixelbar-tools");
        }

        installedVersion = ReadInstalledPluginVersion(dshHome, profileName);
        if (!string.Equals(installedVersion, PluginVersion, StringComparison.OrdinalIgnoreCase))
        {
            return DshOperationResult.Failed(
                $"DSH 已执行安装，但实际加载的插件版本为 {installedVersion ?? "未知"}，期望版本为 {PluginVersion}");
        }

        return verified with
        {
            Success = true,
            Message = $"PixelBar 插件 {PluginVersion} 已安装到 DSH profile：{profileName}；请重启 DSH 以载入新工具定义"
        };
    }

    public Task<DshOperationResult> InstallOrUpdateVoiceProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
        => InstallOrUpdateVoiceProfileAsync(
            FindLikelyExecutable(),
            null,
            profileName,
            cancellationToken);

    public async Task<DshOperationResult> InstallOrUpdateVoiceProfileAsync(
        string executable,
        string? dshHome,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProfileName(profileName))
            return DshOperationResult.Failed("Profile 名称只能包含字母、数字、点、下划线和连字符");

        profileName = profileName.Trim();
        var profileConfig = await RunDshAsync(
            executable,
            dshHome,
            ["--profile", profileName, "--dump-config"],
            TimeSpan.FromSeconds(45),
            cancellationToken);
        if (!profileConfig.Success)
        {
            profileConfig = await RunDshAsync(
                executable,
                dshHome,
                ["--profile", profileName, "--from-default-profile", "headless", "--dump-config"],
                TimeSpan.FromSeconds(90),
                cancellationToken);
            if (!profileConfig.Success)
            {
                return profileConfig with
                {
                    Message = $"创建 DSH Headless profile 失败：{profileConfig.Message}"
                };
            }
        }

        if (!IsHeadlessProfileConfig(profileConfig.StandardOutput))
        {
            return DshOperationResult.Failed(
                $"DSH profile“{profileName}”已存在，但不是 Headless profile；请为语音 Agent 使用独立的 Profile 名称");
        }

        var installed = await InstallOrUpdatePluginAsync(
            executable,
            dshHome,
            profileName,
            cancellationToken);
        return installed.Success
            ? installed with
            {
                Message = $"语音 Agent profile“{profileName}”已就绪，PixelBar 插件版本为 {PluginVersion}"
            }
            : installed;
    }

    public Task<DshHeadlessPromptResult> RunHeadlessPromptAsync(
        string profileName,
        string prompt,
        int timeoutSeconds = 150,
        CancellationToken cancellationToken = default)
        => RunHeadlessPromptAsync(
            FindLikelyExecutable(),
            null,
            profileName,
            prompt,
            timeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Starts a headless DSH process and leaves its stdin open so the next voice
    /// command can skip profile composition and plugin startup.
    /// </summary>
    public DshOperationResult PrepareHeadlessPrompt(string profileName)
        => PrepareHeadlessPrompt(FindLikelyExecutable(), null, profileName);

    /// <summary>
    /// Starts (or reuses) one prepared headless DSH process for the supplied
    /// executable, DSH home and profile. A preparation failure is returned to the
    /// caller and does not prevent RunHeadlessPromptAsync from using a cold process.
    /// </summary>
    public DshOperationResult PrepareHeadlessPrompt(
        string executable,
        string? dshHome,
        string profileName)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return DshOperationResult.Failed("请填写 DSH 可执行文件路径");

        if (!IsValidProfileName(profileName))
            return DshOperationResult.Failed("Profile 名称只能包含字母、数字、点、下划线和连字符");

        try
        {
            var identity = CreatePreparedDshIdentity(executable, dshHome, profileName);
            lock (preparedHeadlessProcessGate)
            {
                if (disposed)
                    return DshOperationResult.Failed("DSH 集成服务已经释放");

                if (preparedHeadlessProcess is not null
                    && preparedHeadlessProcess.Identity.Matches(identity)
                    && IsProcessRunning(preparedHeadlessProcess.Started.Process))
                {
                    return new DshOperationResult(
                        true,
                        0,
                        $"DSH Headless profile“{identity.ProfileName}”已在预热",
                        string.Empty,
                        string.Empty);
                }

                if (preparedHeadlessProcess is not null)
                {
                    DisposePreparedProcess(preparedHeadlessProcess);
                    preparedHeadlessProcess = null;
                }

                var startInfo = CreateDshStartInfo(
                    executable,
                    dshHome,
                    ["--profile", profileName.Trim(), "--json", "-"],
                    redirectStandardInput: true);
                var started = StartDshProcess(startInfo);
                preparedHeadlessProcess = new PreparedDshProcess(identity, started);
            }

            return new DshOperationResult(
                true,
                0,
                $"已启动 DSH Headless profile“{profileName.Trim()}”预热进程",
                string.Empty,
                string.Empty);
        }
        catch (Exception exception)
        {
            // Preparing is an optimization. The next command will still take the
            // normal cold-start path when no prepared process is cached.
            return DshOperationResult.Failed($"DSH 预热失败，将在执行命令时重试：{exception.Message}");
        }
    }

    /// <summary>
    /// Stops and drains the cached headless process, if any. The service remains
    /// reusable and may be prepared again later.
    /// </summary>
    public void DiscardPreparedHeadlessPrompt()
    {
        PreparedDshProcess? prepared;
        lock (preparedHeadlessProcessGate)
        {
            prepared = preparedHeadlessProcess;
            preparedHeadlessProcess = null;
        }

        if (prepared is not null)
            DisposePreparedProcess(prepared);
    }

    public void Dispose()
    {
        PreparedDshProcess? prepared;
        lock (preparedHeadlessProcessGate)
        {
            if (disposed)
                return;

            disposed = true;
            prepared = preparedHeadlessProcess;
            preparedHeadlessProcess = null;
        }

        if (prepared is not null)
            DisposePreparedProcess(prepared);
    }

    public async Task<DshHeadlessPromptResult> RunHeadlessPromptAsync(
        string executable,
        string? dshHome,
        string profileName,
        string prompt,
        int timeoutSeconds = 150,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidProfileName(profileName))
            return DshHeadlessPromptResult.Failed("Profile 名称只能包含字母、数字、点、下划线和连字符");

        if (string.IsNullOrWhiteSpace(prompt))
            return DshHeadlessPromptResult.Failed("语音指令不能为空");

        var effectivePrompt = BuildVoiceAgentPrompt(prompt);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 300));
        PreparedDshProcess? prepared = null;
        try
        {
            var identity = CreatePreparedDshIdentity(executable, dshHome, profileName);
            prepared = TakePreparedHeadlessProcess(identity);
        }
        catch
        {
            // Preserve RunDshAsync's normal validation/error result for malformed
            // executable or home paths instead of making the cache optimization fatal.
        }

        DshOperationResult operation;
        if (prepared is not null)
        {
            var completion = await CompleteStartedDshProcessAsync(
                prepared.Started,
                timeout,
                cancellationToken,
                effectivePrompt);
            if (!completion.StandardInputWriteStarted)
            {
                // A prepared process can exit while idle (for example when its
                // profile is invalid). No prompt was dispatched, so retrying with
                // a fresh process cannot duplicate a device command.
                operation = await RunDshAsync(
                    executable,
                    dshHome,
                    ["--profile", profileName.Trim(), "--json", "-"],
                    timeout,
                    cancellationToken,
                    effectivePrompt);
            }
            else
            {
                operation = completion.Operation;
            }
        }
        else
        {
            operation = await RunDshAsync(
                executable,
                dshHome,
                ["--profile", profileName.Trim(), "--json", "-"],
                timeout,
                cancellationToken,
                effectivePrompt);
        }

        // DSH normally emits JSONL on stdout, while npm notices use stderr.
        // Some Windows batch launchers can swap those streams, so inspect both.
        var events = ParseHeadlessEvents($"{operation.StandardOutput}\n{operation.StandardError}");
        var finalText = FirstNonBlank(events.FinalText, events.LastUsableMessage);
        var eventError = events.Errors.Count == 0
            ? null
            : string.Join("；", events.Errors.Distinct(StringComparer.Ordinal));

        if (!operation.Success)
        {
            var message = eventError is null
                ? operation.Message
                : $"{eventError}（{operation.Message}）";
            return new DshHeadlessPromptResult(
                false,
                message,
                finalText ?? string.Empty,
                operation.ExitCode,
                operation.StandardOutput,
                operation.StandardError)
            {
                Errors = events.Errors,
                CalledTools = events.CalledTools,
                SuccessfulTools = events.SuccessfulTools
            };
        }

        if (string.IsNullOrWhiteSpace(events.FinalText) && eventError is not null)
        {
            return new DshHeadlessPromptResult(
                false,
                eventError,
                finalText ?? string.Empty,
                operation.ExitCode,
                operation.StandardOutput,
                operation.StandardError)
            {
                Errors = events.Errors,
                CalledTools = events.CalledTools,
                SuccessfulTools = events.SuccessfulTools
            };
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            if (events.SawFinal)
                finalText = "DSH 已完成命令";
            else
                return new DshHeadlessPromptResult(
                    false,
                    $"DSH 已退出，但没有返回可用的 JSONL 事件（stdout {operation.StandardOutput.Length} 字符，stderr {operation.StandardError.Length} 字符）",
                    string.Empty,
                    operation.ExitCode,
                    operation.StandardOutput,
                    operation.StandardError)
                {
                    Errors = events.Errors,
                    CalledTools = events.CalledTools,
                    SuccessfulTools = events.SuccessfulTools
                };
        }

        return new DshHeadlessPromptResult(
            true,
            finalText,
            finalText,
            operation.ExitCode,
            operation.StandardOutput,
            operation.StandardError)
        {
            Errors = events.Errors,
            CalledTools = events.CalledTools,
            SuccessfulTools = events.SuccessfulTools
        };
    }

    public async Task<DshOperationResult> VerifyBridgeAsync(
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 120)));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                DeviceControlPipeServer.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            var request = JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString("N"),
                method = "get_status",
                parameters = new { }
            });
            await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
                return DshOperationResult.Failed("设备桥接未返回响应");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                return DshOperationResult.Failed(error.GetProperty("message").GetString() ?? "设备桥接返回错误");

            var result = root.GetProperty("result");
            var message = GetJsonString(result, "message");
            var status = GetJsonString(result, "status");
            var code = GetJsonString(result, "code");
            if (!result.TryGetProperty("success", out var successElement)
                || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CreateBridgeFailure(
                    "设备桥接返回了无效的 success 字段",
                    status,
                    code,
                    line);
            }

            if (!successElement.GetBoolean())
            {
                return CreateBridgeFailure(
                    message ?? "设备桥接返回失败",
                    status,
                    code,
                    line);
            }

            if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(code, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBridgeFailure(
                    message ?? "设备桥接返回了不一致的成功状态",
                    status,
                    code,
                    line);
            }

            var connected = result.TryGetProperty("data", out var data)
                            && data.TryGetProperty("isConnected", out var connectedElement)
                            && connectedElement.GetBoolean();
            return new DshOperationResult(
                true,
                0,
                connected ? "设备桥接正常，PixelBar 已连接" : "设备桥接正常，PixelBar 当前未连接",
                message ?? line,
                string.Empty)
            {
                Status = status,
                Code = code
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DshOperationResult.Failed("设备桥接验证超时");
        }
        catch (Exception exception)
        {
            return DshOperationResult.Failed($"设备桥接验证失败：{exception.Message}");
        }
    }

    public string? ResolvePluginBundlePath()
    {
        var packaged = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Integrations",
            "DeepSeekHarness",
            PluginBundleFileName);
        if (File.Exists(packaged))
            return packaged;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
        {
            var source = Path.Combine(
                directory.FullName,
                "HaloPixelToolBox",
                "HaloPixelToolBox",
                "Assets",
                "Integrations",
                "DeepSeekHarness",
                PluginBundleFileName);
            if (File.Exists(source))
                return source;
        }

        return null;
    }

    public static bool IsValidProfileName(string profileName)
        => !string.IsNullOrWhiteSpace(profileName) && ProfileNameRegex().IsMatch(profileName.Trim());

    private static string? ReadInstalledPluginVersion(string? dshHome, string profileName)
    {
        try
        {
            var packagePath = Path.Combine(
                ResolveDshHome(dshHome),
                "profiles",
                profileName,
                "node_modules",
                PluginPackageName,
                "package.json");
            if (!File.Exists(packagePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProfilePluginDependencyReference(string? dshHome, string profileName)
    {
        try
        {
            var packagePath = Path.Combine(
                ResolveDshHome(dshHome),
                "profiles",
                profileName,
                "package.json");
            if (!File.Exists(packagePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object
                || !dependencies.TryGetProperty(PluginPackageName, out var dependency)
                || dependency.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return dependency.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryRestorePluginDependencyAsync(
        string executable,
        string? dshHome,
        string profileName,
        string? originalDependencyReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalDependencyReference))
            return "未找到原插件依赖引用，无法自动回滚";

        var restored = await RunDshAsync(
            executable,
            dshHome,
            ["plugin", "--profile", profileName, "add", originalDependencyReference],
            InstallTimeout,
            cancellationToken);
        return restored.Success
            ? $"已回滚到原插件依赖：{originalDependencyReference}"
            : $"自动回滚原插件依赖失败：{restored.Message}";
    }

    private static string ResolveDshHome(string? dshHome)
    {
        var configuredHome = !string.IsNullOrWhiteSpace(dshHome)
            ? dshHome
            : Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredHome.Trim().Trim('"')));
    }

    private static async Task<DshOperationResult> RunDshAsync(
        string executable,
        string? dshHome,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return DshOperationResult.Failed("请填写 DSH 可执行文件路径");

        try
        {
            var startInfo = CreateDshStartInfo(
                executable,
                dshHome,
                arguments,
                redirectStandardInput: standardInput is not null);
            var started = StartDshProcess(startInfo);
            var completion = await CompleteStartedDshProcessAsync(
                started,
                timeout,
                cancellationToken,
                standardInput);
            return completion.Operation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DshOperationResult.Failed($"无法运行 DSH：{exception.Message}");
        }
    }

    internal static ProcessStartInfo CreateDshStartInfo(
        string executable,
        string? dshHome,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput)
    {
        var command = DshExecutableResolver.Resolve(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (redirectStandardInput)
            startInfo.StandardInputEncoding = Utf8NoBom;
        if (IsNpxCommand(command))
        {
            startInfo.ArgumentList.Add("@deepseek-ai/dsh");
            // Opening the installed host and probing its version should prefer
            // npm's existing cache. Missing packages may still be fetched; keep
            // explicit npm environment preferences and install/update behavior.
            if ((arguments.Count == 1 && arguments[0] == "--version") || arguments.Contains("--no-open"))
            {
                if (!startInfo.Environment.Keys.Any(key => key.Equals("npm_config_prefer_offline", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("npm_config_prefer_online", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("npm_config_offline", StringComparison.OrdinalIgnoreCase)))
                    startInfo.Environment["npm_config_prefer_offline"] = "true";
            }
        }

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (!string.IsNullOrWhiteSpace(dshHome))
        {
            var home = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dshHome.Trim().Trim('"')));
            Directory.CreateDirectory(home);
            startInfo.Environment["DSH_HOME"] = home;
        }

        return startInfo;
    }

    private static StartedDshProcess StartDshProcess(ProcessStartInfo startInfo)
    {
        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 DSH 进程");

            // Drain both pipes from process start. A prepared DSH process can emit
            // enough startup diagnostics to fill a pipe before it receives stdin.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            return new StartedDshProcess(process, outputTask, errorTask);
        }
        catch
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }

            throw;
        }
    }

    private static async Task<DshProcessCompletion> CompleteStartedDshProcessAsync(
        StartedDshProcess started,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput)
    {
        using var process = started.Process;
        var standardInputWriteStarted = false;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            if (standardInput is not null)
            {
                if (!IsProcessRunning(process))
                {
                    await WaitForExitAndDrainAsync(process, started.OutputTask, started.ErrorTask);
                    return new DshProcessCompletion(
                        CreateExitedBeforeInputResult(process, started.OutputTask, started.ErrorTask),
                        false);
                }

                // DSH reads the stdin task as a line. EOF without a terminating
                // newline produces an empty task on the Windows npx.cmd path.
                // Once this write starts, do not retry automatically: the command
                // may already have reached DSH and a retry could duplicate it.
                standardInputWriteStarted = true;
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), timeoutSource.Token);
                await process.StandardInput.FlushAsync(timeoutSource.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeoutSource.Token);
            // A launcher can exit while a child still owns its redirected pipes.
            // Bound stream draining by the same deadline as process execution.
            var output = await started.OutputTask.WaitAsync(timeoutSource.Token);
            var error = await started.ErrorTask.WaitAsync(timeoutSource.Token);
            var message = process.ExitCode == 0
                ? FirstNonEmptyLine(output) ?? "命令执行成功"
                : FirstNonEmptyLine(error) ?? FirstNonEmptyLine(output) ?? $"DSH 退出码 {process.ExitCode}";
            return new DshProcessCompletion(
                new DshOperationResult(process.ExitCode == 0, process.ExitCode, message, output, error),
                standardInputWriteStarted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitAndDrainAsync(process, started.OutputTask, started.ErrorTask);
            return new DshProcessCompletion(
                new DshOperationResult(
                    false,
                    -1,
                    $"DSH 命令在 {timeout.TotalSeconds:0} 秒后超时",
                    GetCompletedOutput(started.OutputTask),
                    GetCompletedOutput(started.ErrorTask)),
                standardInputWriteStarted);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitAndDrainAsync(process, started.OutputTask, started.ErrorTask);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            await WaitForExitAndDrainAsync(process, started.OutputTask, started.ErrorTask);
            return new DshProcessCompletion(
                new DshOperationResult(
                    false,
                    -1,
                    $"无法运行 DSH：{exception.Message}",
                    GetCompletedOutput(started.OutputTask),
                    GetCompletedOutput(started.ErrorTask)),
                standardInputWriteStarted);
        }
    }

    private PreparedDshProcess? TakePreparedHeadlessProcess(PreparedDshIdentity identity)
    {
        PreparedDshProcess? prepared;
        lock (preparedHeadlessProcessGate)
        {
            prepared = preparedHeadlessProcess;
            preparedHeadlessProcess = null;
        }

        if (prepared is null)
            return null;

        if (prepared.Identity.Matches(identity))
            return prepared;

        DisposePreparedProcess(prepared);
        return null;
    }

    private static PreparedDshIdentity CreatePreparedDshIdentity(
        string executable,
        string? dshHome,
        string profileName)
    {
        var command = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        var home = ResolveDshHome(dshHome);
        return new PreparedDshIdentity(command, home, profileName.Trim());
    }

    private static bool IsProcessRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static DshOperationResult CreateExitedBeforeInputResult(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        var output = GetCompletedOutput(outputTask);
        var error = GetCompletedOutput(errorTask);
        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        var message = FirstNonEmptyLine(error)
            ?? FirstNonEmptyLine(output)
            ?? $"DSH 预热进程在接收命令前退出（退出码 {exitCode}）";
        return new DshOperationResult(false, exitCode, message, output, error);
    }

    private static void DisposePreparedProcess(PreparedDshProcess prepared)
    {
        var started = prepared.Started;
        var process = started.Process;
        try
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            TryKill(process);
            try
            {
                process.WaitForExit(3000);
            }
            catch
            {
            }

            try
            {
                Task.WhenAll(started.OutputTask, started.ErrorTask).Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Accessing Exception marks any faulted stream task as observed.
                _ = started.OutputTask.Exception;
                _ = started.ErrorTask.Exception;
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateCommandDirectories()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var knownDirectories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims")
        };
        return pathDirectories
            .Concat(knownDirectories)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmptyLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string BuildVoiceAgentPrompt(string prompt)
    {
        // Keep the voice request compact in DSH session logs. RunHeadlessPromptAsync
        // transfers this string through stdin so batch-file quoting cannot alter it.
        var normalizedPrompt = string.Join(
            ' ',
            prompt.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"你是 Halo PixelBar 的语音助手。请严格执行下面的用户口令；优先调用可用的 PixelBar 工具完成操作；完成后只用一句简短中文说明结果，不使用 Markdown。不要改变、扩展或猜测用户原意。用户口令：{normalizedPrompt}";
    }

    private static bool IsHeadlessProfileConfig(string config)
        => config.Contains("@deepseek-ai/dsh-headless", StringComparison.OrdinalIgnoreCase);

    private static HeadlessEventSummary ParseHeadlessEvents(string output)
    {
        string? finalText = null;
        string? lastUsableMessage = null;
        var sawFinal = false;
        var errors = new List<string>();
        var calledTools = new List<string>();
        var successfulTools = new List<string>();
        var toolsByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (string.Equals(type, "final", StringComparison.OrdinalIgnoreCase))
                {
                    sawFinal = true;
                    var text = GetJsonString(root, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                        finalText = text.Trim();
                    continue;
                }

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    var text = GetJsonString(root, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                        lastUsableMessage = text.Trim();
                    continue;
                }

                if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
                {
                    var tool = GetJsonString(root, "tool");
                    var callId = GetJsonString(root, "callId");
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        calledTools.Add(tool);
                        if (!string.IsNullOrWhiteSpace(callId))
                            toolsByCallId[callId] = tool;
                    }
                    continue;
                }

                if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    var outcome = ParseToolResult(root);
                    var callId = GetJsonString(root, "callId");
                    toolsByCallId.TryGetValue(callId ?? string.Empty, out var tool);
                    if (outcome.Success)
                    {
                        if (!string.IsNullOrWhiteSpace(outcome.Message))
                            lastUsableMessage = outcome.Message.Trim();
                        if (!string.IsNullOrWhiteSpace(tool))
                            successfulTools.Add(tool);
                    }
                    else if (!string.IsNullOrWhiteSpace(outcome.Message))
                    {
                        errors.Add(outcome.Message.Trim());
                    }
                    continue;
                }

                if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)
                    || type?.EndsWith("_error", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var message = GetHeadlessErrorMessage(root);
                    if (!string.IsNullOrWhiteSpace(message))
                        errors.Add(message);
                }
            }
            catch (JsonException)
            {
                // npx may write informational lines alongside the NDJSON stream.
            }
        }

        return new HeadlessEventSummary(
            finalText,
            lastUsableMessage,
            sawFinal,
            errors,
            calledTools,
            successfulTools);
    }

    private static ToolResultOutcome ParseToolResult(JsonElement root)
    {
        var eventStatus = GetJsonString(root, "status");
        var eventSucceeded = string.IsNullOrWhiteSpace(eventStatus)
            || string.Equals(eventStatus, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventStatus, "succeeded", StringComparison.OrdinalIgnoreCase);

        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new ToolResultOutcome(
                eventSucceeded,
                eventSucceeded ? null : GetHeadlessErrorMessage(root) ?? "DSH 工具调用失败");
        }

        JsonDocument? nestedDocument = null;
        try
        {
            var payload = result;
            string? rawResult = null;
            if (result.ValueKind == JsonValueKind.String)
            {
                rawResult = result.GetString();
                if (string.IsNullOrWhiteSpace(rawResult))
                    return new ToolResultOutcome(eventSucceeded, null);

                try
                {
                    nestedDocument = JsonDocument.Parse(rawResult);
                    payload = nestedDocument.RootElement;
                }
                catch (JsonException)
                {
                    return new ToolResultOutcome(eventSucceeded, rawResult.Trim());
                }
            }

            if (payload.ValueKind != JsonValueKind.Object)
                return new ToolResultOutcome(eventSucceeded, payload.GetRawText());

            var payloadSucceeded = !payload.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.False;

            var payloadStatus = GetJsonString(payload, "status");
            if (string.Equals(payloadStatus, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payloadStatus, "error", StringComparison.OrdinalIgnoreCase))
                payloadSucceeded = false;

            var message = GetJsonString(payload, "message")
                ?? GetJsonString(payload, "error");
            return new ToolResultOutcome(eventSucceeded && payloadSucceeded, message);
        }
        finally
        {
            nestedDocument?.Dispose();
        }
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static async Task WaitForExitAndDrainAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Preserve the original timeout or cancellation outcome.
        }

        try
        {
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // The caller can still use whichever stream completed successfully.
        }
    }

    private static string GetCompletedOutput(Task<string> task)
        => task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;

    private static string? GetHeadlessErrorMessage(JsonElement root)
    {
        foreach (var propertyName in new[] { "message", "text", "error" })
        {
            if (!root.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("message", out var nestedMessage)
                && nestedMessage.ValueKind == JsonValueKind.String)
            {
                return nestedMessage.GetString();
            }

            return value.GetRawText();
        }

        return null;
    }

    private static DshOperationResult CreateBridgeFailure(
        string message,
        string? status,
        string? code,
        string response)
    {
        var details = $"{message}（status: {status ?? "未知"}，code: {code ?? "未知"}）";
        return new DshOperationResult(false, -1, message, response, details)
        {
            Status = status,
            Code = code
        };
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static bool IsNpxCommand(string command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command);
        return fileName.Equals("npx", StringComparison.OrdinalIgnoreCase);
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

    private sealed record HeadlessEventSummary(
        string? FinalText,
        string? LastUsableMessage,
        bool SawFinal,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> CalledTools,
        IReadOnlyList<string> SuccessfulTools);

    private sealed record ToolResultOutcome(bool Success, string? Message);

    private sealed record StartedDshProcess(
        Process Process,
        Task<string> OutputTask,
        Task<string> ErrorTask);

    private sealed record DshProcessCompletion(
        DshOperationResult Operation,
        bool StandardInputWriteStarted);

    private sealed record PreparedDshProcess(
        PreparedDshIdentity Identity,
        StartedDshProcess Started);

    private sealed record PreparedDshIdentity(
        string Executable,
        string DshHome,
        string ProfileName)
    {
        public bool Matches(PreparedDshIdentity other)
            => string.Equals(Executable, other.Executable, StringComparison.OrdinalIgnoreCase)
               && string.Equals(DshHome, other.DshHome, StringComparison.OrdinalIgnoreCase)
               && string.Equals(ProfileName, other.ProfileName, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileNameRegex();
}

public sealed record DshOperationResult(
    bool Success,
    int ExitCode,
    string Message,
    string StandardOutput,
    string StandardError)
{
    public string? Status { get; init; }

    public string? Code { get; init; }

    public static DshOperationResult Failed(string message)
        => new(false, -1, message, string.Empty, string.Empty);
}

public sealed record DshHeadlessPromptResult(
    bool Success,
    string Message,
    string FinalText,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CalledTools { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SuccessfulTools { get; init; } = Array.Empty<string>();

    public static DshHeadlessPromptResult Failed(string message)
        => new(false, message, string.Empty, -1, string.Empty, string.Empty);
}

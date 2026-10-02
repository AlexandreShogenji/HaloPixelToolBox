using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

public sealed record DshSessionsConnectionOptions(string Executable, string Home, string Profile)
{
    public static DshSessionsConnectionOptions FromProfile() => new(
        DisplayFeatureProfile.DshExecutablePath,
        DisplayFeatureProfile.DshHomePath,
        DisplayFeatureProfile.DshProfileName);
}

/// <summary>
/// Shared client of a real DSH host. History reads never resume agents; device commands
/// use one dedicated session. Page lifetime never owns the host.
/// </summary>
public sealed partial class DshSessionsService : IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<DshSessionsConnectionOptions> optionsFactory;
    private readonly string discoveryRoot;
    private readonly HttpClient client;
    private DshSessionsSnapshot current = DshSessionsSnapshot.Initial;
    private BridgeDescriptor? endpoint;
    private Process? ownedHost;
    private Process? ownedBridgeHost;
    private DshOwnedProcessJob? ownedJob;
    private string? ownedHome;
    private Task? stdoutPump;
    private Task? stderrPump;
    private string? listedHome;
    private string? listedProfile;
    private bool commandRefreshRunning;
    private bool commandRefreshRequested;
    private bool disposed;

    public DshSessionsService(
        Func<DshSessionsConnectionOptions>? optionsFactory = null,
        string? discoveryRoot = null)
    {
        this.optionsFactory = optionsFactory ?? DshSessionsConnectionOptions.FromProfile;
        this.discoveryRoot = discoveryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloPixelToolBox", "DshSessionBridge");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            var home = ResolveHome(this.optionsFactory().Home);
            current = current with { Home = home, VoiceTarget = ReadSavedTarget(home), DeviceSessionId = ReadSavedDeviceSession(home) };
        }
        catch (Exception exception) { current = current with { Message = $"DSH 数据目录配置无效：{exception.Message}" }; }
    }

    public event EventHandler<DshSessionsSnapshot>? Changed;

    public DshSessionsSnapshot Current
    {
        get { lock (stateGate) return current; }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await connectionGate.WaitAsync(linked.Token);
        try { await RefreshCoreAsync(linked.Token); }
        finally { connectionGate.Release(); }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => ConnectCoreAsync(cancellationToken, requireDeviceCommands: false);

    public async Task PrepareDeviceSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var options = optionsFactory();
        var home = ResolveHome(options.Home);
        BridgeDescriptor? active;
        lock (stateGate) active = endpoint;
        // Readiness probes can run beside an expensive history-list refresh.
        // Only connection creation/replacement needs the connection gate.
        if (Current.IsConnected && active is { CanPrompt: true } && SameHome(active.Home, home)
            && active.Profile == options.Profile && ProcessIsAlive(active.Pid)
            && await ProbeReadyDeviceEndpointAsync(active, linked.Token))
        {
            var latestOptions = optionsFactory();
            lock (stateGate)
            {
                if (ReferenceEquals(endpoint, active) && current.IsConnected
                    && SameHome(home, ResolveHome(latestOptions.Home)) && options.Profile == latestOptions.Profile)
                    return;
            }
        }
        await ConnectCoreAsync(cancellationToken, requireDeviceCommands: true);
        if (!Current.IsConnected)
            throw new InvalidOperationException(Current.Message);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken, bool requireDeviceCommands)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await connectionGate.WaitAsync(linked.Token);
        var startedHere = false;
        try
        {
            var options = optionsFactory();
            var home = ResolveHome(options.Home);
            if (ownedHome is not null && !SameHome(ownedHome, home))
                await StopOwnedHostAsync();
            if (!SameHome(Current.Home, home))
                MarkDisconnected("正在连接当前数据目录的 DSH 服务。");
            if (await FindEndpointAsync(home, linked.Token, requiredProfile: options.Profile,
                requireDeviceCommands: requireDeviceCommands) is { } discovered)
            {
                SetEndpoint(discovered);
                await RefreshCoreAsync(linked.Token);
                return;
            }

            if (!Regex.IsMatch(options.Profile, @"\A[A-Za-z0-9_.-]+\z")
                || string.IsNullOrWhiteSpace(options.Executable))
                throw new InvalidOperationException("请在设置中填写有效的 DSH 启动程序和 Web Profile 名称。");

            var bridgePath = ResolveBridgeAsset("index.js")
                ?? throw new FileNotFoundException("未找到内置 DSH 会话桥接模块。");
            var discoveryDirectory = GetDiscoveryDirectory(home);
            Directory.CreateDirectory(discoveryDirectory);
            var ownedStartupId = Guid.NewGuid().ToString("N");
            Publish(Current with { Message = "正在检测 DSH 并准备音箱控制会话。" });
            var legacy = await DshDeviceSessionPreset.UsesLegacyPresetsAsync(options.Executable, home, linked.Token);
            var patchPath = await DshDeviceSessionPreset.WritePatchAsync(
                discoveryDirectory, bridgePath, home, options.Profile, ownedStartupId, legacy, linked.Token);

            await StopOwnedHostAsync();
            var startInfo = DshIntegrationService.CreateDshStartInfo(
                options.Executable, home,
                ["--profile", options.Profile, "--patch", patchPath,
                 "--host", "127.0.0.1", "--port", "0", "--no-open"],
                redirectStandardInput: false);
            linked.Token.ThrowIfCancellationRequested();
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 DSH 会话服务。");
            DshOwnedProcessJob job;
            try { job = DshOwnedProcessJob.Attach(process); }
            catch
            {
                TryKill(process);
                process.Dispose();
                throw;
            }
            lock (stateGate)
            {
                if (disposed)
                {
                    job.Dispose();
                    TryKill(process);
                    process.Dispose();
                    throw new OperationCanceledException(linked.Token);
                }
                ownedHost = process;
                ownedJob = job;
                ownedHome = home;
                // DSH prints its authenticated browser URL. Drain it without logging
                // or returning that token-bearing URL to the UI.
                stdoutPump = DrainDiagnosticsAsync(process.StandardOutput);
                stderrPump = DrainDiagnosticsAsync(process.StandardError);
            }
            startedHere = true;
            Publish(Current with
            {
                IsConnected = false, HostUrl = string.Empty,
                Message = "正在启动 DSH 会话服务，首次加载可能需要一些时间。"
            });

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(120));
            while (!process.HasExited)
            {
                if (await FindEndpointAsync(home, startup.Token, ownedStartupId,
                    requiredProfile: options.Profile, requireDeviceCommands: requireDeviceCommands) is { } ready)
                {
                    var bridgeProcess = job.CaptureMember(ready.Pid);
                    lock (stateGate)
                    {
                        if (disposed)
                        {
                            bridgeProcess.Dispose();
                            throw new OperationCanceledException(linked.Token);
                        }
                        ownedBridgeHost = bridgeProcess;
                    }
                    SetEndpoint(ready);
                    await RefreshCoreAsync(startup.Token);
                    if (!Current.IsConnected)
                        throw new InvalidOperationException(Current.Message);
                    return;
                }
                await Task.Delay(400, startup.Token);
            }
            throw new InvalidOperationException($"DSH 会话服务退出（退出码 {process.ExitCode}）。请确认设置中的 Profile 为 Web Profile，并检测 DSH。"
            );
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            if (startedHere) await StopOwnedHostAsync();
            MarkDisconnected("DSH 会话服务在 120 秒内未就绪，请检查设置中的 DSH 与 Web Profile。");
        }
        catch (OperationCanceledException)
        {
            if (startedHere) await StopOwnedHostAsync();
            MarkDisconnected("连接已取消。");
            throw;
        }
        catch (Exception exception)
        {
            if (startedHere) await StopOwnedHostAsync();
            MarkDisconnected(exception.Message);
        }
        finally { connectionGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            await StopOwnedHostAsync();
            MarkDisconnected("已断开会话服务。外部启动的 DSH 服务不受影响。");
        }
        finally { connectionGate.Release(); }
    }

    public async Task<DshDeviceCommandResult> ExecuteDeviceCommandAsync(
        string command, int timeoutSeconds = 120, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        command = command.Trim();
        if (command.Length is 0 or > 4096)
            throw new ArgumentException("设备口令不能为空或超过 4096 字符。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await commandGate.WaitAsync(linked.Token);
        string sessionId = string.Empty;
        string requestId = string.Empty;
        var sent = false;
        try
        {
            await PrepareDeviceSessionAsync(linked.Token);
            var options = optionsFactory();
            var home = ResolveHome(options.Home);
            BridgeDescriptor connected;
            lock (stateGate)
                connected = endpoint ?? throw new InvalidOperationException("DSH 设备会话未连接。");
            if (!connected.CanPrompt || !SameHome(connected.Home, home) || connected.Profile != options.Profile)
                throw new InvalidOperationException("DSH 配置已变更，请重新连接后再说口令。");

            sessionId = ReadSavedDeviceSession(home);
            if (sessionId.Length == 0)
            {
                sessionId = Guid.TryParse(connected.DeviceSessionId, out _) ? connected.DeviceSessionId
                    : Current.Sessions.FirstOrDefault(session => session.DeviceControlKind == "canonical"
                        && session.RuntimeStatus is "idle" or "running" && Guid.TryParse(session.Id, out _))?.Id
                        ?? Guid.NewGuid().ToString("D");
                // Save identity before dispatch. A lost response must never turn
                // the next command into a second fresh conversation.
                DshDeviceSessionIdentity.Save(GetDeviceIdentityPath(home, options.Profile), home, options.Profile, sessionId);
                DisplayFeatureProfile.DshDeviceSessionId = sessionId;
                DisplayFeatureProfile.DshDeviceSessionHomePath = home;
                DisplayFeatureProfile.DshDeviceSessionProfileName = options.Profile;
            }
            Publish(Current with { DeviceSessionId = sessionId });
            linked.Token.ThrowIfCancellationRequested();
            requestId = Guid.NewGuid().ToString("D");
            sent = true;
            using var document = await RequestAsync(connected, "v1/device-command", linked.Token,
                new { command, sessionId, requestId, timeoutSeconds = Math.Clamp(timeoutSeconds, 30, 300) },
                Math.Clamp(timeoutSeconds, 30, 300) + 10);
            var root = document.RootElement;
            if (ReadString(root, "sessionId") != sessionId)
                throw new InvalidDataException("DSH 返回的设备会话身份不一致。");
            if (ReadString(root, "requestId") != requestId)
                throw new InvalidDataException("DSH 返回了其他设备口令的结果。");
            linked.Token.ThrowIfCancellationRequested();
            var activeOptions = optionsFactory();
            BridgeDescriptor? activeEndpoint;
            lock (stateGate) activeEndpoint = endpoint;
            if (!SameHome(home, ResolveHome(activeOptions.Home)) || activeOptions.Profile != options.Profile
                || activeEndpoint?.Pid != connected.Pid || activeEndpoint.BaseUrl != connected.BaseUrl)
                throw new OperationCanceledException("DSH 配置或连接已改变，请查看原设备会话的结果。");
            var result = ReadCommandResult(root, sessionId, requestId);
            // Return the confirmed outcome immediately. A list refresh is only a
            // UI update; it must not add latency or change the command outcome.
            ScheduleCommandRefresh();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (BridgeRequestException exception) when (IsAdmissionRejection(exception.Code))
        {
            return new(false, "口令未提交：" + DescribeRejection(exception.Code), string.Empty, sessionId, [], [])
                { ErrorCode = exception.Code, RequestId = requestId };
        }
        catch (Exception exception)
        {
            return new(false, sent
                ? $"未确认 DSH 执行结果：{exception.Message}。请查看音箱控制会话；本轮不会自动重发。"
                : exception.Message, string.Empty, sessionId, [], [])
                { ErrorCode = sent ? "unconfirmed" : "not_submitted", RequestId = requestId };
        }
        finally { commandGate.Release(); }
    }

    public async Task<DshDeviceCommandResult> SendMessageAsync(
        string sessionId, string message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        message = message.Trim();
        if (message.Length is 0 or > 4096)
            throw new ArgumentException("消息不能为空或超过 4096 字符。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await commandGate.WaitAsync(linked.Token);
        var sent = false;
        var requestId = Guid.NewGuid().ToString("D");
        try
        {
            var connected = RequireSessionEndpoint(sessionId, capabilities => capabilities.CanSendMessages);
            sent = true;
            using var document = await RequestAsync(connected, "v1/session-command", linked.Token,
                new { sessionId, command = message, requestId, timeoutSeconds = 120 }, timeoutSeconds: 130);
            var result = ReadCommandResult(document.RootElement, sessionId, requestId);
            linked.Token.ThrowIfCancellationRequested();
            ValidateReplyScope(connected);
            ScheduleCommandRefresh();
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (BridgeRequestException exception) when (IsAdmissionRejection(exception.Code))
        {
            return new(false, "消息未提交：" + DescribeRejection(exception.Code), string.Empty, sessionId, [], [])
                { ErrorCode = exception.Code, RequestId = requestId };
        }
        catch (Exception exception)
        {
            return new(false, sent
                ? $"未确认 DSH 执行结果：{exception.Message}。请刷新会话核对，本轮不会自动重发。"
                : exception.Message, string.Empty, sessionId, [], [])
                { ErrorCode = sent ? "unconfirmed" : "not_submitted", RequestId = requestId };
        }
        finally { commandGate.Release(); }
    }

    public async Task<DshSessionSummary> CreateSessionAsync(string title, CancellationToken cancellationToken = default)
    {
        title = ValidateTitle(title);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await commandGate.WaitAsync(linked.Token);
        try
        {
            var connected = RequireSessionEndpoint(null, capabilities => capabilities.CanCreateSessions);
            using var document = await RequestAsync(connected, "v1/sessions/create", linked.Token, new { title });
            linked.Token.ThrowIfCancellationRequested();
            ValidateReplyScope(connected);
            var item = document.RootElement.GetProperty("session");
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || ReadBool(item, "isDeviceControl"))
                throw new InvalidDataException("DSH 返回了无效的新会话。");
            var status = ReadString(item, "runtimeStatus");
            return new(id, ReadString(item, "title"), ReadString(item, "workingDirectory"),
                ReadDate(item, "updatedAt") ?? DateTimeOffset.MinValue,
                status is "idle" or "running" or "detached" ? status : "unknown")
                { IsArchived = ReadBool(item, "isArchived") };
        }
        finally { commandGate.Release(); }
    }

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
    {
        title = ValidateTitle(title);
        return MutateSessionAsync(sessionId, "v1/sessions/rename", new { sessionId, title },
            capabilities => capabilities.CanRenameSessions,
            root => ReadString(root, "sessionId") == sessionId && ReadString(root, "title") == title, cancellationToken);
    }

    public Task SetSessionArchivedAsync(string sessionId, bool archived, CancellationToken cancellationToken = default)
        => MutateSessionAsync(sessionId, "v1/sessions/archive", new { sessionId, archived },
            capabilities => archived ? capabilities.CanArchiveSessions : capabilities.CanRestoreSessions,
            root => ReadString(root, "sessionId") == sessionId
                && root.TryGetProperty("archived", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && (value.ValueKind == JsonValueKind.True) == archived,
            cancellationToken, allowArchived: !archived);

    private async Task MutateSessionAsync(string sessionId, string path, object body,
        Func<DshSessionCapabilities, bool> supported, Func<JsonElement, bool> validReply,
        CancellationToken cancellationToken, bool allowArchived = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await commandGate.WaitAsync(linked.Token);
        try
        {
            var connected = RequireSessionEndpoint(sessionId, supported, allowArchived);
            using var document = await RequestAsync(connected, path, linked.Token, body);
            linked.Token.ThrowIfCancellationRequested();
            ValidateReplyScope(connected);
            if (!validReply(document.RootElement))
                throw new InvalidDataException("DSH 返回了其他会话的操作结果。");
        }
        finally { commandGate.Release(); }
    }

    private BridgeDescriptor RequireSessionEndpoint(string? sessionId,
        Func<DshSessionCapabilities, bool> supported, bool allowArchived = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = optionsFactory();
        var home = ResolveHome(options.Home);
        lock (stateGate)
        {
            if (!current.IsConnected || endpoint is null || !SameHome(endpoint.Home, home) || endpoint.Profile != options.Profile)
                throw new InvalidOperationException("请先连接当前数据目录和 Profile 的 DSH 服务。");
            if (!supported(endpoint.Capabilities))
                throw new InvalidOperationException("当前 DSH 服务不支持此操作，请更新并重新连接。");
            if (sessionId is not null)
            {
                var session = current.Sessions.FirstOrDefault(item => item.Id == sessionId)
                    ?? throw new InvalidOperationException("会话已不在当前列表中，请刷新。");
                if (session.IsDeviceControl || session.Id == current.DeviceSessionId)
                    throw new InvalidOperationException("音箱控制会话请使用专用设备口令，不能修改聚合历史来源。");
                if (session.IsArchived && !allowArchived)
                    throw new InvalidOperationException("请先恢复已归档会话。");
            }
            return endpoint;
        }
    }

    private void ValidateReplyScope(BridgeDescriptor connected)
    {
        var options = optionsFactory();
        lock (stateGate)
        {
            if (!SameHome(connected.Home, ResolveHome(options.Home)) || connected.Profile != options.Profile
                || endpoint?.Pid != connected.Pid || endpoint.BaseUrl != connected.BaseUrl)
                throw new OperationCanceledException("DSH 配置或连接已改变，请查看原会话的操作结果。");
        }
    }

    private static string ValidateTitle(string title)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200 || title.Any(char.IsControl))
            throw new ArgumentException("会话名称须为 1–200 字符，不能包含换行或控制字符。");
        return title;
    }

    private static DshDeviceCommandResult ReadCommandResult(JsonElement root, string sessionId, string requestId)
    {
        if (ReadString(root, "sessionId") != sessionId || ReadString(root, "requestId") != requestId)
            throw new InvalidDataException("DSH 返回了其他消息或会话的执行结果。");
        var status = ReadString(root, "status");
        if (status is not ("" or "completed" or "accepted" or "unknown"))
            throw new InvalidDataException("DSH 返回了无效的消息状态。");
        var completed = status == "completed" || (status.Length == 0 && ReadBool(root, "success"));
        var accepted = completed || status == "accepted";
        var text = ReadString(root, "finalText");
        var message = !completed && accepted ? "消息已提交，DSH 可能仍在执行，请查看会话；本轮不会自动重发。"
            : !accepted && status == "unknown" ? "未确认消息是否提交，请查看会话；本轮不会自动重发。"
            : ReadString(root, "message");
        return new(ReadBool(root, "success"), message, text, sessionId,
            ReadStringArray(root, "calledTools"), ReadStringArray(root, "successfulTools"))
            { Accepted = accepted, Completed = completed, RequestId = requestId,
                ErrorCode = !accepted && status == "unknown" ? "unconfirmed" : string.Empty };
    }

    public async Task<DshHistoryPage> ReadHistoryAsync(
        string sessionId, long? beforeSeq = null, CancellationToken cancellationToken = default)
        => await ReadHistoryCoreAsync(sessionId, beforeSeq, 50, cancellationToken);

    public async Task<DshDeviceHistoryPage> ReadDeviceHistoryAsync(
        IReadOnlyList<string> sessionIds, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var home = ResolveHome(optionsFactory().Home);
        var allowed = Current.Sessions.Where(session => session.IsDeviceControl || session.Id == Current.DeviceSessionId)
            .Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        if (sessionIds.Any(id => !allowed.Contains(id)))
            throw new InvalidOperationException("音箱历史来源不在当前设备会话列表中，请刷新。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var profile = optionsFactory().Profile;
        var page = await DshDeviceHistoryReader.ReadAsync(sessionIds, home.ToUpperInvariant() + "\n" + profile, cursor,
            (id, before, token) => ReadHistoryCoreAsync(id, before, 10, token), linked.Token);
        if (!SameHome(home, ResolveHome(optionsFactory().Home)) || profile != optionsFactory().Profile)
            throw new OperationCanceledException("DSH 配置已改变，忽略旧音箱历史。");
        return page;
    }

    private async Task<DshHistoryPage> ReadHistoryCoreAsync(
        string sessionId, long? beforeSeq, int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        BridgeDescriptor? connected;
        lock (stateGate) connected = endpoint;
        if (connected is null || !Current.IsConnected)
            throw new InvalidOperationException("请先连接 DSH 会话服务。");
        var options = optionsFactory();
        if (!SameHome(connected.Home, ResolveHome(options.Home)) || connected.Profile != options.Profile)
            throw new InvalidOperationException("DSH 数据目录或 Profile 已变更，请重新连接。");
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 512 || beforeSeq < 0)
            throw new ArgumentException("无效的会话或历史游标。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var path = "v1/history?sessionId=" + Uri.EscapeDataString(sessionId) + "&limit=" + limit;
        if (beforeSeq is { } cursor) path += "&beforeSeq=" + cursor;
        using var document = await RequestAsync(connected, path, linked.Token);
        var root = document.RootElement;
        if (!string.Equals(ReadString(root, "sessionId"), sessionId, StringComparison.Ordinal))
            throw new InvalidDataException("DSH 返回了其他会话的历史，已拒绝显示。");
        var entries = new List<DshHistoryEntry>();
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            var sequence = entry.GetProperty("sequence").GetInt64();
            if (sequence < 0 || (beforeSeq is { } requestedBefore && sequence >= requestedBefore)
                || (entries.Count > 0 && sequence <= entries[^1].Sequence))
                throw new InvalidDataException("DSH 历史事件顺序无效。");
            var role = ReadString(entry, "role");
            var text = ReadString(entry, "text");
            var kind = ReadString(entry, "kind");
            if (kind is not ("conversation" or "context" or "tool"))
                kind = role.Equals("tool", StringComparison.OrdinalIgnoreCase) ? "tool"
                    : role.Equals("system", StringComparison.OrdinalIgnoreCase) || role.Equals("developer", StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith("Current runtime context. This snapshot supersedes earlier runtime-context snapshots.", StringComparison.Ordinal)
                        ? "context" : "conversation";
            var entrySource = ReadString(entry, "sessionId");
            if (entrySource.Length > 0 && entrySource != sessionId)
                throw new InvalidDataException("DSH 历史消息来源不一致。");
            entries.Add(new(sequence, role, text, ReadDate(entry, "createdAt"), ReadBool(entry, "truncated"))
                { Kind = kind, SessionId = sessionId });
        }
        var before = root.TryGetProperty("beforeSeq", out var value) && value.TryGetInt64(out var number)
            ? (long?)number : null;
        var hasMore = ReadBool(root, "hasMore");
        if (hasMore && (before is null || before < 0 || (beforeSeq is { } previous && before >= previous)))
            throw new InvalidDataException("DSH 历史游标没有前进。");
        linked.Token.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (!ReferenceEquals(endpoint, connected))
                throw new OperationCanceledException("DSH 连接已改变，忽略旧历史响应。");
        }
        var activeOptions = optionsFactory();
        if (!SameHome(connected.Home, ResolveHome(activeOptions.Home)) || connected.Profile != activeOptions.Profile)
            throw new OperationCanceledException("DSH 数据目录或 Profile 已改变，忽略旧历史响应。");
        return new(entries.ToArray(), before, hasMore, ReadBool(root, "truncated"));
    }

    public void SelectVoiceTarget(DshSessionSummary session)
    {
        var home = ResolveHome(optionsFactory().Home);
        DshSessionSummary actual;
        lock (stateGate)
        {
            if (!current.IsConnected || endpoint is null || !SameHome(home, endpoint.Home)
                || endpoint.Profile != optionsFactory().Profile)
                throw new InvalidOperationException("请先连接当前数据目录的 DSH 服务。");
            actual = current.Sessions.FirstOrDefault(item => item.Id == session.Id)
                ?? throw new InvalidOperationException("会话已不在当前列表中，请刷新。");
        }
        DisplayFeatureProfile.DshVoiceTargetSessionId = actual.Id;
        DisplayFeatureProfile.DshVoiceTargetHomePath = home;
        DisplayFeatureProfile.DshVoiceTargetTitle = actual.Title;
        DisplayFeatureProfile.DshVoiceTargetWorkingDirectory = actual.WorkingDirectory;
        Publish(Current with { VoiceTarget = actual });
    }

    public void ClearVoiceTarget()
    {
        DisplayFeatureProfile.DshVoiceTargetSessionId = string.Empty;
        DisplayFeatureProfile.DshVoiceTargetHomePath = string.Empty;
        DisplayFeatureProfile.DshVoiceTargetTitle = string.Empty;
        DisplayFeatureProfile.DshVoiceTargetWorkingDirectory = string.Empty;
        Publish(Current with { VoiceTarget = null });
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        string? home = null;
        try
        {
            var options = optionsFactory();
            home = ResolveHome(options.Home);
            BridgeDescriptor? connected;
            bool wasConnected;
            lock (stateGate)
            {
                connected = endpoint;
                wasConnected = current.IsConnected;
            }
            if (!wasConnected || connected is null || !SameHome(home, connected.Home) || connected.Profile != options.Profile
                || !ProcessIsAlive(connected.Pid))
            {
                var discovered = await FindEndpointAsync(home, cancellationToken, requiredProfile: options.Profile);
                if (discovered is null)
                {
                    MarkDisconnected("尚未连接 DSH 会话服务。点击“启动并连接 DSH”加载本机历史。");
                    return;
                }
                // A retained descriptor only protects replies already in flight.
                // Rediscover after read failures because a live PID can reload its
                // bridge on a new port or rotate its bearer. An identical result
                // keeps the original reference for concurrent confirmed replies.
                if (connected is null || connected != discovered) connected = discovered;
                SetEndpoint(connected);
            }
            // Cold legacy history classification can outlast a normal history
            // read. The bridge has its own 15-second request bound.
            using var document = await RequestAsync(connected, "v1/sessions", cancellationToken, timeoutSeconds: 20);
            var root = document.RootElement;
            if (!SameHome(ReadString(root, "home"), home))
                throw new InvalidDataException("DSH 服务的数据目录与设置不一致。");
            var sessions = new List<DshSessionSummary>();
            var deviceSessionId = ReadSavedDeviceSession(home);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("sessions").EnumerateArray())
            {
                if (sessions.Count >= 5000) throw new InvalidDataException("会话列表超过首版支持的 5000 个会话。");
                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    throw new InvalidDataException("DSH 会话标识为空或重复。");
                var status = ReadString(item, "runtimeStatus");
                if (status is not ("running" or "idle" or "detached")) status = "unknown";
                var title = ReadString(item, "title");
                sessions.Add(new(id, string.IsNullOrWhiteSpace(title) ? "未命名会话" : title,
                    ReadString(item, "workingDirectory"), ReadDate(item, "updatedAt") ?? DateTimeOffset.MinValue, status)
                    { IsDeviceControl = ReadBool(item, "isDeviceControl") || id == deviceSessionId,
                        DeviceControlKind = ReadString(item, "deviceControlKind"), IsArchived = ReadBool(item, "isArchived") });
            }
            cancellationToken.ThrowIfCancellationRequested();
            var activeOptions = optionsFactory();
            var activeHome = ResolveHome(activeOptions.Home);
            if (!SameHome(home, activeHome) || options.Profile != activeOptions.Profile)
            {
                MarkDisconnected("DSH 数据目录或 Profile 已变更，请重新连接。");
                return;
            }
            var saved = ReadSavedTarget(home);
            var target = saved is null ? null : sessions.FirstOrDefault(item => item.Id == saved.Id) ?? saved;
            lock (stateGate)
            {
                listedHome = home;
                listedProfile = options.Profile;
            }
            Publish(new(true, connected.BaseUrl,
                $"已连接 DSH · {sessions.Count} 个会话。运行状态来自当前连接的服务。", sessions.ToArray(), target)
                { Home = home, DeviceSessionId = deviceSessionId, Capabilities = connected.Capabilities });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // List health is a UI concern. A failed background read must not
            // discard a separately authenticated command response in flight.
            // New commands still reconnect because IsConnected becomes false.
            MarkDisconnected($"无法读取 DSH 会话：{exception.Message}", retainEndpoint: true);
        }
    }

    private async Task<bool> ProbeReadyDeviceEndpointAsync(BridgeDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            using var status = await RequestAsync(descriptor, "v1/status", cancellationToken, timeoutSeconds: 2);
            var root = status.RootElement;
            return root.GetProperty("protocolVersion").GetInt32() == 1 && ReadBool(root, "ready")
                && root.GetProperty("pid").GetInt32() == descriptor.Pid
                && SameHome(ReadString(root, "home"), descriptor.Home)
                && ReadString(root, "deviceAgentPreset") == "halo-device"
                && root.TryGetProperty("capabilities", out var capabilities)
                && ReadBool(capabilities, "prompt") && ReadBool(capabilities, "deviceHistoryGrouping");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
    }

    private void ScheduleCommandRefresh()
    {
        lock (stateGate)
        {
            if (disposed || shutdown.IsCancellationRequested)
                return;
            commandRefreshRequested = true;
            if (commandRefreshRunning)
                return;
            commandRefreshRunning = true;
        }
        _ = RefreshAfterCommandsAsync();
    }

    private async Task RefreshAfterCommandsAsync()
    {
        var released = false;
        try
        {
            while (true)
            {
                lock (stateGate)
                {
                    if (disposed || shutdown.IsCancellationRequested || !commandRefreshRequested)
                    {
                        commandRefreshRunning = false;
                        released = true;
                        return;
                    }
                    commandRefreshRequested = false;
                }
                try { await RefreshAsync(shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (ObjectDisposedException) { }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Debug.WriteLine($"[DshSessions] 后台刷新失败：{exception.Message}");
                }
            }
        }
        finally
        {
            if (!released)
                lock (stateGate) commandRefreshRunning = false;
        }
    }

    private async Task<BridgeDescriptor?> FindEndpointAsync(string home, CancellationToken cancellationToken,
        string? ownedStartupId = null, string? requiredProfile = null, bool requireDeviceCommands = false)
    {
        var directory = GetDiscoveryDirectory(home);
        if (!Directory.Exists(directory)) return null;
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discovery.CancelAfter(TimeSpan.FromSeconds(5));
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json")
            .Where(file => file.Length <= 16 * 1024 && !file.Name.Contains("patch", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc).Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discovery.IsCancellationRequested) break;
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(discovery.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(1));
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName, probe.Token));
                var root = document.RootElement;
                var descriptor = new BridgeDescriptor(ReadString(root, "home"), ReadString(root, "profile"),
                    root.GetProperty("pid").GetInt32(), ReadString(root, "baseUrl"), ReadString(root, "token"), ReadString(root, "ownedStartupId"));
                if (root.GetProperty("protocolVersion").GetInt32() != 1 || !SameHome(descriptor.Home, home)
                    || (ownedStartupId is not null && descriptor.OwnedStartupId != ownedStartupId)
                    || (requiredProfile is not null && descriptor.Profile != requiredProfile)
                    || !IsAllowedEndpoint(descriptor.BaseUrl) || !IsValidToken(descriptor.Token)
                    || !ProcessIsAlive(descriptor.Pid)) continue;
                using var status = await RequestAsync(descriptor, "v1/status", probe.Token);
                if (status.RootElement.GetProperty("protocolVersion").GetInt32() == 1
                    && ReadBool(status.RootElement, "ready")
                    && SameHome(ReadString(status.RootElement, "home"), home)
                    && status.RootElement.GetProperty("pid").GetInt32() == descriptor.Pid)
                {
                    if (ownedStartupId is not null && ReadString(status.RootElement, "ownedStartupId") != ownedStartupId)
                        continue;
                    if (!status.RootElement.TryGetProperty("capabilities", out var capabilities)
                        || !ReadBool(capabilities, "deviceHistoryGrouping")) continue;
                    var canPrompt = ReadBool(capabilities, "prompt")
                        && ReadString(status.RootElement, "deviceAgentPreset") == "halo-device";
                    if (requireDeviceCommands && !canPrompt) continue;
                    return descriptor with
                    {
                        CanPrompt = canPrompt, DeviceSessionId = ReadString(status.RootElement, "deviceSessionId"),
                        Capabilities = ReadCapabilities(capabilities)
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or JsonException or HttpRequestException
                or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException
                or OperationCanceledException or UnauthorizedAccessException)
            {
                // Stale descriptors and half-written startup files are not live
                // hosts. Never log the descriptor or its bearer token.
            }
        }
        return null;
    }

    private async Task<JsonDocument> RequestAsync(BridgeDescriptor descriptor, string path, CancellationToken cancellationToken,
        object? body = null, int timeoutSeconds = 10)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(new Uri(descriptor.BaseUrl), path));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var code = string.Empty;
            // Read only a bounded structured code. Raw DSH diagnostics may
            // contain private paths or service details and do not belong in UI.
            if (response.Content.Headers.ContentLength is null or <= 8192)
            {
                await using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var errorBuffer = new MemoryStream();
                var chunk = new byte[1024];
                int count;
                while ((count = await errorStream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (errorBuffer.Length + count > 8192) break;
                    await errorBuffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                }
                try
                {
                    using var errorDocument = JsonDocument.Parse(errorBuffer.ToArray());
                    if (errorDocument.RootElement.ValueKind == JsonValueKind.Object
                        && errorDocument.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        code = ReadString(error, "code");
                }
                catch (JsonException) { }
            }
            throw new BridgeRequestException(response.StatusCode, code);
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("DSH 返回的数据过大，请缩小历史页。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("DSH 返回的数据超过大小限制。");
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private string GetDiscoveryDirectory(string home)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(discoveryRoot, key);
    }

    private void SetEndpoint(BridgeDescriptor descriptor)
    {
        lock (stateGate) endpoint = descriptor;
    }

    private void MarkDisconnected(string message, bool retainEndpoint = false)
    {
        string home;
        string? profile = null;
        try
        {
            var options = optionsFactory();
            // Settings may change while a request fails. Publish the current
            // scope, never the scope captured by that request before awaiting.
            home = ResolveHome(options.Home);
            profile = options.Profile;
        }
        catch { home = string.Empty; }
        lock (stateGate)
        {
            if (!retainEndpoint || endpoint is null || !SameHome(endpoint.Home, home) || endpoint.Profile != profile)
                endpoint = null;
        }
        var previous = Current;
        var target = ReadSavedTarget(home);
        Publish(new(false, string.Empty, message,
            listedHome is not null && SameHome(listedHome, home) && listedProfile == profile
                ? previous.Sessions.Select(session => session with { RuntimeStatus = "unknown" }).ToArray() : [], target)
                { Home = home, DeviceSessionId = ReadSavedDeviceSession(home) });
    }

    private void Publish(DshSessionsSnapshot snapshot)
    {
        lock (stateGate) current = snapshot;
        foreach (EventHandler<DshSessionsSnapshot> handler in Changed?.GetInvocationList() ?? [])
        {
            try { handler(this, snapshot); }
            catch (Exception exception) { Debug.WriteLine($"[DshSessions] 状态订阅失败：{exception.Message}"); }
        }
    }

    private static DshSessionSummary? ReadSavedTarget(string home)
    {
        if (string.IsNullOrWhiteSpace(DisplayFeatureProfile.DshVoiceTargetSessionId)
            || !SameHome(DisplayFeatureProfile.DshVoiceTargetHomePath, home)) return null;
        return new(DisplayFeatureProfile.DshVoiceTargetSessionId, DisplayFeatureProfile.DshVoiceTargetTitle,
            DisplayFeatureProfile.DshVoiceTargetWorkingDirectory, DateTimeOffset.MinValue, "unknown");
    }

    private string ReadSavedDeviceSession(string home)
    {
        var profile = optionsFactory().Profile;
        var path = GetDeviceIdentityPath(home, profile);
        var durable = DshDeviceSessionIdentity.Read(path, home, profile);
        if (durable.Length > 0) return durable;
        var previous = SameHome(DisplayFeatureProfile.DshDeviceSessionHomePath, home)
            && DisplayFeatureProfile.DshDeviceSessionProfileName == profile
            && Guid.TryParse(DisplayFeatureProfile.DshDeviceSessionId, out _)
            ? DisplayFeatureProfile.DshDeviceSessionId : string.Empty;
        if (previous.Length > 0)
            DshDeviceSessionIdentity.Save(path, home, profile, previous);
        return previous;
    }

    private string GetDeviceIdentityPath(string home, string profile)
        => Path.Combine(GetDiscoveryDirectory(home), "device-session-"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile))).ToLowerInvariant() + ".state");

    private async Task StopOwnedHostAsync()
    {
        Process? process;
        Process? bridge;
        DshOwnedProcessJob? job;
        Task? output;
        Task? error;
        lock (stateGate)
        {
            process = ownedHost;
            bridge = ownedBridgeHost;
            job = ownedJob;
            ownedHost = null;
            ownedBridgeHost = null;
            ownedJob = null;
            ownedHome = null;
            output = stdoutPump;
            error = stderrPump;
            stdoutPump = stderrPump = null;
        }
        job?.Dispose();
        foreach (var owned in new[] { bridge, process }.OfType<Process>())
        {
            TryKill(owned);
            try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            owned.Dispose();
        }
        try { await Task.WhenAll(output ?? Task.CompletedTask, error ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static bool ProcessIsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }

    private static bool IsAllowedEndpoint(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 0
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";

    private static bool IsValidToken(string value)
        => value.Length is >= 32 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ResolveHome(string value)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(value)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : value.Trim().Trim('"'))));

    private static bool SameHome(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(ResolveHome(left), ResolveHome(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DshSessionCapabilities ReadCapabilities(JsonElement value)
        => new(ReadBool(value, "sessionPrompt"), ReadBool(value, "sessionCreate"),
            ReadBool(value, "sessionRename"), ReadBool(value, "sessionArchive"), ReadBool(value, "sessionRestore"))
        {
            CanCreateTasks = ReadBool(value, "taskCreate"),
            CanPromptTasks = ReadBool(value, "taskPrompt"),
            CanMonitorTasks = ReadBool(value, "taskMonitor"),
            CanRespondToTasks = ReadBool(value, "taskRespond"),
            CanCancelTasks = ReadBool(value, "taskCancel"),
            CanAdoptTasks = ReadBool(value, "taskAdopt")
        };

    private static bool IsAdmissionRejection(string code)
        => code is "device_session_conflict" or "device_session_unavailable" or "session_unavailable"
            or "request_id_conflict" or "session_not_found" or "dsh_rejected_command" or "invalid_command"
            or "invalid_session_id" or "invalid_request_id" or "device_command_queue_full" or "session_command_queue_full"
            or "prompt_not_supported" or "session_prompt_not_supported" or "session_archived" or "unauthorized"
            or "session_is_device_control" or "dsh_rejected_request";

    private static string DescribeRejection(string code) => code switch
    {
        "device_session_conflict" => "服务已绑定另一音箱会话，请重新连接后核对固定会话。",
        "device_session_unavailable" or "session_unavailable" => "原会话暂不可用，可能被占用或工作目录不匹配，请刷新或重新连接。",
        "request_id_conflict" => "消息请求编号冲突，请核对原会话。",
        "session_not_found" => "会话不存在，请刷新列表。",
        "session_archived" => "请先恢复已归档会话。",
        "device_command_queue_full" or "session_command_queue_full" => "等待中的消息过多，请稍后手动发送。",
        "prompt_not_supported" or "session_prompt_not_supported" => "当前 DSH 服务不支持发送，请更新并重新连接。",
        "session_is_device_control" => "请从音箱控制入口发送设备口令。",
        "unauthorized" => "会话服务鉴权失败，请重新连接。",
        _ => "DSH 拒绝了消息，请检查输入并刷新会话。"
    };

    private static string[] ReadStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray() : [];

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
        => DateTimeOffset.TryParse(ReadString(element, name), out var date) ? date : null;

    private static string? ResolveBridgeAsset(string name)
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Assets", "Integrations", "DeepSeekHarness", "SessionBridge", name);
        if (File.Exists(packaged)) return packaged;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; level < 9 && directory is not null; level++, directory = directory.Parent)
        {
            var source = Path.Combine(directory.FullName, "integrations", "deepseek-harness", "halo-session-bridge", name);
            if (File.Exists(source)) return source;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        Process? process;
        Process? bridge;
        DshOwnedProcessJob? job;
        lock (stateGate)
        {
            if (disposed) return;
            disposed = true;
            process = ownedHost;
            bridge = ownedBridgeHost;
            job = ownedJob;
            ownedHost = ownedBridgeHost = null;
            ownedJob = null;
        }
        shutdown.Cancel();
        // Do not wait for the UI synchronization context during application exit.
        job?.Dispose();
        foreach (var owned in new[] { bridge, process }.OfType<Process>())
        {
            TryKill(owned);
            try { owned.WaitForExit(3000); } catch { }
            owned.Dispose();
        }
        client.Dispose();
    }

    private sealed record BridgeDescriptor(string Home, string Profile, int Pid, string BaseUrl, string Token, string OwnedStartupId)
    {
        public bool CanPrompt { get; init; }
        public string DeviceSessionId { get; init; } = string.Empty;
        public DshSessionCapabilities Capabilities { get; init; } = new();
    }

    private sealed class BridgeRequestException(System.Net.HttpStatusCode status, string code)
        : HttpRequestException($"会话服务返回 HTTP {(int)status}。", null, status)
    {
        public string Code { get; } = code;
    }
}

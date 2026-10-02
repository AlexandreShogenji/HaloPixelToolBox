using HaloPixelToolBox.Models;
using System.Text.Json;

namespace HaloPixelToolBox.Services;

public interface IDshTaskSessionClient
{
    DshSessionsSnapshot Current { get; }
    event EventHandler<DshSessionsSnapshot>? Changed;
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<DshSessionSummary> CreateTaskSessionAsync(DshTaskStartRequest request, CancellationToken cancellationToken = default);
    Task AdoptTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<DshTaskSubmission> SubmitTaskPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default);
    Task<DshTaskRemoteState> ReadTaskStateAsync(string sessionId, CancellationToken cancellationToken = default);
    Task RespondTaskInteractionAsync(string sessionId, string interactionId, string type,
        string? outcome, IReadOnlyDictionary<string, string>? answers, CancellationToken cancellationToken = default);
    Task CancelTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task ReleaseTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    void SelectVoiceTarget(DshSessionSummary session);
    void ClearVoiceTarget();
}

public sealed partial class DshSessionsService : IDshTaskSessionClient
{
    private readonly SemaphoreSlim taskRequestGate = new(1, 1);

    public async Task<DshSessionSummary> CreateTaskSessionAsync(DshTaskStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var title = ValidateTitle(request.Title);
        var requestedDirectory = request.BaseDirectory.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(requestedDirectory) || requestedDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("任务根目录须为本机绝对路径。");
        var baseDirectory = Path.GetFullPath(requestedDirectory);
        if (request.Prompt.Length > 4096)
            throw new ArgumentException("任务指令不能超过 4096 字符。");
        var folderTitle = new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(40).ToArray()).Trim().TrimEnd('.');
        if (folderTitle.Length == 0) folderTitle = "任务";
        if (System.Text.RegularExpressions.Regex.IsMatch(folderTitle, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) folderTitle = "任务-" + folderTitle;
        var directoryName = folderTitle + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await taskRequestGate.WaitAsync(linked.Token);
        try
        {
            var connected = RequireSessionEndpoint(null, caps => caps.CanCreateTasks);
            using var document = await RequestAsync(connected, "v1/tasks/create", linked.Token,
                new { baseDirectory, directoryName, title }, timeoutSeconds: 25);
            ValidateReplyScope(connected);
            linked.Token.ThrowIfCancellationRequested();
            var item = document.RootElement.GetProperty("session");
            var id = ReadString(item, "id");
            var cwd = ReadString(item, "workingDirectory");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || ReadBool(item, "isDeviceControl")
                || ReadBool(item, "isArchived") || !SameHome(cwd, Path.Combine(baseDirectory, directoryName)))
                throw new InvalidDataException("新任务的会话或目录不匹配，请刷新核对，未自动重建。");
            var session = new DshSessionSummary(id, ReadString(item, "title"), cwd,
                ReadDate(item, "updatedAt") ?? DateTimeOffset.Now, ReadString(item, "runtimeStatus"));
            Publish(Current with { Sessions = Current.Sessions.Where(s => s.Id != id).Append(session).ToArray() });
            return session;
        }
        finally { taskRequestGate.Release(); }
    }

    public async Task AdoptTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanAdoptTasks);
        using var document = await RequestAsync(connected, "v1/tasks/adopt", linked.Token, new { sessionId }, 25);
        ValidateTaskReply(document.RootElement, sessionId);
        ValidateReplyScope(connected);
    }

    public async Task<DshTaskSubmission> SubmitTaskPromptAsync(string sessionId, string prompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4096)
            throw new ArgumentException("任务消息须为 1–4096 字符。");
        var requestId = Guid.NewGuid().ToString("D");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanPromptTasks);
        using var document = await RequestAsync(connected, "v1/tasks/prompt", linked.Token,
            new { sessionId, requestId, prompt = prompt.Trim() }, 25);
        var root = document.RootElement;
        ValidateTaskReply(root, sessionId);
        ValidateReplyScope(connected);
        if (ReadString(root, "requestId") != requestId || !ReadBool(root, "accepted"))
            throw new InvalidDataException("未确认任务消息已接受，请查看会话；不会自动重发。");
        ScheduleCommandRefresh();
        return new(sessionId, requestId, true);
    }

    public async Task<DshTaskRemoteState> ReadTaskStateAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanMonitorTasks);
        using var document = await RequestAsync(connected, "v1/tasks/status?sessionId=" + Uri.EscapeDataString(sessionId), linked.Token);
        var root = document.RootElement;
        ValidateTaskReply(root, sessionId);
        ValidateReplyScope(connected);
        var interactions = new List<DshTaskInteraction>();
        if (root.TryGetProperty("pendingInteractions", out var pending))
        {
            if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > 16)
                throw new InvalidDataException("任务互动列表无效。");
            foreach (var interaction in pending.EnumerateArray())
            {
                var id = ReadString(interaction, "id");
                var type = ReadString(interaction, "type");
                if (id.Length is 0 or > 256 || type is not ("approval" or "question"))
                    throw new InvalidDataException("任务互动身份无效。");
                var questions = new List<DshTaskQuestion>();
                if (interaction.TryGetProperty("questions", out var questionList))
                {
                    if (questionList.ValueKind != JsonValueKind.Array || questionList.GetArrayLength() > 8)
                        throw new InvalidDataException("任务问题列表无效。");
                    foreach (var question in questionList.EnumerateArray())
                    {
                        var questionId = ReadString(question, "id");
                        if (questionId.Length is 0 or > 200 || questions.Any(q => q.Id == questionId))
                            throw new InvalidDataException("任务问题身份无效。");
                        var choices = new List<DshTaskOption>();
                        if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                        {
                            if (options.GetArrayLength() > 32) throw new InvalidDataException("任务选项过多。");
                            foreach (var option in options.EnumerateArray())
                                choices.Add(new(ReadString(option, "label"), ReadString(option, "description")));
                        }
                        questions.Add(new(questionId, ReadString(question, "header"), ReadString(question, "question"),
                            choices, ReadBool(question, "multiSelect")));
                    }
                }
                if (type == "question" && questions.Count == 0) throw new InvalidDataException("待回答请求缺少问题。");
                interactions.Add(new(id, type, ReadString(interaction, "toolName"), ReadString(interaction, "reason"),
                    questions, ReadDate(interaction, "createdAt")));
            }
        }
        var revision = root.TryGetProperty("revision", out var revisionValue) ? revisionValue.ToString() : string.Empty;
        return new(sessionId, ReadString(root, "taskStatus"), ReadString(root, "runtimeStatus"), revision,
            ReadString(root, "finalText"), ReadString(root, "lastTurnReason"), ReadBool(root, "hasSubmittedPrompt"), interactions)
            { ProgressText = ReadString(root, "progressText") };
    }

    public async Task RespondTaskInteractionAsync(string sessionId, string interactionId, string type,
        string? outcome, IReadOnlyDictionary<string, string>? answers, CancellationToken cancellationToken = default)
    {
        if (interactionId.Length is 0 or > 256 || type is not ("approval" or "question")
            || (type == "approval" && outcome is not ("allowed-once" or "rejected")))
            throw new ArgumentException("任务回答类型或请求身份无效。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanRespondToTasks);
        var body = type == "approval"
            ? (object)new { sessionId, interactionId, type, outcome }
            : new { sessionId, interactionId, type, answers };
        using var document = await RequestAsync(connected, "v1/tasks/interactions/respond", linked.Token, body, 25);
        ValidateTaskReply(document.RootElement, sessionId);
        ValidateReplyScope(connected);
        if (ReadString(document.RootElement, "interactionId") != interactionId || !ReadBool(document.RootElement, "accepted"))
            throw new InvalidDataException("未确认互动回答被接受，请核对原任务，不会自动重发。");
    }

    public async Task CancelTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanCancelTasks);
        using var document = await RequestAsync(connected, "v1/tasks/cancel", linked.Token, new { sessionId }, 25);
        ValidateTaskReply(document.RootElement, sessionId);
        ValidateReplyScope(connected);
        if (!ReadBool(document.RootElement, "accepted"))
            throw new InvalidDataException("DSH 未确认取消请求。");
    }

    private static void ValidateTaskReply(JsonElement root, string sessionId)
    {
        if (root.ValueKind != JsonValueKind.Object || ReadString(root, "sessionId") != sessionId)
            throw new InvalidDataException("DSH 返回了其他任务的结果。");
    }

    public async Task ReleaseTaskSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var connected = RequireSessionEndpoint(sessionId, caps => caps.CanMonitorTasks);
        using var document = await RequestAsync(connected, "v1/tasks/release", linked.Token, new { sessionId }, 25);
        ValidateTaskReply(document.RootElement, sessionId);
        ValidateReplyScope(connected);
        if (!ReadBool(document.RootElement, "released")) throw new InvalidDataException("未确认任务监控已释放。");
    }
}

namespace HaloPixelToolBox.Models;

public sealed record DshSessionSummary(
    string Id,
    string Title,
    string WorkingDirectory,
    DateTimeOffset UpdatedAt,
    string RuntimeStatus)
{
    public bool IsDeviceControl { get; init; }
    public string DeviceControlKind { get; init; } = string.Empty;
    public bool IsArchived { get; init; }

    public string StatusText => RuntimeStatus switch
    {
        "running" => "运行中",
        "idle" => "空闲",
        "detached" => "当前服务未加载",
        _ => "状态未知"
    };
}

public sealed record DshHistoryEntry(
    long Sequence,
    string Role,
    string Text,
    DateTimeOffset? CreatedAt,
    bool Truncated = false)
{
    public string Kind { get; init; } = "conversation";
    public string SessionId { get; init; } = string.Empty;
}

public sealed record DshHistoryPage(
    IReadOnlyList<DshHistoryEntry> Entries,
    long? BeforeSeq,
    bool HasMore,
    bool Truncated = false);

public sealed record DshDeviceHistoryPage(
    IReadOnlyList<DshHistoryEntry> Entries,
    string? BeforeCursor,
    bool HasMore,
    bool Truncated = false);

/// <summary>The verified device conversation that owns a short spoken follow-up.</summary>
public sealed record DshDeviceReplyTarget(string SessionId, string Home, string Profile);

public sealed record DshDeviceCommandResult(
    bool Success,
    string Message,
    string FinalText,
    string SessionId,
    IReadOnlyList<string> CalledTools,
    IReadOnlyList<string> SuccessfulTools)
{
    public bool Accepted { get; init; }
    public bool Completed { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public DshDeviceReplyTarget? ReplyTarget { get; init; }
}

public sealed record DshSessionCapabilities(
    bool CanSendMessages = false,
    bool CanCreateSessions = false,
    bool CanRenameSessions = false,
    bool CanArchiveSessions = false,
    bool CanRestoreSessions = false)
{
    public bool CanCreateTasks { get; init; }
    public bool CanPromptTasks { get; init; }
    public bool CanMonitorTasks { get; init; }
    public bool CanRespondToTasks { get; init; }
    public bool CanCancelTasks { get; init; }
    public bool CanAdoptTasks { get; init; }
}

public sealed record DshSessionsSnapshot(
    bool IsConnected,
    string HostUrl,
    string Message,
    IReadOnlyList<DshSessionSummary> Sessions,
    DshSessionSummary? VoiceTarget)
{
    public string Home { get; init; } = string.Empty;
    public string DeviceSessionId { get; init; } = string.Empty;
    public DshSessionCapabilities Capabilities { get; init; } = new();

    public static DshSessionsSnapshot Initial { get; } = new(
        false, string.Empty, "点击“启动并连接 DSH”查看本机会话。", [], null);
}

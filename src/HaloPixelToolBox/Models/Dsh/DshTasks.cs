namespace HaloPixelToolBox.Models;

public sealed record DshTaskOption(string Label, string Description);
public sealed record DshTaskQuestion(string Id, string Header, string Question,
    IReadOnlyList<DshTaskOption> Options, bool MultiSelect = false);
public sealed record DshTaskInteraction(string Id, string Type, string ToolName, string Reason,
    IReadOnlyList<DshTaskQuestion> Questions, DateTimeOffset? CreatedAt = null);
public sealed record DshTaskRemoteState(string SessionId, string TaskStatus, string RuntimeStatus,
    string Revision, string FinalText, string LastTurnReason, bool HasSubmittedPrompt,
    IReadOnlyList<DshTaskInteraction> PendingInteractions)
{
    public string ProgressText { get; init; } = string.Empty;
}
public sealed record DshTaskSubmission(string SessionId, string RequestId, bool Accepted);
public sealed record DshTaskStartRequest(string BaseDirectory, string Title, string Prompt);
public sealed record DshTaskVoiceResult(bool Handled, string Message)
{
    public bool ListenForReply { get; init; }
}

public sealed record DshTaskSnapshot(string SessionId, string Title, string WorkingDirectory,
    string State, string StatusText, string Detail, string FinalText,
    IReadOnlyList<DshTaskInteraction> PendingInteractions, bool IsMonitoring, bool IsBusy)
{
    public string VoiceInteractionId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> VoiceAnswers { get; init; } = new Dictionary<string, string>();
    public long VoiceAnswerRevision { get; init; }
    // Used only on a transient feedback request, never persisted as task state.
    public long? DisplayRequestRevision { get; init; }
    public static DshTaskSnapshot Initial { get; } = new("", "", "", "idle", "未监控任务", "", "", [], false, false);
    public bool NeedsAttention => PendingInteractions.Count > 0;
    public bool CanRespond => IsMonitoring && !IsBusy && NeedsAttention;
}

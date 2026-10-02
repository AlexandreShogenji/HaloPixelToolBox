namespace HaloPixelToolBox.Services;

public enum VoiceAgentPhase
{
    Stopped, Starting, LoadingModel, ListeningForWake, Acknowledging,
    ListeningForCommand, Recognizing, Executing, CoolingDown, Stopping, Error
}

public sealed record VoiceAgentSnapshot(
    VoiceAgentPhase Phase, string Status, string Detail, string LastTranscript, string LastResponse, bool IsRunning)
{
    public static VoiceAgentSnapshot Initial { get; } = new(VoiceAgentPhase.Stopped, "语音 Agent 未启动", "使用保存配置", "", "", false);
}

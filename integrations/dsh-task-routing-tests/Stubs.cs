using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Services
{
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

}
namespace HaloPixelToolBox.Profiles.CrossVersionProfiles
{
    internal static class DisplayFeatureProfile
    {
        public static string DshTaskRootDirectory { get; set; } = "C:/tasks";
        public static string DshHomePath { get; set; } = "C:/test-home";
        public static string DshProfileName { get; set; } = "test-profile";
    }
}
internal static class ProbeAssert
{
    public static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}

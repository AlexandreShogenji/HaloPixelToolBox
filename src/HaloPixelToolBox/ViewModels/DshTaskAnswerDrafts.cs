using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.ViewModels;

/// <summary>In-memory drafts scoped to the current task and unchanged pending requests.</summary>
public sealed class DshTaskAnswerDrafts
{
    private sealed record Draft(DshTaskInteraction Interaction, IReadOnlyList<DshTaskQuestionAnswer> Answers);
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private string? currentScope;
    private string? currentSessionId;

    public void Synchronize(string scope, DshTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        UseContext(scope, snapshot.SessionId);
        if (!snapshot.IsMonitoring || string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            drafts.Clear();
            return;
        }
        // A disconnected host cannot prove a pending request has expired. Keep the
        // draft until the same task returns with an authoritative request list.
        if (snapshot.State == "disconnected") return;

        foreach (var pair in drafts.ToArray())
        {
            if (!snapshot.PendingInteractions.Any(interaction
                => DshTaskInteractionIdentity.Matches(pair.Value.Interaction, interaction)))
                drafts.Remove(pair.Key);
        }
    }

    public IReadOnlyList<DshTaskQuestionAnswer> GetOrCreate(string scope, string sessionId,
        DshTaskInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(interaction.Id);
        UseContext(scope, sessionId);
        if (drafts.TryGetValue(interaction.Id, out var draft)
            && DshTaskInteractionIdentity.Matches(draft.Interaction, interaction))
            return draft.Answers;

        // A snapshot also detects changes to an externally mutable Questions/Options list.
        var schema = DshTaskInteractionIdentity.Capture(interaction);
        var answers = Array.AsReadOnly(schema.Questions.Select(question => new DshTaskQuestionAnswer(question)).ToArray());
        drafts[interaction.Id] = new Draft(schema, answers);
        return answers;
    }

    public void Remove(string scope, string sessionId, string interactionId)
    {
        if (currentScope == scope && currentSessionId == sessionId) drafts.Remove(interactionId);
    }

    public void Clear()
    {
        drafts.Clear();
        currentScope = null;
        currentSessionId = null;
    }

    private void UseContext(string scope, string sessionId)
    {
        if (currentScope == scope && currentSessionId == sessionId) return;
        drafts.Clear();
        currentScope = scope;
        currentSessionId = sessionId;
    }
}

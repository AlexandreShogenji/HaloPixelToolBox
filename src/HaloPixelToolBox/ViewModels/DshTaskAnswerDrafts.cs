using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.ViewModels;

/// <summary>In-memory drafts scoped to the current task and unchanged pending requests.</summary>
public sealed class DshTaskAnswerDrafts
{
    private sealed record Draft(DshTaskInteraction Interaction, IReadOnlyList<DshTaskQuestionAnswer> Answers);
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private string? currentScope;
    private string? currentSessionId;
    private long voiceAnswerRevision;
    private DshTaskInteraction? voiceInteraction;
    private IReadOnlyDictionary<string, string> voiceAnswers = new Dictionary<string, string>();

    public void Synchronize(string scope, DshTaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        UseContext(scope, snapshot.SessionId);
        if (!snapshot.IsMonitoring || string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            drafts.Clear();
            ResetVoiceContext();
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
        if (voiceInteraction is not null && !snapshot.PendingInteractions.Any(interaction
            => DshTaskInteractionIdentity.Matches(voiceInteraction, interaction)))
            ForgetVoiceAnswers();
        SynchronizeVoice(snapshot);
    }

    public IReadOnlyList<DshTaskQuestionAnswer> GetOrCreate(string scope, string sessionId,
        DshTaskInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(interaction.Id);
        UseContext(scope, sessionId);
        if (voiceInteraction?.Id == interaction.Id && !DshTaskInteractionIdentity.Matches(voiceInteraction, interaction))
            ForgetVoiceAnswers();
        if (drafts.TryGetValue(interaction.Id, out var draft)
            && DshTaskInteractionIdentity.Matches(draft.Interaction, interaction))
            return draft.Answers;

        // A snapshot also detects changes to an externally mutable Questions/Options list.
        var schema = DshTaskInteractionIdentity.Capture(interaction);
        var answers = Array.AsReadOnly(schema.Questions.Select(question => new DshTaskQuestionAnswer(question)).ToArray());
        drafts[interaction.Id] = new Draft(schema, answers);
        if (DshTaskInteractionIdentity.Matches(voiceInteraction, schema)) ImportVoiceAnswers(answers, voiceAnswers);
        return answers;
    }

    public void Remove(string scope, string sessionId, string interactionId)
    {
        if (currentScope != scope || currentSessionId != sessionId) return;
        drafts.Remove(interactionId);
        if (voiceInteraction?.Id == interactionId) ForgetVoiceAnswers();
    }

    public void Clear()
    {
        drafts.Clear();
        currentScope = null;
        currentSessionId = null;
        ResetVoiceContext();
    }

    private void UseContext(string scope, string sessionId)
    {
        if (currentScope == scope && currentSessionId == sessionId) return;
        drafts.Clear();
        ResetVoiceContext();
        currentScope = scope;
        currentSessionId = sessionId;
    }

    private void SynchronizeVoice(DshTaskSnapshot snapshot)
    {
        // Polling the same voice snapshot must not replace subsequent keyboard edits.
        if (snapshot.VoiceAnswerRevision <= voiceAnswerRevision) return;
        voiceAnswerRevision = snapshot.VoiceAnswerRevision;
        if (string.IsNullOrEmpty(snapshot.VoiceInteractionId))
        {
            if (voiceInteraction is not null && drafts.TryGetValue(voiceInteraction.Id, out var previous)
                && DshTaskInteractionIdentity.Matches(voiceInteraction, previous.Interaction))
                ImportVoiceAnswers(previous.Answers, new Dictionary<string, string>());
            ForgetVoiceAnswers();
            return;
        }

        var interaction = snapshot.PendingInteractions.FirstOrDefault(item => item.Id == snapshot.VoiceInteractionId
            && item.Type == "question");
        if (interaction is null) { ForgetVoiceAnswers(); return; }
        voiceInteraction = DshTaskInteractionIdentity.Capture(interaction);
        // Capture values too: mutating a dictionary without a revision cannot update a UI draft.
        voiceAnswers = voiceInteraction.Questions.Where(question => snapshot.VoiceAnswers.ContainsKey(question.Id))
            .ToDictionary(question => question.Id, question => snapshot.VoiceAnswers[question.Id], StringComparer.Ordinal);
        if (drafts.TryGetValue(interaction.Id, out var draft)
            && DshTaskInteractionIdentity.Matches(voiceInteraction, draft.Interaction))
            ImportVoiceAnswers(draft.Answers, voiceAnswers);
    }

    private static void ImportVoiceAnswers(IReadOnlyList<DshTaskQuestionAnswer> answers, IReadOnlyDictionary<string, string> values)
    {
        foreach (var answer in answers)
            answer.ImportAnswer(values.TryGetValue(answer.Question.Id, out var value) ? value : string.Empty);
    }

    private void ForgetVoiceAnswers()
    {
        voiceInteraction = null;
        voiceAnswers = new Dictionary<string, string>();
    }

    private void ResetVoiceContext()
    {
        voiceAnswerRevision = 0;
        ForgetVoiceAnswers();
    }
}

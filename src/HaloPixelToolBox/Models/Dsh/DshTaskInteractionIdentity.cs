namespace HaloPixelToolBox.Models;

/// <summary>Compares the request content rather than the references of its question lists.</summary>
public static class DshTaskInteractionIdentity
{
    public static DshTaskInteraction Capture(DshTaskInteraction interaction) => interaction with
    {
        Questions = Array.AsReadOnly(interaction.Questions.Select(question => question with
        {
            Options = Array.AsReadOnly(question.Options.ToArray())
        }).ToArray())
    };

    public static bool Matches(DshTaskInteraction? left, DshTaskInteraction? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Id != right.Id || left.Type != right.Type || left.ToolName != right.ToolName
            || left.Reason != right.Reason || left.CreatedAt != right.CreatedAt
            || left.Questions.Count != right.Questions.Count)
            return false;

        for (var index = 0; index < left.Questions.Count; index++)
        {
            var first = left.Questions[index];
            var second = right.Questions[index];
            if (first.Id != second.Id || first.Header != second.Header || first.Question != second.Question
                || first.MultiSelect != second.MultiSelect || first.Options.Count != second.Options.Count)
                return false;

            for (var option = 0; option < first.Options.Count; option++)
            {
                if (first.Options[option].Label != second.Options[option].Label
                    || first.Options[option].Description != second.Options[option].Description)
                    return false;
            }
        }
        return true;
    }
}

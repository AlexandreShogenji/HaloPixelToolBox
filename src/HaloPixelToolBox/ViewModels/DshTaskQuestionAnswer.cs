using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.ViewModels;

/// <summary>A local, explicit answer to one pending DSH question.</summary>
public sealed class DshTaskQuestionAnswer
{
    private readonly bool[] selectedOptions;
    private string freeText = string.Empty;

    public DshTaskQuestionAnswer(DshTaskQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Question = question with { Options = Array.AsReadOnly(question.Options.ToArray()) };
        selectedOptions = new bool[Question.Options.Count];
    }

    public DshTaskQuestion Question { get; }
    public event EventHandler? Changed;

    public string FreeText
    {
        get => freeText;
        set
        {
            value ??= string.Empty;
            var changed = freeText != value;
            freeText = value;
            if (value.Length > 0)
            {
                for (var index = 0; index < selectedOptions.Length; index++)
                {
                    changed |= selectedOptions[index];
                    selectedOptions[index] = false;
                }
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsOptionSelected(int index)
        => index >= 0 && index < selectedOptions.Length && selectedOptions[index];

    public void SetOptionSelected(int index, bool selected)
    {
        if (index < 0 || index >= selectedOptions.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var changed = freeText.Length > 0;
        freeText = string.Empty;
        if (selected && !Question.MultiSelect)
        {
            for (var option = 0; option < selectedOptions.Length; option++)
            {
                var newValue = option == index;
                changed |= selectedOptions[option] != newValue;
                selectedOptions[option] = newValue;
            }
        }
        else
        {
            changed |= selectedOptions[index] != selected;
            selectedOptions[index] = selected;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Answer
    {
        get
        {
            var text = freeText.Trim();
            if (text.Length > 0) return text;
            return string.Join(" | ", Question.Options.Where((_, index) => selectedOptions[index])
                .Select(option => option.Label.Trim()));
        }
    }

    public bool HasAnswer => Answer.Length is >= 1 and <= 4096;
}

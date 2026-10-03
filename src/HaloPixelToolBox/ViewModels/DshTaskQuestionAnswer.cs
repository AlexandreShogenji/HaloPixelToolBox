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

    /// <summary>Import an explicit answer from voice without guessing an option or notifying midway.</summary>
    public void ImportAnswer(string value)
    {
        var text = (value ?? string.Empty).Trim();
        var selections = new bool[selectedOptions.Length];
        var exact = Question.Options.Select((option, index) => (option, index))
            .Where(item => item.option.Label.Trim() == text).Select(item => item.index).ToArray();
        if (text.Length > 0 && exact.Length == 1)
        {
            selections[exact[0]] = true;
            text = string.Empty;
        }
        else if (text.Length > 0 && Question.MultiSelect)
        {
            var complete = true;
            foreach (var token in text.Split(" | ", StringSplitOptions.None))
            {
                var matches = Question.Options.Select((option, index) => (option, index))
                    .Where(item => item.option.Label.Trim() == token.Trim()).Select(item => item.index).ToArray();
                if (token.Trim().Length == 0 || matches.Length != 1) { complete = false; break; }
                selections[matches[0]] = true;
            }
            if (complete) text = string.Empty;
            else Array.Clear(selections);
        }
        var changed = freeText != text || !selectedOptions.SequenceEqual(selections);
        freeText = text;
        Array.Copy(selections, selectedOptions, selections.Length);
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

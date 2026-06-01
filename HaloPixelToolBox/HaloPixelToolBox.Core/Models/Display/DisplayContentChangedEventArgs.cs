namespace HaloPixelToolBox.Core.Models.Display;

public sealed class DisplayContentChangedEventArgs : EventArgs
{
    public DisplayContentChangedEventArgs(DisplayContentKind contentKind, string? text)
    {
        ContentKind = contentKind;
        Text = text;
    }

    public DisplayContentKind ContentKind { get; }

    public string? Text { get; }
}

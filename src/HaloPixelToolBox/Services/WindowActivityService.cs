namespace HaloPixelToolBox.Services;

/// <summary>
/// Whether the main window can display UI. Background device, voice and task
/// services must not use this signal to suspend their actual work.
/// </summary>
public static class WindowActivityService
{
    // Keep non-window hosts usable until MainWindow supplies its initial state.
    public static bool IsVisible { get; private set; } = true;

    public static event EventHandler<bool>? VisibilityChanged;

    internal static void SetVisibility(bool isVisible)
    {
        if (IsVisible == isVisible)
            return;

        IsVisible = isVisible;
        VisibilityChanged?.Invoke(null, isVisible);
    }
}

namespace HaloPixelToolBox.Utilities.Helpers;

/// <summary>
/// Calculates an initial outer-window rectangle in physical pixels from a logical
/// size, without covering the taskbar or imposing a minimum on smaller screens.
/// </summary>
internal readonly record struct InitialWindowBounds(int X, int Y, int Width, int Height)
{
    private const int PreferredWidthDips = 1280;
    private const int PreferredHeightDips = 860;
    private const int EdgeMarginDips = 24;

    public static InitialWindowBounds Calculate(int workX, int workY, int workWidth, int workHeight, uint dpi)
    {
        if (workWidth <= 0) throw new ArgumentOutOfRangeException(nameof(workWidth));
        if (workHeight <= 0) throw new ArgumentOutOfRangeException(nameof(workHeight));
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var horizontalMargin = (int)Math.Min(Math.Round(EdgeMarginDips * scale), (workWidth - 1) / 2);
        var verticalMargin = (int)Math.Min(Math.Round(EdgeMarginDips * scale), (workHeight - 1) / 2);
        var width = (int)Math.Min(workWidth - 2 * horizontalMargin, Math.Round(PreferredWidthDips * scale));
        var height = (int)Math.Min(workHeight - 2 * verticalMargin, Math.Round(PreferredHeightDips * scale));
        return new(workX + (workWidth - width) / 2, workY + (workHeight - height) / 2, width, height);
    }
}

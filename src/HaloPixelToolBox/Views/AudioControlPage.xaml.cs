namespace HaloPixelToolBox.Views;

public sealed partial class AudioControlPage : Page
{
    public AudioControlPageViewModel ViewModel { get; } = new();

    public AudioControlPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Attach();
        Unloaded += (_, _) => ViewModel.Detach();
    }

    private void HeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => MoveSecond(HeaderGrid, HeaderActionColumn, RefreshButton, e.NewSize.Width < 580);

    private void OverviewGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => MoveSecond(OverviewGrid, OverviewSecondColumn, BackendPanel, e.NewSize.Width < 800);

    private void EffectsHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => MoveSecond(EffectsHeaderGrid, EffectsToggleColumn, EffectsToggle, e.NewSize.Width < 580);

    private void GainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => MoveSecond(GainGrid, GainSecondColumn, BalancePanel, e.NewSize.Width < 580);

    private void PresetGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => MoveSecond(PresetGrid, PresetSecondColumn, SavePresetPanel, e.NewSize.Width < 580);

    private static void MoveSecond(Grid grid, ColumnDefinition? secondColumn, FrameworkElement? second, bool stack)
    {
        if (secondColumn is null || second is null)
            return;
        var compactControl = second is Button or ToggleSwitch;
        secondColumn.Width = stack ? new GridLength(0) : compactControl
            ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(second, stack ? 0 : 1);
        Grid.SetRow(second, stack ? 1 : 0);
        second.HorizontalAlignment = compactControl ? stack ? HorizontalAlignment.Left : HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
    }

    private void Actions_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not StackPanel panel)
            return;
        panel.Orientation = e.NewSize.Width < 360 ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
            child.HorizontalAlignment = panel.Orientation == Orientation.Vertical
                ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}

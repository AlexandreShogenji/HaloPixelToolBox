namespace HaloPixelToolBox.Views;

public sealed partial class CustomSubtitleToolPage : Page
{
    private const double SingleColumnBreakpoint = 680;
    private const double OptionStackBreakpoint = 360;

    private bool? isAlignmentOptionsStacked;
    private bool? isScrollOptionsStacked;

    public CustomSubtitleToolPageViewModel ViewModel { get; } = new();

    public CustomSubtitleToolPage()
    {
        InitializeComponent();
        Loaded += CustomSubtitleToolPage_Loaded;
    }

    private void CustomSubtitleToolPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout(CustomSubtitleContentGrid.ActualWidth);
        UpdateThreeOptionLayout(
            AlignmentOptionsGrid,
            [LeftAlignmentButton, CenterAlignmentButton, RightAlignmentButton],
            AlignmentOptionsGrid.ActualWidth < OptionStackBreakpoint,
            ref isAlignmentOptionsStacked);
        UpdateThreeOptionLayout(
            ScrollOptionsGrid,
            [NoScrollButton, ScrollLeftButton, ScrollRightButton],
            ScrollOptionsGrid.ActualWidth < OptionStackBreakpoint,
            ref isScrollOptionsStacked);
        UpdateTimedSendLayout(TimedSendGrid.ActualWidth);
    }

    private void CustomSubtitleContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double availableWidth)
    {
        if (CustomSubtitleEditorColumn is null ||
            CustomSubtitlePreviewColumn is null ||
            CustomSubtitleEditor is null ||
            CustomSubtitlePreview is null)
            return;

        var useSingleColumn = availableWidth < SingleColumnBreakpoint;
        CustomSubtitleContentGrid.ColumnSpacing = useSingleColumn ? 0 : 14;
        CustomSubtitleEditorColumn.Width = useSingleColumn
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(1.08, GridUnitType.Star);
        CustomSubtitlePreviewColumn.Width = useSingleColumn
            ? new GridLength(0)
            : new GridLength(0.92, GridUnitType.Star);

        Grid.SetRow(CustomSubtitleEditor, 0);
        Grid.SetColumn(CustomSubtitleEditor, 0);
        Grid.SetRow(CustomSubtitlePreview, useSingleColumn ? 1 : 0);
        Grid.SetColumn(CustomSubtitlePreview, useSingleColumn ? 0 : 1);
    }

    private void AlignmentOptionsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RightAlignmentButton is null)
            return;

        UpdateThreeOptionLayout(
            AlignmentOptionsGrid,
            [LeftAlignmentButton, CenterAlignmentButton, RightAlignmentButton],
            e.NewSize.Width < OptionStackBreakpoint,
            ref isAlignmentOptionsStacked);
    }

    private void ScrollOptionsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ScrollRightButton is null)
            return;

        UpdateThreeOptionLayout(
            ScrollOptionsGrid,
            [NoScrollButton, ScrollLeftButton, ScrollRightButton],
            e.NewSize.Width < OptionStackBreakpoint,
            ref isScrollOptionsStacked);
    }

    private static void UpdateThreeOptionLayout(
        Grid grid,
        IReadOnlyList<FrameworkElement> options,
        bool stackVertically,
        ref bool? currentLayout)
    {
        if (currentLayout == stackVertically)
            return;

        currentLayout = stackVertically;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        var columnCount = stackVertically ? 1 : options.Count;
        var rowCount = stackVertically ? options.Count : 1;
        for (var index = 0; index < columnCount; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < rowCount; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < options.Count; index++)
        {
            Grid.SetColumn(options[index], stackVertically ? 0 : index);
            Grid.SetRow(options[index], stackVertically ? index : 0);
        }
    }

    private void TimedSendGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTimedSendLayout(e.NewSize.Width);

    private void UpdateTimedSendLayout(double availableWidth)
    {
        if (TimedSendDelayColumn is null || TimedSendDelayBox is null)
            return;

        var stackVertically = availableWidth < OptionStackBreakpoint;
        TimedSendGrid.ColumnSpacing = stackVertically ? 0 : 14;
        TimedSendDelayColumn.Width = stackVertically
            ? new GridLength(0)
            : new GridLength(100);
        Grid.SetRow(TimedSendToggle, 0);
        Grid.SetColumn(TimedSendToggle, 0);
        Grid.SetRow(TimedSendDelayBox, stackVertically ? 1 : 0);
        Grid.SetColumn(TimedSendDelayBox, stackVertically ? 0 : 1);
    }
}

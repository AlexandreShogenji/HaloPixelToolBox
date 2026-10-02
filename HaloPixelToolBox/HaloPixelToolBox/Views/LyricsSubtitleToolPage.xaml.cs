namespace HaloPixelToolBox.Views;

public sealed partial class LyricsSubtitleToolPage : Page
{
    private const double HeaderSingleColumnBreakpoint = 560;
    private const double ContentSingleColumnBreakpoint = 720;
    private const double ProviderTwoColumnBreakpoint = 620;
    private const double PlaybackOptionsSingleColumnBreakpoint = 360;

    private bool? isProviderTwoColumnLayout;

    public LyricsSubtitleToolPageViewModel ViewModel { get; } = new();

    public LyricsSubtitleToolPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        PlaybackPositionSlider.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerPressed), true);
        PlaybackPositionSlider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerReleased), true);
        PlaybackPositionSlider.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerCaptureLost), true);
        Loaded += LyricsSubtitleToolPage_Loaded;
    }

    private void LyricsSubtitleToolPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLyricsHeaderLayout(LyricsHeaderGrid.ActualWidth);
        UpdateLyricsContentLayout(LyricsContentGrid.ActualWidth);
        UpdateProviderOptionsLayout(ProviderOptionsGrid.ActualWidth);
        UpdatePlaybackOptionsLayout(PlaybackOptionsGrid.ActualWidth);
    }

    private void LyricsHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateLyricsHeaderLayout(e.NewSize.Width);

    private void UpdateLyricsHeaderLayout(double availableWidth)
    {
        if (LyricsHeaderActions is null)
            return;

        var useSingleColumn = availableWidth < HeaderSingleColumnBreakpoint;
        LyricsHeaderGrid.ColumnSpacing = useSingleColumn ? 0 : 16;
        Grid.SetRow(LyricsHeaderActions, useSingleColumn ? 1 : 0);
        Grid.SetColumn(LyricsHeaderActions, useSingleColumn ? 0 : 1);
        LyricsHeaderActions.HorizontalAlignment = useSingleColumn
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        LyricsHeaderActions.Orientation = useSingleColumn
            ? Orientation.Vertical
            : Orientation.Horizontal;
    }

    private void LyricsContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateLyricsContentLayout(e.NewSize.Width);

    private void UpdateLyricsContentLayout(double availableWidth)
    {
        if (LyricsControlsColumn is null ||
            LyricsPreviewColumn is null ||
            LyricsControls is null ||
            LyricsPreview is null)
            return;

        var useSingleColumn = availableWidth < ContentSingleColumnBreakpoint;
        LyricsContentGrid.ColumnSpacing = useSingleColumn ? 0 : 14;
        LyricsControlsColumn.Width = useSingleColumn
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0.9, GridUnitType.Star);
        LyricsPreviewColumn.Width = useSingleColumn
            ? new GridLength(0)
            : new GridLength(1.1, GridUnitType.Star);
        Grid.SetRow(LyricsControls, 0);
        Grid.SetColumn(LyricsControls, 0);
        Grid.SetRow(LyricsPreview, useSingleColumn ? 1 : 0);
        Grid.SetColumn(LyricsPreview, useSingleColumn ? 0 : 1);
    }

    private void ProviderOptionsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateProviderOptionsLayout(e.NewSize.Width);

    private void UpdateProviderOptionsLayout(double availableWidth)
    {
        if (NetEaseProviderButton is null || CustomProviderStatus is null)
            return;

        var useTwoColumns = availableWidth < ProviderTwoColumnBreakpoint;
        if (isProviderTwoColumnLayout == useTwoColumns)
            return;

        isProviderTwoColumnLayout = useTwoColumns;
        ProviderOptionsGrid.ColumnDefinitions.Clear();
        ProviderOptionsGrid.RowDefinitions.Clear();

        var columnCount = useTwoColumns ? 2 : 5;
        var rowCount = useTwoColumns ? 6 : 2;
        for (var index = 0; index < columnCount; index++)
            ProviderOptionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < rowCount; index++)
            ProviderOptionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var buttons = new FrameworkElement[]
        {
            NetEaseProviderButton,
            QqMusicProviderButton,
            SpotifyProviderButton,
            LocalFileProviderButton,
            CustomProviderButton
        };
        var statuses = new FrameworkElement[]
        {
            NetEaseProviderStatus,
            QqMusicProviderStatus,
            SpotifyProviderStatus,
            LocalFileProviderStatus,
            CustomProviderStatus
        };

        for (var index = 0; index < buttons.Length; index++)
        {
            var column = useTwoColumns ? index % 2 : index;
            var buttonRow = useTwoColumns ? (index / 2) * 2 : 0;
            var columnSpan = useTwoColumns && index == buttons.Length - 1 ? 2 : 1;

            Grid.SetColumn(buttons[index], column);
            Grid.SetRow(buttons[index], buttonRow);
            Grid.SetColumnSpan(buttons[index], columnSpan);
            Grid.SetColumn(statuses[index], column);
            Grid.SetRow(statuses[index], buttonRow + 1);
            Grid.SetColumnSpan(statuses[index], columnSpan);
        }
    }

    private void PlaybackOptionsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdatePlaybackOptionsLayout(e.NewSize.Width);

    private void UpdatePlaybackOptionsLayout(double availableWidth)
    {
        if (PlaybackOffsetColumn is null || PlaybackOffsetBox is null)
            return;

        var useSingleColumn = availableWidth < PlaybackOptionsSingleColumnBreakpoint;
        PlaybackOptionsGrid.ColumnSpacing = useSingleColumn ? 0 : 12;
        PlaybackOffsetColumn.Width = useSingleColumn
            ? new GridLength(0)
            : new GridLength(150);
        Grid.SetRow(PlaybackScrollOption, 0);
        Grid.SetColumn(PlaybackScrollOption, 0);
        Grid.SetRow(PlaybackOffsetBox, useSingleColumn ? 1 : 0);
        Grid.SetColumn(PlaybackOffsetBox, useSingleColumn ? 0 : 1);
    }

    private void PlaybackPositionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ViewModel.PreviewPlaybackPositionFromSeconds(e.NewValue);
    }

    private void PlaybackPositionSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.BeginPlaybackSeek();
    }

    private async void PlaybackPositionSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
            await ViewModel.CommitPlaybackPositionFromSecondsAsync(slider.Value);
    }

    private async void PlaybackPositionSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
            await ViewModel.CommitPlaybackPositionFromSecondsAsync(slider.Value);
    }
}

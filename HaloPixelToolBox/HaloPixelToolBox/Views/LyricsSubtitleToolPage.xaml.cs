namespace HaloPixelToolBox.Views;

public sealed partial class LyricsSubtitleToolPage : Page
{
    public LyricsSubtitleToolPageViewModel ViewModel { get; } = new();

    public LyricsSubtitleToolPage()
    {
        InitializeComponent();
        PlaybackPositionSlider.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerPressed), true);
        PlaybackPositionSlider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerReleased), true);
        PlaybackPositionSlider.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(PlaybackPositionSlider_PointerCaptureLost), true);
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

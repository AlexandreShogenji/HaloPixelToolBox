namespace HaloPixelToolBox.Views;

public sealed partial class VideoSubtitleToolPage : Page
{
    public VideoSubtitleToolPageViewModel ViewModel { get; } = new();

    public VideoSubtitleToolPage()
    {
        InitializeComponent();
    }
}

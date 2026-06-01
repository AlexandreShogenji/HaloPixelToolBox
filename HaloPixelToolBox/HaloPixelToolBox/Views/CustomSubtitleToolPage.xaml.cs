namespace HaloPixelToolBox.Views;

public sealed partial class CustomSubtitleToolPage : Page
{
    public CustomSubtitleToolPageViewModel ViewModel { get; } = new();

    public CustomSubtitleToolPage()
    {
        InitializeComponent();
    }
}

namespace HaloPixelToolBox.Views;

public sealed partial class BrowserTranslationSubtitleToolPage : Page
{
    public BrowserTranslationSubtitleToolPageViewModel ViewModel { get; } = BrowserTranslationSubtitleToolPageViewModel.Shared;

    public BrowserTranslationSubtitleToolPage()
    {
        InitializeComponent();
    }
}

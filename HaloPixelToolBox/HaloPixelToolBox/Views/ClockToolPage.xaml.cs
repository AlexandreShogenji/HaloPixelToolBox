namespace HaloPixelToolBox.Views;

public sealed partial class ClockToolPage : Page
{
    public ClockToolPageViewModel ViewModel { get; } = new();

    public ClockToolPage()
    {
        InitializeComponent();
    }
}

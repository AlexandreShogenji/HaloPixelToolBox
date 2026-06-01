namespace HaloPixelToolBox.Views;

public sealed partial class LightingToolPage : Page
{
    private bool isInitializing = true;

    public LightingToolPageViewModel ViewModel { get; } = new();

    public LightingToolPage()
    {
        InitializeComponent();
        isInitializing = false;
    }

    private void AmbientColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!isInitializing)
            ViewModel.SetAmbientColor(args.NewColor);
    }

    private void PixelColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!isInitializing)
            ViewModel.SetPixelColor(args.NewColor);
    }
}

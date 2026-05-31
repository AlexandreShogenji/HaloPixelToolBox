using HaloPixelToolBox.Core.Models;
using Microsoft.UI.Xaml.Navigation;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Views;

public sealed partial class CloudMusicLyricsToolPage : Page
{
    public static CloudMusicLyricsToolPage? Current { get; set; }
    public CloudMusicLyricsToolPageViewModel ViewModel { get; set; } = new();
    public CloudMusicLyricsToolPage()
    {
        Current = this;
        InitializeComponent();
        ViewModel.AutoNavigationParameterService.Initialize(this);
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    private void SendBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.SendText();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.AutoNavigationParameterService.OnParameterChange(e.Parameter);
    }
}

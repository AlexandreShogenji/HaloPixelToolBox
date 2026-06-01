namespace HaloPixelToolBox.Views;

/// <summary>
/// 网易云歌词旧入口，保留用于兼容历史导航。
/// </summary>
public sealed partial class CloudMusicLyricsToolPage : Page
{
    public CloudMusicLyricsToolPageViewModel ViewModel { get; } = new();

    public CloudMusicLyricsToolPage()
    {
        InitializeComponent();
    }
}

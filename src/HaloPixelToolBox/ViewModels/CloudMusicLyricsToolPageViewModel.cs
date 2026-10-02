using CommunityToolkit.Mvvm.ComponentModel;

namespace HaloPixelToolBox.ViewModels;

public partial class CloudMusicLyricsToolPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string statusMessage = "请从左侧导航进入“歌词字幕”页面，那里已预留网易云、QQ 音乐、本地歌词等扩展接口。";
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Lyrics;

namespace HaloPixelToolBox.ViewModels;

public partial class LyricsSubtitleToolPageViewModel : ViewModelBase
{
    private readonly LyricsProviderRegistry providerRegistry = new();
    private readonly HaloPixelDisplayService displayService = new();
    private readonly LyricsProviderKind[] providerMapping =
    [
        LyricsProviderKind.NetEaseCloudMusic,
        LyricsProviderKind.QQMusic,
        LyricsProviderKind.LocalFile,
        LyricsProviderKind.Custom
    ];

    private LyricsTrack? currentTrack;

    public List<string> ProviderNames { get; } = ["网易云音乐（占位）", "QQ 音乐（占位）", "本地歌词（占位）", "自定义 Provider"];

    [ObservableProperty]
    private int selectedProviderIndex;

    [ObservableProperty]
    private string keyword = "示例歌曲";

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private bool enableScroll = true;

    [ObservableProperty]
    private string previewText = "尚未加载歌词";

    [ObservableProperty]
    private string statusMessage = "平台接口已预留，抓取逻辑等待后续适配";

    [RelayCommand]
    private async Task LoadLyricsAsync()
    {
        var providerKind = providerMapping[Math.Clamp(SelectedProviderIndex, 0, providerMapping.Length - 1)];
        var provider = providerRegistry.GetProvider(providerKind);
        currentTrack = await provider.SearchAsync(new LyricsQuery
        {
            Provider = providerKind,
            Keyword = Keyword
        });

        PreviewText = currentTrack is null
            ? "未获取到歌词"
            : string.Join(Environment.NewLine, currentTrack.Lines.Select(line => line.Text));
        StatusMessage = "歌词数据模型已加载，当前为占位数据";
    }

    [RelayCommand]
    private async Task SendCurrentLineAsync()
    {
        if (currentTrack is null)
            await LoadLyricsAsync();

        var position = TimeSpan.FromSeconds(PositionSeconds);
        var cue = currentTrack?.Lines.FirstOrDefault(line => line.Start <= position && line.End >= position)
            ?? currentTrack?.Lines.FirstOrDefault();
        if (cue is null)
        {
            StatusMessage = "没有可发送的歌词";
            return;
        }

        await displayService.SendSubtitleCueAsync(cue, new DisplayTextOptions
        {
            Source = DisplayContentKind.Lyrics,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = EnableScroll ? TextScrollDirection.RightToLeft : TextScrollDirection.None
        });
        StatusMessage = $"已发送歌词：{cue.Text}";
    }
}

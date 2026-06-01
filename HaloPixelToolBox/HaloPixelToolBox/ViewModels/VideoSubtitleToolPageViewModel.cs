using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Subtitles;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.ViewModels;

public partial class VideoSubtitleToolPageViewModel : ViewModelBase
{
    private readonly SubtitleParserFactory parserFactory = new();
    private readonly SubtitleTimelineController timelineController = new(new HaloPixelDisplayService());
    private readonly HaloPixelDisplayService displayService = new();
    private SubtitleDocument? document;

    [ObservableProperty]
    private string videoPath = DisplayFeatureProfile.VideoPath;

    [ObservableProperty]
    private string subtitlePath = DisplayFeatureProfile.VideoSubtitlePath;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private int loadedCueCount;

    [ObservableProperty]
    private string previewText = "导入 SRT/VTT 字幕后会显示预览";

    [ObservableProperty]
    private string statusMessage = "等待导入字幕";

    partial void OnVideoPathChanged(string value) => DisplayFeatureProfile.VideoPath = value;
    partial void OnSubtitlePathChanged(string value) => DisplayFeatureProfile.VideoSubtitlePath = value;

    [RelayCommand]
    private void ParseSubtitle()
    {
        if (string.IsNullOrWhiteSpace(SubtitlePath) || !File.Exists(SubtitlePath))
        {
            StatusMessage = "字幕文件不存在";
            return;
        }

        var parser = parserFactory.GetParser(SubtitlePath);
        document = parser.Parse(SubtitlePath);
        LoadedCueCount = document.Cues.Count;
        PreviewText = string.Join(Environment.NewLine, document.Cues.Take(8).Select(cue => cue.ToString()));
        StatusMessage = $"已解析 {parser.FormatName} 字幕，共 {LoadedCueCount} 条";
    }

    [RelayCommand]
    private async Task SendCurrentCueAsync()
    {
        if (document is null)
            ParseSubtitle();

        var cue = document is null ? null : timelineController.GetCurrentCue(document, TimeSpan.FromSeconds(PositionSeconds));
        if (cue is null)
        {
            StatusMessage = "当前进度没有字幕";
            return;
        }

        await displayService.SendSubtitleCueAsync(cue, new DisplayTextOptions
        {
            Source = DisplayContentKind.VideoSubtitle,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = TextScrollDirection.RightToLeft
        });
        StatusMessage = $"已发送当前字幕：{cue.Text}";
    }

    [RelayCommand]
    private void StartPlayback()
    {
        if (document is null)
            ParseSubtitle();

        if (document is null)
            return;

        timelineController.Play(document, new DisplayTextOptions
        {
            Source = DisplayContentKind.VideoSubtitle,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = TextScrollDirection.RightToLeft
        }, TimeSpan.FromSeconds(PositionSeconds));
        StatusMessage = "字幕同步播放已启动";
    }

    [RelayCommand]
    private void StopPlayback()
    {
        timelineController.Stop();
        StatusMessage = "字幕同步播放已停止";
    }
}

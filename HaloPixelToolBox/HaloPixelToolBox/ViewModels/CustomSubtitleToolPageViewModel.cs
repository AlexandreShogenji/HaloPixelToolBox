using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;

namespace HaloPixelToolBox.ViewModels;

public partial class CustomSubtitleToolPageViewModel : ViewModelBase
{
    private readonly HaloPixelDisplayService displayService = new();

    private readonly HaloPixelTextLayout[] layoutMapping =
    [
        HaloPixelTextLayout.Left,
        HaloPixelTextLayout.Center,
        HaloPixelTextLayout.Right,
        HaloPixelTextLayout.Stretch
    ];

    private readonly TextScrollDirection[] scrollMapping =
    [
        TextScrollDirection.None,
        TextScrollDirection.LeftToRight,
        TextScrollDirection.RightToLeft
    ];

    public List<string> LayoutNames { get; } = ["左对齐", "居中", "右对齐", "拉伸"];
    public List<string> ScrollDirectionNames { get; } = ["不滚动", "从左到右", "从右到左"];
    public List<string> ColorNames { get; } = ["白色", "红色", "绿色", "蓝色"];

    [ObservableProperty]
    private string text = "Halo Pixel";

    [ObservableProperty]
    private int selectedLayoutIndex = 1;

    [ObservableProperty]
    private int selectedScrollDirectionIndex = 2;

    [ObservableProperty]
    private int speed = 5;

    [ObservableProperty]
    private bool blink;

    [ObservableProperty]
    private bool multiLine;

    [ObservableProperty]
    private bool enableTimedSend;

    [ObservableProperty]
    private int delaySeconds = 5;

    [ObservableProperty]
    private int selectedColorIndex;

    [ObservableProperty]
    private string statusMessage = "等待发送";

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            StatusMessage = "请输入字幕内容";
            return;
        }

        await displayService.SendTextAsync(BuildOptions());
        StatusMessage = EnableTimedSend ? $"字幕已加入定时发送：{DelaySeconds} 秒后" : "字幕已发送";
    }

    private DisplayTextOptions BuildOptions()
    {
        return new DisplayTextOptions
        {
            Text = Text,
            Layout = layoutMapping[Math.Clamp(SelectedLayoutIndex, 0, layoutMapping.Length - 1)],
            ScrollDirection = scrollMapping[Math.Clamp(SelectedScrollDirectionIndex, 0, scrollMapping.Length - 1)],
            Speed = Speed,
            Blink = Blink,
            MultiLine = MultiLine,
            Color = SelectedColorIndex switch
            {
                1 => HaloPixelColor.RedColor,
                2 => HaloPixelColor.GreenColor,
                3 => HaloPixelColor.BlueColor,
                _ => HaloPixelColor.White
            },
            SendAt = EnableTimedSend ? DateTimeOffset.Now.AddSeconds(Math.Max(0, DelaySeconds)) : null
        };
    }
}

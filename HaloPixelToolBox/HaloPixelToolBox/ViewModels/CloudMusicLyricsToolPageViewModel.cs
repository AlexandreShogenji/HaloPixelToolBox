using CommunityToolkit.Mvvm.ComponentModel;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Utilities;
using XFEExtension.NetCore.WinUIHelper.Implements;

namespace HaloPixelToolBox.ViewModels;

public partial class CloudMusicLyricsToolPageViewModel : ServiceBaseViewModelBase<string>
{
    [ObservableProperty]
    private bool deviceReady;
    [ObservableProperty]
    private string testText = "乃木坂工事中";
    [ObservableProperty]
    private string statusMessage = string.Empty;
    [ObservableProperty]
    private int selectedLayoutIndex = 2; // Center

    public List<string> LayoutOptions { get; } = new()
    {
        "左对齐 (静态)",
        "右对齐 (静态)",
        "居中 (静态)",
        "两端对齐 (静态)",
        "从左向右滚动 (动态)",
        "从右向左滚动 (动态)",
    };

    private static readonly HaloPixelTextLayout[] LayoutMapping =
    {
        HaloPixelTextLayout.Left,
        HaloPixelTextLayout.Right,
        HaloPixelTextLayout.Center,
        HaloPixelTextLayout.Stretch,
        HaloPixelTextLayout.ScrollLeftToRight,
        HaloPixelTextLayout.ScrollRightToLeft,
    };

    public HaloPixelDevice Device { get; set; } = new();

    public CloudMusicLyricsToolPageViewModel()
    {
        Task.Run(async () =>
        {
            while (!DeviceReady)
            {
                var ready = Device.Initialize();
                AutoNavigationParameterService.CurrentPage?.DispatcherQueue.TryEnqueue(() =>
                {
                    DeviceReady = ready;
                    StatusMessage = ready ? "音响已连接" : "搜索音响中...";
                });
                await Task.Delay(500);
            }
        });
    }

    public void SendText()
    {
        if (!DeviceReady) { StatusMessage = "音响未连接"; return; }
        if (string.IsNullOrWhiteSpace(TestText)) { StatusMessage = "请输入文字"; return; }

        try
        {
            var layout = LayoutMapping[SelectedLayoutIndex];
            Device.SetTextLayout(layout);
            Device.ShowText(TestText.Trim());
            StatusMessage = $"已发送: {TestText.Trim()} ({LayoutOptions[SelectedLayoutIndex]})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"失败: {ex.Message}";
        }
    }
}

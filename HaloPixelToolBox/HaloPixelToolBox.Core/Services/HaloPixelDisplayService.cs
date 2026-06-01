using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;

namespace HaloPixelToolBox.Core.Services;

/// <summary>
/// 统一显示服务。新增功能只经过该服务调用现有 HaloPixelDevice，避免破坏底层 HID 协议逻辑。
/// </summary>
public class HaloPixelDisplayService
{
    public HaloPixelDevice Device { get; }

    public HaloPixelDisplayService() : this(new HaloPixelDevice())
    {
    }

    public HaloPixelDisplayService(HaloPixelDevice device)
    {
        Device = device;
    }

    public bool EnsureDeviceReady() => Device.CurrentDevice is not null || Device.Initialize();

    public Task<bool> SendTextAsync(DisplayTextOptions options, CancellationToken cancellationToken = default)
    {
        if (!EnsureDeviceReady())
            return Task.FromResult(false);

        if (options.SendAt is not null)
        {
            var delay = options.SendAt.Value - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
                return SendTextAfterDelayAsync(options, delay, cancellationToken);
        }

        SendTextCore(options);
        return Task.FromResult(true);
    }

    public Task<bool> SendSubtitleCueAsync(SubtitleCue cue, DisplayTextOptions options, CancellationToken cancellationToken = default)
    {
        options.Text = cue.Text;
        return SendTextAsync(options, cancellationToken);
    }

    public void ShowBuiltInUi(HaloPixelUIModel uiModel)
    {
        if (EnsureDeviceReady())
            Device.SetUIModel(uiModel);
    }

    private async Task<bool> SendTextAfterDelayAsync(DisplayTextOptions options, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return false;

        SendTextCore(options);
        return true;
    }

    private void SendTextCore(DisplayTextOptions options)
    {
        var layout = options.ScrollDirection switch
        {
            TextScrollDirection.LeftToRight => HaloPixelTextLayout.ScrollLeftToRight,
            TextScrollDirection.RightToLeft => HaloPixelTextLayout.ScrollRightToLeft,
            _ => options.Layout
        };

        // Speed、Blink、Color 等字段当前官方文本协议未公开，先保留在模型中，后续扩展协议时在这里适配。
        Device.SetTextLayout(layout);
        Device.ShowText(NormalizeText(options));
    }

    private static string NormalizeText(DisplayTextOptions options)
    {
        var text = options.Text.Trim();
        if (!options.MultiLine)
            return text.ReplaceLineEndings(" ");

        return text.ReplaceLineEndings(" / ");
    }
}

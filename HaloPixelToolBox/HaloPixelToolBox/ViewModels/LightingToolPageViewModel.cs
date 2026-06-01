using CommunityToolkit.Mvvm.ComponentModel;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.Lighting;
using Windows.UI;

namespace HaloPixelToolBox.ViewModels;

public partial class LightingToolPageViewModel : ViewModelBase
{
    private readonly HaloPixelLightingService lightingService = new();
    private CancellationTokenSource? ambientSendThrottle;
    private CancellationTokenSource? pixelSendThrottle;

    public List<string> EffectNames { get; } =
    [
        "氛围呼吸",
        "幻彩潮汐",
        "纯色静光",
        "炫彩涟漪",
        "流光逐影",
        "动态光影"
    ];

    public List<string> BrightnessNames { get; } = ["低", "中", "高"];

    [ObservableProperty]
    private bool ambientEnabled = true;

    [ObservableProperty]
    private int ambientEffectIndex = 2;

    [ObservableProperty]
    private int ambientBrightnessIndex = 2;

    [ObservableProperty]
    private double ambientSpeed = 10;

    [ObservableProperty]
    private int ambientRed = 45;

    [ObservableProperty]
    private int ambientGreen;

    [ObservableProperty]
    private int ambientBlue = 179;

    [ObservableProperty]
    private int pixelRed;

    [ObservableProperty]
    private int pixelGreen = 85;

    [ObservableProperty]
    private int pixelBlue = 170;

    [ObservableProperty]
    private bool syncAmbientWithPixel;

    [ObservableProperty]
    private string statusMessage = "调节灯光参数会自动发送到设备";

    public string AmbientHex => ToHex(AmbientRed, AmbientGreen, AmbientBlue);

    public string PixelHex => ToHex(PixelRed, PixelGreen, PixelBlue);

    public Color AmbientPickerColor => Color.FromArgb(255, ClampByte(AmbientRed), ClampByte(AmbientGreen), ClampByte(AmbientBlue));

    public Color PixelPickerColor => Color.FromArgb(255, ClampByte(PixelRed), ClampByte(PixelGreen), ClampByte(PixelBlue));

    public void SetAmbientColor(Color color)
    {
        AmbientRed = color.R;
        AmbientGreen = color.G;
        AmbientBlue = color.B;
    }

    public void SetPixelColor(Color color)
    {
        PixelRed = color.R;
        PixelGreen = color.G;
        PixelBlue = color.B;
    }

    partial void OnAmbientEnabledChanged(bool value) => _ = SendAmbientLightNowAsync();

    partial void OnAmbientEffectIndexChanged(int value) => _ = SendAmbientLightNowAsync();

    partial void OnAmbientBrightnessIndexChanged(int value) => _ = SendAmbientLightNowAsync();

    partial void OnAmbientSpeedChanged(double value) => QueueAmbientLightSend();

    partial void OnAmbientRedChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnAmbientGreenChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnAmbientBlueChanged(int value) => OnAmbientColorComponentChanged();

    partial void OnPixelRedChanged(int value) => OnPixelColorComponentChanged();

    partial void OnPixelGreenChanged(int value) => OnPixelColorComponentChanged();

    partial void OnPixelBlueChanged(int value) => OnPixelColorComponentChanged();

    private void OnAmbientColorComponentChanged()
    {
        OnPropertyChanged(nameof(AmbientHex));
        OnPropertyChanged(nameof(AmbientPickerColor));
        QueueAmbientLightSend();
    }

    private void OnPixelColorComponentChanged()
    {
        OnPropertyChanged(nameof(PixelHex));
        OnPropertyChanged(nameof(PixelPickerColor));
        QueuePixelColorSend();
    }

    private void QueueAmbientLightSend()
    {
        ambientSendThrottle?.Cancel();
        var current = new CancellationTokenSource();
        ambientSendThrottle = current;
        _ = SendAmbientLightAfterDelayAsync(current.Token);
    }

    private void QueuePixelColorSend()
    {
        pixelSendThrottle?.Cancel();
        var current = new CancellationTokenSource();
        pixelSendThrottle = current;
        _ = SendPixelColorAfterDelayAsync(current.Token);
    }

    private async Task SendAmbientLightAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(160, cancellationToken);
            await SendAmbientLightNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendPixelColorAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(160, cancellationToken);
            await SendPixelColorNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAmbientLightNowAsync(CancellationToken cancellationToken = default)
    {
        var result = await lightingService.SetAmbientLightAsync(BuildAmbientOptions(), cancellationToken);
        StatusMessage = result
            ? (AmbientEnabled ? "氛围灯设置已生效" : "氛围灯已关闭")
            : "未检测到花再 Halo PixelBar";
    }

    private async Task SendPixelColorNowAsync(CancellationToken cancellationToken = default)
    {
        var pixelColor = BuildPixelColor();
        var result = await lightingService.SetPixelScreenColorAsync(pixelColor, cancellationToken);
        if (!result)
        {
            StatusMessage = "未检测到花再 Halo PixelBar";
            return;
        }

        if (SyncAmbientWithPixel)
        {
            AmbientRed = pixelColor.Red;
            AmbientGreen = pixelColor.Green;
            AmbientBlue = pixelColor.Blue;
            await lightingService.SetAmbientLightAsync(BuildAmbientOptions(), cancellationToken);
            StatusMessage = "像素屏颜色已生效，并同步氛围灯颜色";
            return;
        }

        StatusMessage = "像素屏颜色已生效";
    }

    private AmbientLightOptions BuildAmbientOptions()
    {
        var effect = (AmbientLightEffect)(Math.Clamp(AmbientEffectIndex, 0, EffectNames.Count - 1) + 1);
        var brightness = (AmbientLightBrightness)(Math.Clamp(AmbientBrightnessIndex, 0, BrightnessNames.Count - 1) + 1);
        return new AmbientLightOptions
        {
            IsEnabled = AmbientEnabled,
            Effect = effect,
            Brightness = brightness,
            Speed = (byte)Math.Clamp((int)Math.Round(AmbientSpeed), 1, 10),
            Color = BuildColor(AmbientRed, AmbientGreen, AmbientBlue)
        };
    }

    private HaloPixelColor BuildPixelColor() => BuildColor(PixelRed, PixelGreen, PixelBlue);

    private static HaloPixelColor BuildColor(int red, int green, int blue)
        => new(ClampByte(red), ClampByte(green), ClampByte(blue));

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static string ToHex(int red, int green, int blue)
        => $"#{Math.Clamp(red, 0, 255):X2}{Math.Clamp(green, 0, 255):X2}{Math.Clamp(blue, 0, 255):X2}";
}

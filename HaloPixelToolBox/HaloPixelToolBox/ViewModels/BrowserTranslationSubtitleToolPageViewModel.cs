using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Translation;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.ViewModels;

public partial class BrowserTranslationSubtitleToolPageViewModel : ViewModelBase
{
    private readonly IBrowserSubtitleCapture subtitleCapture = new PlaceholderBrowserSubtitleCapture();
    private readonly ITranslationService translationService = new PlaceholderTranslationService();
    private readonly HaloPixelDisplayService displayService = new();
    private CancellationTokenSource? cancellationTokenSource;

    [ObservableProperty]
    private string browserProcessName = DisplayFeatureProfile.BrowserProcessName;

    [ObservableProperty]
    private string translationApiEndpoint = DisplayFeatureProfile.TranslationApiEndpoint;

    [ObservableProperty]
    private string translationApiKey = DisplayFeatureProfile.TranslationApiKey;

    [ObservableProperty]
    private string sourceLanguage = "auto";

    [ObservableProperty]
    private string targetLanguage = "zh-CN";

    [ObservableProperty]
    private string capturedText = "尚未捕获字幕";

    [ObservableProperty]
    private string statusMessage = "捕获、翻译、发送链路已搭建，当前使用占位数据源";

    partial void OnBrowserProcessNameChanged(string value) => DisplayFeatureProfile.BrowserProcessName = value;
    partial void OnTranslationApiEndpointChanged(string value) => DisplayFeatureProfile.TranslationApiEndpoint = value;
    partial void OnTranslationApiKeyChanged(string value) => DisplayFeatureProfile.TranslationApiKey = value;

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        StopCapture();
        cancellationTokenSource = new CancellationTokenSource();
        StatusMessage = "实时字幕捕获已启动";

        var configuration = BuildConfiguration();
        try
        {
            await foreach (var cue in subtitleCapture.CaptureAsync(configuration, cancellationTokenSource.Token))
            {
                var translated = await translationService.TranslateAsync(cue.Text, SourceLanguage, TargetLanguage, cancellationTokenSource.Token);
                CapturedText = translated;
                await displayService.SendTextAsync(new DisplayTextOptions
                {
                    Text = translated,
                    Source = DisplayContentKind.BrowserTranslation,
                    Layout = HaloPixelTextLayout.Center,
                    ScrollDirection = TextScrollDirection.RightToLeft
                }, cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "实时字幕捕获已停止";
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
        StatusMessage = "实时字幕捕获已停止";
    }

    [RelayCommand]
    private async Task SendSampleAsync()
    {
        var translated = await translationService.TranslateAsync("Browser subtitle placeholder", SourceLanguage, TargetLanguage);
        CapturedText = translated;
        await displayService.SendTextAsync(new DisplayTextOptions
        {
            Text = translated,
            Source = DisplayContentKind.BrowserTranslation,
            Layout = HaloPixelTextLayout.Center,
            ScrollDirection = TextScrollDirection.RightToLeft
        });
        StatusMessage = "示例翻译字幕已发送";
    }

    private BrowserTranslationConfiguration BuildConfiguration()
    {
        return new BrowserTranslationConfiguration
        {
            BrowserProcessName = BrowserProcessName,
            TranslationApiEndpoint = TranslationApiEndpoint,
            TranslationApiKey = TranslationApiKey,
            SourceLanguage = SourceLanguage,
            TargetLanguage = TargetLanguage
        };
    }
}

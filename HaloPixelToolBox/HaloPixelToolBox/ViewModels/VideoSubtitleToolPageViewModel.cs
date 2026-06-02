using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Services.Subtitles;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using Windows.Storage.Pickers;

namespace HaloPixelToolBox.ViewModels;

public partial class VideoSubtitleToolPageViewModel : ViewModelBase
{
    private readonly PotPlayerSubtitleSyncService potPlayerSubtitleSyncService = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    [ObservableProperty]
    private string potPlayerSubtitleOutputPath = DisplayFeatureProfile.PotPlayerSubtitleOutputPath;

    [ObservableProperty]
    private bool isPotPlayerSyncEnabled = DisplayFeatureProfile.PotPlayerSubtitleSyncEnabled;

    [ObservableProperty]
    private string potPlayerSyncStatus = "PotPlayer 字幕同步未启用";

    [ObservableProperty]
    private string previewText = "等待 PotPlayer 字幕同步";

    [ObservableProperty]
    private string statusMessage = "请选择字幕输出文件并启用同步";

    public VideoSubtitleToolPageViewModel()
    {
        potPlayerSubtitleSyncService.StatusChanged += (_, message) => UpdatePotPlayerStatus(message);
        potPlayerSubtitleSyncService.SubtitleSent += (_, text) => UpdatePotPlayerStatus($"已同步：{text}");
        potPlayerSubtitleSyncService.SubtitleSourceResolved += (_, path) => UpdateSubtitleSource(path);
        if (IsPotPlayerSyncEnabled)
            StartPotPlayerSync();
    }

    partial void OnPotPlayerSubtitleOutputPathChanged(string value) => DisplayFeatureProfile.PotPlayerSubtitleOutputPath = value;

    partial void OnIsPotPlayerSyncEnabledChanged(bool value)
    {
        DisplayFeatureProfile.PotPlayerSubtitleSyncEnabled = value;
        if (value)
            StartPotPlayerSync();
        else
            potPlayerSubtitleSyncService.Stop();
    }

    [RelayCommand]
    private void RestartPotPlayerSync()
    {
        IsPotPlayerSyncEnabled = true;
        StartPotPlayerSync();
    }

    private void StartPotPlayerSync()
    {
        potPlayerSubtitleSyncService.Start(new PotPlayerSubtitleSyncConfiguration
        {
            SubtitleOutputPath = PotPlayerSubtitleOutputPath,
            DisplayOptions = new DisplayTextOptions
            {
                Source = DisplayContentKind.VideoSubtitle,
                Layout = HaloPixelTextLayout.Center,
                ScrollDirection = TextScrollDirection.RightToLeft
            }
        });
    }

    [RelayCommand]
    private async Task SelectSubtitleOutputFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };

        foreach (var extension in new[] { ".txt", ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".sub" })
            picker.FileTypeFilter.Add(extension);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        PotPlayerSubtitleOutputPath = file.Path;
        StatusMessage = $"已选择字幕文件：{Path.GetFileName(file.Path)}";
        if (IsPotPlayerSyncEnabled)
            StartPotPlayerSync();
    }

    private void UpdatePotPlayerStatus(string message)
    {
        void Apply()
        {
            PotPlayerSyncStatus = message;
            StatusMessage = message;
            if (message.StartsWith("已同步：", StringComparison.Ordinal))
                PreviewText = message["已同步：".Length..];
        }

        if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            dispatcherQueue.TryEnqueue(Apply);
        else
            Apply();
    }

    private void UpdateSubtitleSource(string path)
    {
        void Apply()
        {
            if (!PotPlayerSubtitleOutputPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                PotPlayerSubtitleOutputPath = path;
        }

        if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            dispatcherQueue.TryEnqueue(Apply);
        else
            Apply();
    }
}

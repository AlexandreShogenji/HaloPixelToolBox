using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Clock;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.ViewModels;

public partial class ClockToolPageViewModel : ViewModelBase
{
    private readonly ClockTemplateResourceLoader templateLoader = new();
    private readonly ClockDisplayController clockController = new(new HaloPixelDisplayService());
    private IReadOnlyList<ClockTemplateDefinition> templates = [];

    [ObservableProperty]
    private List<string> templateNames = [];

    [ObservableProperty]
    private int selectedTemplateIndex;

    [ObservableProperty]
    private string fontFamily = "默认字体";

    [ObservableProperty]
    private int x;

    [ObservableProperty]
    private int y;

    [ObservableProperty]
    private int refreshInterval = DisplayFeatureProfile.ClockRefreshInterval;

    [ObservableProperty]
    private bool showDate;

    [ObservableProperty]
    private string previewText = string.Empty;

    [ObservableProperty]
    private string statusMessage = "等待发送";

    public string TemplateDirectory
    {
        get => DisplayFeatureProfile.ClockTemplateDirectory;
        set => DisplayFeatureProfile.ClockTemplateDirectory = value;
    }

    public ClockToolPageViewModel()
    {
        ReloadTemplates();
        UpdatePreview();
    }

    partial void OnSelectedTemplateIndexChanged(int value) => UpdatePreview();
    partial void OnShowDateChanged(bool value) => UpdatePreview();
    partial void OnRefreshIntervalChanged(int value) => DisplayFeatureProfile.ClockRefreshInterval = Math.Max(200, value);

    [RelayCommand]
    private void ReloadTemplates()
    {
        templates = templateLoader.LoadTemplates(DisplayFeatureProfile.ClockTemplateDirectory);
        TemplateNames = templates.Select(template => template.Name).ToList();
        SelectedTemplateIndex = Math.Max(0, templates.ToList().FindIndex(template => template.Id == DisplayFeatureProfile.SelectedClockTemplateId));
        UpdatePreview();
    }

    [RelayCommand]
    private async Task SendClockAsync()
    {
        await clockController.SendOnceAsync(BuildConfiguration());
        StatusMessage = "时钟已发送到字幕屏";
    }

    [RelayCommand]
    private void StartClock()
    {
        clockController.Start(BuildConfiguration());
        StatusMessage = "时钟实时刷新已启动";
    }

    [RelayCommand]
    private void StopClock()
    {
        clockController.Stop();
        StatusMessage = "时钟实时刷新已停止";
    }

    private ClockConfiguration BuildConfiguration()
    {
        var template = templates.Count > 0 ? templates[Math.Clamp(SelectedTemplateIndex, 0, templates.Count - 1)] : new ClockTemplateDefinition();
        DisplayFeatureProfile.SelectedClockTemplateId = template.Id;

        return new ClockConfiguration
        {
            Template = template,
            FontFamily = FontFamily,
            X = X,
            Y = Y,
            RefreshIntervalMilliseconds = Math.Max(200, RefreshInterval),
            ShowDate = ShowDate
        };
    }

    private void UpdatePreview()
    {
        if (templates.Count == 0)
            return;

        PreviewText = ClockRenderer.Render(DateTimeOffset.Now, BuildConfiguration());
    }
}

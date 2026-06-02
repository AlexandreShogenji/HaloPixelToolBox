using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Scenes;
using Microsoft.UI.Xaml;

namespace HaloPixelToolBox.ViewModels;

public partial class PersonalSceneToolPageViewModel : ViewModelBase
{
    private readonly PersonalSceneResourceLoader resourceLoader = new();
    private readonly PersonalSceneDisplayController displayController = new(new HaloPixelDisplayService());
    private readonly CustomSceneResourceGenerationService customSceneGenerationService = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private IReadOnlyList<PersonalSceneDefinition> scenes = [];
    private long uploadTotalBytes;
    private DateTimeOffset lastUploadProgressUiUpdate;

    [ObservableProperty]
    private List<PersonalSceneCategoryGroup> categories = [];

    [ObservableProperty]
    private int selectedCategoryIndex;

    [ObservableProperty]
    private List<PersonalSceneDefinition> selectedScenes = [];

    [ObservableProperty]
    private string statusMessage = "等待加载场景资源";

    [ObservableProperty]
    private string sceneCountText = "已发现 0 个场景";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadOverlayOpacity))]
    private bool isUploading;

    [ObservableProperty]
    private string uploadMessage = "正在上传音箱像素屏";

    [ObservableProperty]
    private double uploadProgressValue;

    [ObservableProperty]
    private string uploadProgressText = "0%";

    [ObservableProperty]
    private List<CustomSceneFrameSlot> customFrameSlots = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeneratedCustomSceneVisibility))]
    [NotifyPropertyChangedFor(nameof(GeneratedCustomScenePreviewSource))]
    private PersonalSceneDefinition? generatedCustomScene;

    [ObservableProperty]
    private string customSceneGenerationStatus = "拖入或选择 1 到 5 张 256×32 PNG 图像";

    [ObservableProperty]
    private bool isGeneratingCustomScene;

    public string OfficialInstallPath => PersonalSceneResourceLoader.DefaultOfficialInstallPath;

    public double UploadOverlayOpacity => IsUploading ? 1 : 0;

    public Visibility CustomWorkbenchVisibility => IsCustomCategorySelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GeneratedCustomSceneVisibility => GeneratedCustomScene is null ? Visibility.Collapsed : Visibility.Visible;

    public string GeneratedCustomScenePreviewSource => GeneratedCustomScene?.PreviewSource ?? "ms-appx:///Assets/icon.png";

    private bool IsCustomCategorySelected => Categories.Count > 0
        && SelectedCategoryIndex >= 0
        && SelectedCategoryIndex < Categories.Count
        && Categories[SelectedCategoryIndex].Category == PersonalSceneCategory.Custom;

    public string OfficialCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "EDIFIER TempoHub",
        "cache");

    public PersonalSceneToolPageViewModel()
    {
        CustomFrameSlots = Enumerable.Range(0, 5)
            .Select(index => new CustomSceneFrameSlot(index))
            .ToList();
        ReloadScenes();
    }

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        UpdateSelectedScenes();
        OnPropertyChanged(nameof(CustomWorkbenchVisibility));
    }

    [RelayCommand]
    private void ReloadScenes()
    {
        scenes = resourceLoader.LoadScenes();
        Categories = resourceLoader.LoadCategoryPlans()
            .Select(plan => new PersonalSceneCategoryGroup(plan.Category, plan.DisplayName, plan.Count))
            .ToList();

        var previewCount = scenes.Count(scene => !string.IsNullOrWhiteSpace(scene.PreviewPath));
        GeneratedCustomScene = customSceneGenerationService.LoadGeneratedScene();
        SceneCountText = $"已加载 {scenes.Count} 个场景，{previewCount} 张预览图";
        SelectedCategoryIndex = Math.Clamp(SelectedCategoryIndex, 0, Math.Max(0, Categories.Count - 1));
        UpdateSelectedScenes();
        StatusMessage = previewCount > 0
            ? "已从 TempoHub 缓存解包官方预览图，并按官方分类重新编排"
            : "未找到 TempoHub 预览缓存，已退回到内置参数列表";
    }

    public void SelectCategory(PersonalSceneCategoryGroup category)
    {
        var index = Categories.IndexOf(category);
        if (index >= 0)
            SelectedCategoryIndex = index;
    }

    public async Task SendSceneAsync(PersonalSceneDefinition scene)
    {
        if (IsUploading)
            return;

        try
        {
            if (scene.RequiresResourceUpload)
            {
                uploadTotalBytes = 0;
                lastUploadProgressUiUpdate = DateTimeOffset.MinValue;
                UploadProgressValue = 0;
                UploadProgressText = "准备上传";
                IsUploading = true;
                UploadMessage = $"正在上传：{scene.Name}";
                StatusMessage = $"正在上传：{scene.Name}";
                await Task.Yield();
            }

            var progress = scene.RequiresResourceUpload ? new UploadProgressReporter(UpdateUploadProgress) : null;
            var result = await displayController.SendAsync(scene, progress);
            if (!result)
            {
                StatusMessage = scene.RequiresResourceUpload
                    ? $"上传失败或超时：{scene.Name}，请确认官方软件未同时占用设备后重试"
                    : "该场景缺少可发送参数";
                return;
            }

            foreach (var item in scenes)
                item.IsInUse = item.Id == scene.Id;

            if (scene.RequiresResourceUpload)
            {
                UploadMessage = $"上传完成：{scene.Name}";
                if (uploadTotalBytes > 0)
                    UpdateUploadProgress(new PixelSceneUploadProgress(uploadTotalBytes, uploadTotalBytes, "Completed"));
                else
                    ApplyUploadProgress(100, "100.0%");
                await Task.Delay(350);
            }

            StatusMessage = scene.RequiresResourceUpload
                ? $"已上传并切换场景：{scene.Name}（{scene.CategoryIndex}-{scene.SceneIndex}）"
                : $"已发送：{scene.Name}（{scene.CategoryIndex}-{scene.SceneIndex}）";
        }
        finally
        {
            IsUploading = false;
        }
    }

    public async Task SetCustomFrameAsync(CustomSceneFrameSlot slot, string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            CustomSceneGenerationStatus = "图片文件不存在";
            return;
        }

        if (!Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            CustomSceneGenerationStatus = "仅支持 PNG 图像";
            return;
        }

        slot.ImagePath = imagePath;
        CustomSceneGenerationStatus = $"已载入第 {slot.Index + 1} 帧：{Path.GetFileName(imagePath)}";
        await Task.CompletedTask;
    }

    public async Task SendGeneratedCustomSceneAsync()
    {
        if (GeneratedCustomScene is not null)
            await SendSceneAsync(GeneratedCustomScene);
    }

    [RelayCommand]
    private async Task GenerateCustomSceneAsync()
    {
        if (IsGeneratingCustomScene)
            return;

        try
        {
            IsGeneratingCustomScene = true;
            CustomSceneGenerationStatus = "正在生成自定义资源";
            var imagePaths = CustomFrameSlots
                .Where(slot => !string.IsNullOrWhiteSpace(slot.ImagePath))
                .Select(slot => slot.ImagePath!)
                .ToList();
            var result = await customSceneGenerationService.GenerateAsync(imagePaths);
            CustomSceneGenerationStatus = result.Message;
            if (result.Success)
                GeneratedCustomScene = customSceneGenerationService.LoadGeneratedScene();
        }
        finally
        {
            IsGeneratingCustomScene = false;
        }
    }

    [RelayCommand]
    private void DeleteGeneratedCustomScene()
    {
        customSceneGenerationService.DeleteGeneratedScene();
        GeneratedCustomScene = null;
        CustomSceneGenerationStatus = "已删除生成资源";
    }

    private void UpdateUploadProgress(PixelSceneUploadProgress progress)
    {
        var percent = Math.Clamp(progress.Percent, 0, 100);
        var now = DateTimeOffset.Now;
        if (progress.Stage == "Uploading" && percent < 100 && now - lastUploadProgressUiUpdate < TimeSpan.FromMilliseconds(30))
            return;

        lastUploadProgressUiUpdate = now;
        var text = progress.TotalBytes > 0
            ? $"{percent:0.0}%（{FormatBytes(progress.SentBytes)} / {FormatBytes(progress.TotalBytes)}）"
            : $"{percent:0.0}%";
        uploadTotalBytes = progress.TotalBytes;

        void Apply() => ApplyUploadProgress(percent, text);

        if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            dispatcherQueue.TryEnqueue(Apply);
        else
            Apply();
    }

    private void ApplyUploadProgress(double percent, string text)
    {
        UploadProgressValue = percent;
        UploadProgressText = text;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        return $"{bytes / 1024d:0.0} KB";
    }

    private sealed class UploadProgressReporter : IProgress<PixelSceneUploadProgress>
    {
        private readonly Action<PixelSceneUploadProgress> report;

        public UploadProgressReporter(Action<PixelSceneUploadProgress> report)
        {
            this.report = report;
        }

        public void Report(PixelSceneUploadProgress value) => report(value);
    }

    private void UpdateSelectedScenes()
    {
        if (Categories.Count == 0)
        {
            SelectedScenes = [];
            return;
        }

        var safeIndex = Math.Clamp(SelectedCategoryIndex, 0, Categories.Count - 1);
        for (var index = 0; index < Categories.Count; index++)
            Categories[index].IsSelected = index == safeIndex;

        var category = Categories[safeIndex].Category;
        SelectedScenes = scenes
            .Where(scene => scene.Category == category)
            .OrderBy(scene => scene.SceneIndex)
            .ToList();
        OnPropertyChanged(nameof(CustomWorkbenchVisibility));
    }
}

public partial class PersonalSceneCategoryGroup : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public PersonalSceneCategoryGroup(PersonalSceneCategory category, string name, int count)
    {
        Category = category;
        Name = name;
        Count = count;
    }

    public PersonalSceneCategory Category { get; }

    public string Name { get; }

    public int Count { get; }

    public string Header => $"{Name} ({Count})";
}

public partial class CustomSceneFrameSlot : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(PreviewSource))]
    [NotifyPropertyChangedFor(nameof(PreviewOpacity))]
    [NotifyPropertyChangedFor(nameof(PlaceholderOpacity))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? imagePath;

    public CustomSceneFrameSlot(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public string Title => $"帧 {Index + 1}";

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public string PreviewSource => HasImage ? new Uri(ImagePath!).AbsoluteUri : "ms-appx:///Assets/icon.png";

    public double PreviewOpacity => HasImage ? 1 : 0.12;

    public double PlaceholderOpacity => HasImage ? 0 : 1;

    public string StatusText => HasImage ? Path.GetFileName(ImagePath) ?? "PNG 图像" : "拖入 256×32 PNG";
}

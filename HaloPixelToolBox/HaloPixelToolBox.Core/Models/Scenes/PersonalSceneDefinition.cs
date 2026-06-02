using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaloPixelToolBox.Core.Models.Scenes;

public sealed partial class PersonalSceneDefinition : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InUseBadgeOpacity))]
    private bool isInUse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonOpacity))]
    [NotifyPropertyChangedFor(nameof(DeleteButtonHitTestVisible))]
    private bool canDelete;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public PersonalSceneCategory Category { get; set; } = PersonalSceneCategory.Unknown;

    public string Source { get; set; } = string.Empty;

    public string? ResourcePath { get; set; }

    public string? ResourceRemoteUrl { get; set; }

    public string? PreviewPath { get; set; }

    public string? PreviewRemoteUrl { get; set; }

    public string PreviewSource => string.IsNullOrWhiteSpace(PreviewPath)
        ? "ms-appx:///Assets/icon.png"
        : new Uri(PreviewPath).AbsoluteUri;

    public HaloPixelUIModel? BuiltInUiModel { get; set; }

    public byte[]? ScreenSettingParameters { get; set; }

    public int CategoryIndex { get; set; }

    public int SceneIndex { get; set; }

    /// <summary>
    /// 资源上传握手使用的分类索引。自定义类实际索引仍为 9，但设备首次进入自定义类时需要借用已验证的游戏类索引预热上传通道。
    /// </summary>
    public int? UploadCategoryIndex { get; set; }

    public DisplayContentKind ContentKind { get; set; } = DisplayContentKind.Scene;

    public bool CanSendDirectly => BuiltInUiModel is not null
        || ScreenSettingParameters is not null
        || !string.IsNullOrWhiteSpace(ResourcePath)
        || !string.IsNullOrWhiteSpace(ResourceRemoteUrl);

    public bool RequiresResourceUpload => !string.IsNullOrWhiteSpace(ResourcePath)
        || !string.IsNullOrWhiteSpace(ResourceRemoteUrl);

    public double InUseBadgeOpacity => IsInUse ? 1 : 0;

    public double DeleteButtonOpacity => CanDelete ? 1 : 0;

    public bool DeleteButtonHitTestVisible => CanDelete;
}

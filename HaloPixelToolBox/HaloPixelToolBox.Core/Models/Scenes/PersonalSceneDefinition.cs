using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaloPixelToolBox.Core.Models.Scenes;

public sealed partial class PersonalSceneDefinition : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InUseBadgeOpacity))]
    private bool isInUse;

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

    public DisplayContentKind ContentKind { get; set; } = DisplayContentKind.Scene;

    public bool CanSendDirectly => BuiltInUiModel is not null
        || ScreenSettingParameters is not null
        || !string.IsNullOrWhiteSpace(ResourcePath)
        || !string.IsNullOrWhiteSpace(ResourceRemoteUrl);

    public bool RequiresResourceUpload => !string.IsNullOrWhiteSpace(ResourcePath)
        || !string.IsNullOrWhiteSpace(ResourceRemoteUrl);

    public double InUseBadgeOpacity => IsInUse ? 1 : 0;
}

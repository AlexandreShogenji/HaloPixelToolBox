namespace HaloPixelToolBox.Core.Models.DeviceControl;

public sealed record HaloPixelSceneInfo(
    string Name,
    string Category,
    int CategoryIndex,
    int Position,
    int SceneIndex,
    bool RequiresResourceUpload);

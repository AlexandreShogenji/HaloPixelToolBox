namespace HaloPixelToolBox.Core.Models.Scenes;

public sealed record CustomSceneGenerationResult(
    bool Success,
    string Message,
    string? ResourcePath = null,
    string? PreviewPath = null);

namespace HaloPixelToolBox.Core.Models.Display;

public sealed class DisplayContentChangedEventArgs : EventArgs
{
    public DisplayContentChangedEventArgs(
        DisplayContentKind contentKind,
        string? text,
        string? scenePreviewSource = null,
        string? sceneName = null)
    {
        ContentKind = contentKind;
        Text = text;
        ScenePreviewSource = scenePreviewSource;
        SceneName = sceneName;
    }

    public DisplayContentKind ContentKind { get; }

    public string? Text { get; }

    /// <summary>
    /// 场景内容对应的预览图 URI。文本内容保持为空，便于控制台在图片与字幕之间切换。
    /// </summary>
    public string? ScenePreviewSource { get; }

    public string? SceneName { get; }
}

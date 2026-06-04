namespace HaloPixelToolBox.Core.Utilities;

public static class HaloPixelCachePaths
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "HaloPixelToolBox");

    public static string BrowserSubtitleAsrRoot { get; } = Path.Combine(Root, "BrowserSubtitleAsr");

    public static string BrowserSubtitleAsrWorkRoot { get; } = Path.Combine(BrowserSubtitleAsrRoot, "Work");

    public static string BrowserSubtitleAsrAudioRoot { get; } = Path.Combine(BrowserSubtitleAsrRoot, "Audio");

    public static string BrowserSubtitleAsrSubtitleRoot { get; } = Path.Combine(BrowserSubtitleAsrRoot, "Subtitles");

    public static string BrowserSubtitleAsrModelRoot { get; } = Path.Combine(BrowserSubtitleAsrRoot, "Models");

    public static string HuggingFaceRoot { get; } = Path.Combine(BrowserSubtitleAsrModelRoot, "HuggingFace");

    public static string HuggingFaceHubCache { get; } = Path.Combine(HuggingFaceRoot, "hub");

    public static string ModelScopeRoot { get; } = Path.Combine(BrowserSubtitleAsrModelRoot, "ModelScope");

    public static string TorchRoot { get; } = Path.Combine(BrowserSubtitleAsrModelRoot, "Torch");

    public static string CreateOperationDirectory(string prefix)
    {
        Directory.CreateDirectory(BrowserSubtitleAsrWorkRoot);
        var directory = Path.Combine(BrowserSubtitleAsrWorkRoot, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static IReadOnlyDictionary<string, string> CreatePythonModelCacheEnvironment()
    {
        Directory.CreateDirectory(HuggingFaceRoot);
        Directory.CreateDirectory(HuggingFaceHubCache);
        Directory.CreateDirectory(ModelScopeRoot);
        Directory.CreateDirectory(TorchRoot);

        return new Dictionary<string, string>
        {
            ["HF_HOME"] = HuggingFaceRoot,
            ["HUGGINGFACE_HUB_CACHE"] = HuggingFaceHubCache,
            ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
            ["MODELSCOPE_CACHE"] = ModelScopeRoot,
            ["MODELSCOPE_CACHE_DIR"] = ModelScopeRoot,
            ["TORCH_HOME"] = TorchRoot
        };
    }
}

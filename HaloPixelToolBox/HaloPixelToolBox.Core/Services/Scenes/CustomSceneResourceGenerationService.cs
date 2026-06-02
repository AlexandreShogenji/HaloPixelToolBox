using System.Buffers.Binary;
using System.Diagnostics;
using HaloPixelToolBox.Core.Models.Scenes;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace HaloPixelToolBox.Core.Services.Scenes;

public sealed class CustomSceneResourceGenerationService
{
    private const int RequiredWidth = 256;
    private const int RequiredHeight = 32;
    private const int MaxFrameCount = 5;
    private const int BytesPerHalfPage = 128;
    private const int FramesPerImage = 8;
    private const int CustomCategoryIndex = 9;
    private const int CustomSceneIndex = 0;
    private const int CustomUploadCategoryIndex = 1;
    private const byte FrameParam = 0x64;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public string GeneratedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaloPixelToolBox",
        "CustomScenes");

    public string GeneratedResourcePath => Path.Combine(GeneratedDirectory, "custom_0.bin");

    public string GeneratedPreviewPath => Path.Combine(GeneratedDirectory, "custom_0.png");

    public string PinnedDirectory => Path.Combine(GeneratedDirectory, "Pinned");

    public string DefaultScriptPath => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "PersonalScenes",
        "Generation",
        "parse_bin.py");

    public PersonalSceneDefinition? LoadGeneratedScene()
    {
        if (!File.Exists(GeneratedResourcePath) || !File.Exists(GeneratedPreviewPath))
            return null;

        return CreateGeneratedSceneDefinition(GeneratedResourcePath, GeneratedPreviewPath);
    }

    public IReadOnlyList<PersonalSceneDefinition> LoadPinnedScenes()
    {
        if (!Directory.Exists(PinnedDirectory))
            return [];

        return Directory.EnumerateFiles(PinnedDirectory, "custom_*.bin")
            .Select(path => new
            {
                ResourcePath = path,
                SceneIndex = TryGetPinnedSceneIndex(path)
            })
            .Where(item => item.SceneIndex > CustomSceneIndex)
            .OrderBy(item => item.SceneIndex)
            .Select(item => CreatePinnedSceneDefinition(
                item.SceneIndex,
                item.ResourcePath,
                Path.ChangeExtension(item.ResourcePath, ".png")))
            .Where(scene => File.Exists(scene.PreviewPath))
            .ToList();
    }

    public async Task<CustomSceneGenerationResult> GenerateAsync(
        IReadOnlyList<string> imagePaths,
        string? scriptPath = null,
        CancellationToken cancellationToken = default)
    {
        var frames = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFrameCount)
            .ToList();

        if (frames.Count == 0)
            return new(false, "请先拖入或选择 1 到 5 张 256×32 PNG 图像");

        if (frames.Count > MaxFrameCount)
            return new(false, "最多只能生成 5 帧动画资源");

        foreach (var frame in frames)
        {
            var validation = ValidatePngFrame(frame);
            if (!validation.Success)
                return validation;
        }

        var generatorScript = scriptPath ?? DefaultScriptPath;
        if (!File.Exists(generatorScript))
            return new(false, $"未找到生成脚本：{generatorScript}");

        Directory.CreateDirectory(GeneratedDirectory);
        File.Copy(frames[0], GeneratedPreviewPath, overwrite: true);
        if (File.Exists(GeneratedResourcePath))
            File.Delete(GeneratedResourcePath);

        var result = await RunGeneratorAsync(generatorScript, GeneratedResourcePath, frames, cancellationToken);
        if (!result.Success)
        {
            var fallbackResult = await GenerateWithInternalEncoderAsync(GeneratedResourcePath, frames, cancellationToken);
            if (!fallbackResult.Success)
                return new(false, $"{result.Message}{Environment.NewLine}{fallbackResult.Message}");
        }

        return File.Exists(GeneratedResourcePath)
            ? new(true, frames.Count == 1 ? "静态自定义资源已就绪" : $"{frames.Count} 帧自定义动画资源已就绪", GeneratedResourcePath, GeneratedPreviewPath)
            : new(false, "脚本运行完成，但未生成 bin 文件");
    }

    public void DeleteGeneratedScene()
    {
        DeleteIfExists(GeneratedResourcePath);
        DeleteIfExists(GeneratedPreviewPath);
    }

    public PersonalSceneDefinition? PinGeneratedScene()
    {
        if (!File.Exists(GeneratedResourcePath) || !File.Exists(GeneratedPreviewPath))
            return null;

        Directory.CreateDirectory(PinnedDirectory);
        var nextIndex = LoadPinnedScenes()
            .Select(scene => scene.SceneIndex)
            .DefaultIfEmpty(CustomSceneIndex)
            .Max() + 1;
        var resourcePath = Path.Combine(PinnedDirectory, $"custom_{nextIndex}.bin");
        var previewPath = Path.Combine(PinnedDirectory, $"custom_{nextIndex}.png");

        File.Copy(GeneratedResourcePath, resourcePath, overwrite: true);
        File.Copy(GeneratedPreviewPath, previewPath, overwrite: true);
        DeleteGeneratedScene();
        ReindexPinnedScenes();

        return LoadPinnedScenes().LastOrDefault();
    }

    public bool DeletePinnedScene(PersonalSceneDefinition scene)
    {
        if (!scene.CanDelete || string.IsNullOrWhiteSpace(scene.ResourcePath))
            return false;

        var resourcePath = Path.GetFullPath(scene.ResourcePath);
        var pinnedRoot = Path.GetFullPath(PinnedDirectory);
        if (!resourcePath.StartsWith(pinnedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        DeleteIfExists(resourcePath);
        DeleteIfExists(Path.ChangeExtension(resourcePath, ".png"));
        ReindexPinnedScenes();
        return true;
    }

    private static PersonalSceneDefinition CreateGeneratedSceneDefinition(string resourcePath, string previewPath)
        => new()
        {
            Id = "custom-generated-scene-9-0",
            Name = "自定义生成资源",
            Category = PersonalSceneCategory.Custom,
            Source = "PNG 生成资源",
            CategoryIndex = CustomCategoryIndex,
            UploadCategoryIndex = CustomUploadCategoryIndex,
            SceneIndex = CustomSceneIndex,
            ResourcePath = resourcePath,
            PreviewPath = previewPath
        };

    private static PersonalSceneDefinition CreatePinnedSceneDefinition(int sceneIndex, string resourcePath, string previewPath)
        => new()
        {
            Id = $"custom-pinned-scene-9-{sceneIndex}",
            Name = $"自定义资源 {sceneIndex:D2}",
            Category = PersonalSceneCategory.Custom,
            Source = "用户固定资源",
            CategoryIndex = CustomCategoryIndex,
            UploadCategoryIndex = CustomUploadCategoryIndex,
            SceneIndex = sceneIndex,
            ResourcePath = resourcePath,
            PreviewPath = previewPath,
            CanDelete = true
        };

    private void ReindexPinnedScenes()
    {
        if (!Directory.Exists(PinnedDirectory))
            return;

        var entries = Directory.EnumerateFiles(PinnedDirectory, "custom_*.bin")
            .Select(path => new
            {
                ResourcePath = path,
                PreviewPath = Path.ChangeExtension(path, ".png"),
                SceneIndex = TryGetPinnedSceneIndex(path)
            })
            .Where(item => item.SceneIndex > CustomSceneIndex && File.Exists(item.PreviewPath))
            .OrderBy(item => item.SceneIndex)
            .ToList();

        var tempDirectory = Path.Combine(PinnedDirectory, ".reindex");
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
        Directory.CreateDirectory(tempDirectory);

        for (var index = 0; index < entries.Count; index++)
        {
            File.Copy(entries[index].ResourcePath, Path.Combine(tempDirectory, $"{index + 1}.bin"), overwrite: true);
            File.Copy(entries[index].PreviewPath, Path.Combine(tempDirectory, $"{index + 1}.png"), overwrite: true);
        }

        foreach (var file in Directory.EnumerateFiles(PinnedDirectory, "custom_*.*"))
            File.Delete(file);

        for (var index = 0; index < entries.Count; index++)
        {
            var sceneIndex = index + 1;
            File.Copy(Path.Combine(tempDirectory, $"{sceneIndex}.bin"), Path.Combine(PinnedDirectory, $"custom_{sceneIndex}.bin"), overwrite: true);
            File.Copy(Path.Combine(tempDirectory, $"{sceneIndex}.png"), Path.Combine(PinnedDirectory, $"custom_{sceneIndex}.png"), overwrite: true);
        }

        Directory.Delete(tempDirectory, recursive: true);
    }

    private static int TryGetPinnedSceneIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name["custom_".Length..], out var index))
        {
            return index;
        }

        return -1;
    }

    private static CustomSceneGenerationResult ValidatePngFrame(string path)
    {
        if (!File.Exists(path))
            return new(false, $"PNG 文件不存在：{path}");

        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return new(false, $"仅支持 PNG 图像：{Path.GetFileName(path)}");

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length || !header[..8].SequenceEqual(PngSignature))
                return new(false, $"不是有效 PNG 图像：{Path.GetFileName(path)}");

            var width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
            return width == RequiredWidth && height == RequiredHeight
                ? new(true, "PNG 图像可用")
                : new(false, $"{Path.GetFileName(path)} 尺寸为 {width}×{height}，请使用 256×32 PNG");
        }
        catch (Exception ex)
        {
            return new(false, $"读取 PNG 失败：{ex.Message}");
        }
    }

    private static async Task<CustomSceneGenerationResult> RunGeneratorAsync(
        string scriptPath,
        string outputPath,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in CreatePythonCandidates())
        {
            var result = await TryRunGeneratorAsync(candidate.Executable, candidate.PrefixArguments, scriptPath, outputPath, imagePaths, cancellationToken);
            if (result.Success || !result.Message.StartsWith("无法启动", StringComparison.Ordinal))
                return result;
        }

        return new(false, "无法启动 Python，请确认已安装 Python，并安装 Pillow / numpy");
    }

    private static async Task<CustomSceneGenerationResult> GenerateWithInternalEncoderAsync(
        string outputPath,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        try
        {
            var allFrames = new List<byte[]>(imagePaths.Count * FramesPerImage);
            foreach (var imagePath in imagePaths)
                allFrames.AddRange(await EncodePngFrameAsync(imagePath, cancellationToken));

            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            for (var index = 0; index < allFrames.Count; index++)
            {
                stream.Write(PngFrameMagic);
                stream.Write(BitConverter.GetBytes((ushort)index));
                stream.WriteByte(FrameParam);
                stream.Write(allFrames[index]);
            }

            return new(true, "Python 依赖不可用，已使用内置编码器生成资源");
        }
        catch (Exception ex)
        {
            return new(false, $"内置编码器生成失败：{ex.Message}");
        }
    }

    private static readonly byte[] PngFrameMagic = [0x01, 0x00];

    private static async Task<IReadOnlyList<byte[]>> EncodePngFrameAsync(string imagePath, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth != RequiredWidth || decoder.PixelHeight != RequiredHeight)
            throw new InvalidOperationException($"{Path.GetFileName(imagePath)} 尺寸不是 256×32");

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = pixelData.DetachPixelData();
        var frames = Enumerable.Range(0, FramesPerImage)
            .Select(_ => new byte[BytesPerHalfPage])
            .ToArray();

        for (var page = 0; page < 4; page++)
        {
            var left = frames[page * 2];
            var right = frames[page * 2 + 1];
            for (var column = 0; column < BytesPerHalfPage; column++)
            {
                byte leftByte = 0;
                byte rightByte = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    var row = page * 8 + bit;
                    if (IsPixelOn(pixels, column, row))
                        leftByte |= (byte)(1 << bit);
                    if (IsPixelOn(pixels, column + BytesPerHalfPage, row))
                        rightByte |= (byte)(1 << bit);
                }

                left[column] = leftByte;
                right[column] = rightByte;
            }
        }

        return frames;
    }

    private static bool IsPixelOn(byte[] pixels, int column, int row)
    {
        var offset = ((row * RequiredWidth) + column) * 4;
        var red = pixels[offset];
        var green = pixels[offset + 1];
        var blue = pixels[offset + 2];
        var alpha = pixels[offset + 3];
        var luminance = (red * 0.299d) + (green * 0.587d) + (blue * 0.114d);
        return alpha > 0 && luminance >= 64;
    }

    private static async Task<CustomSceneGenerationResult> TryRunGeneratorAsync(
        string executable,
        IReadOnlyList<string> prefixArguments,
        string scriptPath,
        string outputPath,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in prefixArguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add("generate");
            process.StartInfo.ArgumentList.Add(outputPath);
            foreach (var imagePath in imagePaths)
                process.StartInfo.ArgumentList.Add(imagePath);

            if (!process.Start())
                return new(false, $"无法启动 {executable}");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new(false, "生成超时，请减少帧数或检查 Python 环境");
            }

            var output = await outputTask;
            var error = await errorTask;
            return process.ExitCode == 0
                ? new(true, string.IsNullOrWhiteSpace(output) ? "生成成功" : output.Trim())
                : new(false, string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }
        catch (Exception ex)
        {
            return new(false, $"无法启动 {executable}：{ex.Message}");
        }
    }

    private static IEnumerable<(string Executable, IReadOnlyList<string> PrefixArguments)> CreatePythonCandidates()
    {
        yield return ("python", []);
        yield return ("py", ["-3"]);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}

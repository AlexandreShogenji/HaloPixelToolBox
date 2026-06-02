using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Text.Json;

namespace HaloPixelToolBox.Core.Services;

/// <summary>
/// 统一显示服务。新增功能只经过该服务调用现有 HaloPixelDevice，避免破坏底层 HID 协议逻辑。
/// </summary>
public class HaloPixelDisplayService
{
    private static readonly HttpClient HttpClient = new();

    public static event EventHandler<DisplayContentChangedEventArgs>? ContentSent;

    public HaloPixelDevice Device { get; }

    public HaloPixelDisplayService() : this(new HaloPixelDevice())
    {
    }

    public HaloPixelDisplayService(HaloPixelDevice device)
    {
        Device = device;
    }

    public bool EnsureDeviceReady() => Device.CurrentDevice is not null || Device.Initialize();

    public Task<bool> SendTextAsync(DisplayTextOptions options, CancellationToken cancellationToken = default)
    {
        if (!EnsureDeviceReady())
            return Task.FromResult(false);

        if (options.SendAt is not null)
        {
            var delay = options.SendAt.Value - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
                return SendTextAfterDelayAsync(options, delay, cancellationToken);
        }

        SendTextCore(options);
        return Task.FromResult(true);
    }

    public Task<bool> SendSubtitleCueAsync(SubtitleCue cue, DisplayTextOptions options, CancellationToken cancellationToken = default)
    {
        options.Text = cue.Text;
        return SendTextAsync(options, cancellationToken);
    }

    public void ShowBuiltInUi(HaloPixelUIModel uiModel, DisplayContentKind source = DisplayContentKind.System)
    {
        if (EnsureDeviceReady())
        {
            Device.SetUIModel(uiModel);
            NotifyContentSent(source, uiModel.ToString());
        }
    }

    public void ShowScreenScene(byte group, byte category, byte index, byte option)
    {
        if (EnsureDeviceReady())
        {
            Device.SetScreenScene(group, category, index, option);
            NotifyContentSent(DisplayContentKind.Scene, $"{group}-{category}-{index}-{option}");
        }
    }

    public void ShowPersonalScene(PersonalSceneDefinition scene)
    {
        if (EnsureDeviceReady())
        {
            Device.SetPersonalScene((byte)scene.CategoryIndex, (byte)scene.SceneIndex, scene.ResourceRemoteUrl);
            NotifyContentSent(DisplayContentKind.Scene, $"{scene.CategoryIndex}-{scene.SceneIndex}");
        }
    }

    public async Task<bool> ShowPixelSceneAsync(PersonalSceneDefinition scene, IProgress<PixelSceneUploadProgress>? uploadProgress = null, CancellationToken cancellationToken = default)
    {
        if (!EnsureDeviceReady())
            return false;

        var resourceBytes = await ResolvePixelSceneResourceAsync(scene, cancellationToken);
        if (resourceBytes is null || resourceBytes.Length == 0)
            return false;

        var uploadCategoryIndex = (byte)(scene.UploadCategoryIndex ?? scene.CategoryIndex);
        var result = await Task.Run(
            () => Device.SetPixelSceneResource(uploadCategoryIndex, (byte)scene.SceneIndex, resourceBytes, uploadProgress: uploadProgress, cancellationToken: cancellationToken),
            cancellationToken);

        if (result)
            NotifyContentSent(DisplayContentKind.Scene, $"{scene.CategoryIndex}-{scene.SceneIndex}");

        return result;
    }

    private async Task<bool> SendTextAfterDelayAsync(DisplayTextOptions options, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return false;

        SendTextCore(options);
        return true;
    }

    private void SendTextCore(DisplayTextOptions options)
    {
        var layout = options.ScrollDirection switch
        {
            TextScrollDirection.LeftToRight => HaloPixelTextLayout.ScrollLeftToRight,
            TextScrollDirection.RightToLeft => HaloPixelTextLayout.ScrollRightToLeft,
            _ => options.Layout
        };

        // Speed、Blink、Color 等字段当前官方文本协议未公开，先保留在模型中，后续扩展协议时在这里适配。
        Device.SetTextLayout(layout);
        var text = NormalizeText(options);
        Device.ShowText(text);
        NotifyContentSent(options.Source, text);
    }

    private static void NotifyContentSent(DisplayContentKind source, string? text)
        => ContentSent?.Invoke(null, new DisplayContentChangedEventArgs(source, text));

    private static async Task<byte[]?> ResolvePixelSceneResourceAsync(PersonalSceneDefinition scene, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(scene.ResourcePath) && File.Exists(scene.ResourcePath))
            return await File.ReadAllBytesAsync(scene.ResourcePath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(scene.ResourceRemoteUrl))
        {
            var cachedBytes = TryReadTempoHubCachedResource(scene.ResourceRemoteUrl);
            if (cachedBytes is not null)
                return cachedBytes;

            var cachePath = GetLocalSceneResourcePath(scene);
            if (File.Exists(cachePath))
                return await File.ReadAllBytesAsync(cachePath, cancellationToken);

            var downloaded = await HttpClient.GetByteArrayAsync(scene.ResourceRemoteUrl, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, downloaded, cancellationToken);
            return downloaded;
        }

        return null;
    }

    private static string GetLocalSceneResourcePath(PersonalSceneDefinition scene)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloPixelToolBox",
            "PersonalSceneResources",
            $"{scene.CategoryIndex}_{scene.SceneIndex}.bin");

    private static byte[]? TryReadTempoHubCachedResource(string resourceUrl)
    {
        var recordFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EDIFIER TempoHub",
            "cache",
            "resources",
            "resource_cache_record_file");

        if (!File.Exists(recordFile))
            return null;

        foreach (var line in File.ReadLines(recordFile))
        {
            if (!line.Contains(resourceUrl, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var binPath = document.RootElement.GetProperty("bin_path").GetString();
                if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
                    return null;

                var bytes = File.ReadAllBytes(binPath);
                return TryExtractPickleBytePayload(bytes) ?? bytes;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static byte[]? TryExtractPickleBytePayload(byte[] bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            switch (bytes[index])
            {
                case 0x42 when index + 5 <= bytes.Length:
                {
                    var length = BitConverter.ToInt32(bytes, index + 1);
                    if (length > 0 && index + 5 + length <= bytes.Length)
                        return bytes[(index + 5)..(index + 5 + length)];
                    break;
                }
                case 0x43 when index + 2 <= bytes.Length:
                {
                    var length = bytes[index + 1];
                    if (length > 0 && index + 2 + length <= bytes.Length)
                        return bytes[(index + 2)..(index + 2 + length)];
                    break;
                }
                case 0x8e when index + 9 <= bytes.Length:
                {
                    var length = BitConverter.ToInt64(bytes, index + 1);
                    if (length is > 0 and <= int.MaxValue && index + 9 + length <= bytes.Length)
                        return bytes[(index + 9)..(int)(index + 9 + length)];
                    break;
                }
            }
        }

        return null;
    }

    private static string NormalizeText(DisplayTextOptions options)
    {
        var text = options.Text.Trim();
        if (!options.MultiLine)
            return text.ReplaceLineEndings(" ");

        return text.ReplaceLineEndings(" / ");
    }
}

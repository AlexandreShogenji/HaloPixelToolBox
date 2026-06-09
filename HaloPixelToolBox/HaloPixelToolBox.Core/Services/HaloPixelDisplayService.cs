using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Core.Services;

/// <summary>
/// 统一显示服务。新增功能只经过该服务调用现有 HaloPixelDevice，避免破坏底层 HID 协议逻辑。
/// </summary>
public class HaloPixelDisplayService
{
    private const int MaxTextDisplayUnits = 64;
    private const int MaxTextUtf8Bytes = 55;
    private static readonly TimeSpan MinimumSegmentDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MaximumSegmentDelay = TimeSpan.FromMilliseconds(1400);
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
        var segments = SplitTextSegments(cue.Text);
        if (segments.Count <= 1)
        {
            options.Text = cue.Text;
            return SendTextAsync(options, cancellationToken);
        }

        return SendSubtitleSegmentsAsync(cue, options, segments, cancellationToken);
    }

    public Task<bool> SetDeviceVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(false);

        if (!EnsureDeviceReady())
            return Task.FromResult(false);

        Device.SetDeviceVolume(Math.Clamp(volume, 0, 16));
        return Task.FromResult(true);
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

    private async Task<bool> SendSubtitleSegmentsAsync(
        SubtitleCue cue,
        DisplayTextOptions options,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken)
    {
        var delay = ResolveSegmentDelay(cue, segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentOptions = CloneTextOptions(options, segments[index]);
            var sent = await SendTextAsync(segmentOptions, cancellationToken);
            if (!sent)
                return false;

            if (index < segments.Count - 1)
                await Task.Delay(delay, cancellationToken);
        }

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

    private static DisplayTextOptions CloneTextOptions(DisplayTextOptions source, string text)
    {
        return new DisplayTextOptions
        {
            Text = text,
            Source = source.Source,
            Layout = source.Layout,
            ScrollDirection = source.ScrollDirection,
            Speed = source.Speed,
            Blink = source.Blink,
            Color = source.Color,
            MultiLine = source.MultiLine,
            SendAt = source.SendAt
        };
    }

    private static TimeSpan ResolveSegmentDelay(SubtitleCue cue, int segmentCount)
    {
        if (segmentCount <= 1)
            return TimeSpan.Zero;

        var duration = cue.End > cue.Start ? cue.End - cue.Start : TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
            return MinimumSegmentDelay;

        var rawDelay = TimeSpan.FromMilliseconds(duration.TotalMilliseconds / segmentCount);
        if (rawDelay < MinimumSegmentDelay)
            return MinimumSegmentDelay;
        if (rawDelay > MaximumSegmentDelay)
            return MaximumSegmentDelay;

        return rawDelay;
    }

    private static IReadOnlyList<string> SplitTextSegments(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return [string.Empty];

        var segments = new List<string>();
        var builder = new StringBuilder();
        var units = 0;
        var bytes = 0;
        var lastBreakIndex = -1;
        var lastBreakUnits = 0;
        var lastBreakBytes = 0;

        foreach (var rune in normalized.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeUnits = GetDisplayUnitWeight(rune);
            var runeBytes = Encoding.UTF8.GetByteCount(runeText);
            var wouldOverflow = builder.Length > 0
                && (units + runeUnits > MaxTextDisplayUnits || bytes + runeBytes > MaxTextUtf8Bytes);

            if (wouldOverflow)
            {
                if (lastBreakIndex > 0)
                {
                    AddSegment(segments, builder.ToString(0, lastBreakIndex).Trim());
                    builder.Remove(0, lastBreakIndex);
                    units -= lastBreakUnits;
                    bytes -= lastBreakBytes;
                }
                else
                {
                    AddSegment(segments, builder.ToString().Trim());
                    builder.Clear();
                    units = 0;
                    bytes = 0;
                }

                TrimLeadingBreaks(builder, ref units, ref bytes);
                lastBreakIndex = -1;
                lastBreakUnits = 0;
                lastBreakBytes = 0;
            }

            builder.Append(runeText);
            units += runeUnits;
            bytes += runeBytes;

            if (IsNaturalBreak(rune))
            {
                lastBreakIndex = builder.Length;
                lastBreakUnits = units;
                lastBreakBytes = bytes;
            }
        }

        AddSegment(segments, builder.ToString().Trim());
        return segments.Count == 0 ? [normalized] : segments;
    }

    private static void AddSegment(ICollection<string> segments, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            segments.Add(text);
    }

    private static void TrimLeadingBreaks(StringBuilder builder, ref int units, ref int bytes)
    {
        while (TryReadFirstRune(builder, out var rune, out var charCount) && IsNaturalBreak(rune))
        {
            builder.Remove(0, charCount);
            units -= GetDisplayUnitWeight(rune);
            bytes -= Encoding.UTF8.GetByteCount(rune.ToString());
        }
    }

    private static bool TryReadFirstRune(StringBuilder builder, out Rune rune, out int charCount)
    {
        rune = default;
        charCount = 0;
        if (builder.Length == 0)
            return false;

        var first = builder[0];
        if (char.IsHighSurrogate(first) && builder.Length > 1 && char.IsLowSurrogate(builder[1]))
        {
            rune = new Rune(first, builder[1]);
            charCount = 2;
            return true;
        }

        if (char.IsSurrogate(first))
            return false;

        rune = new Rune(first);
        charCount = 1;
        return true;
    }

    private static int GetDisplayUnitWeight(Rune rune)
    {
        return IsWideCharacter(rune.Value) ? 2 : 1;
    }

    private static bool IsWideCharacter(int value)
    {
        return value is >= 0x2e80 and <= 0x9fff
            or >= 0xf900 and <= 0xfaff
            or >= 0xac00 and <= 0xd7af
            or >= 0xff01 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6;
    }

    private static bool IsNaturalBreak(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
            return true;

        return rune.Value is ','
            or '.'
            or ';'
            or ':'
            or '!'
            or '?'
            or '，'
            or '。'
            or '、'
            or '；'
            or '：'
            or '！'
            or '？'
            or '・'
            or ' '
            or '　';
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

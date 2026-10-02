using System.Security;
using System.Text;
using System.Text.Json;
using HaloPixelToolBox.Models;

namespace HaloPixelToolBox.Services.Audio;

/// <summary>Persists audio controls separately from device, microphone and FxSound settings.</summary>
public sealed class AudioProfileStore
{
    public const int MaximumPresetCount = 32;
    private const int MaximumFileBytes = 512 * 1024;
    private readonly object syncRoot = new();
    private readonly string filePath;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AudioProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaloPixelToolBox", "AudioControl", "profiles.json"))
    {
    }

    public AudioProfileStore(string filePath) => this.filePath = Path.GetFullPath(filePath);

    public Exception? LastError { get; private set; }
    public string FilePath => filePath;

    public AudioControlDocument Load()
    {
        lock (syncRoot)
        {
            LastError = null;
            foreach (var candidate in new[] { filePath, filePath + ".halo-backup" })
            {
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    if (new FileInfo(candidate).Length > MaximumFileBytes)
                        throw new IOException("音效预设文件过大");
                    var document = JsonSerializer.Deserialize<AudioControlDocument>(File.ReadAllText(candidate), Options)
                        ?? throw new JsonException("音效预设文件为空");
                    return ValidateAndCopy(document);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    LastError ??= ex;
                    // Leave unreadable primary and last-good backup untouched. Explicit Save
                    // may replace invalid bytes only after a user changes the settings.
                }
            }
            return CreateDefaultDocument();
        }
    }

    public bool Save(AudioControlDocument document)
    {
        lock (syncRoot)
        {
            LastError = null;
            try
            {
                var validated = ValidateAndCopy(document);
                // Preserve a previously valid backup if the current primary is corrupt.
                if (File.Exists(filePath))
                {
                    try
                    {
                        var old = JsonSerializer.Deserialize<AudioControlDocument>(File.ReadAllText(filePath), Options)
                            ?? throw new JsonException("音效预设文件为空");
                        if (old.SchemaVersion > 1)
                        {
                            LastError = new JsonException("音效预设来自较新版本，未覆盖现有文件");
                            return false;
                        }
                        ValidateAndCopy(old);
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        File.Move(filePath, filePath + ".invalid-" + Guid.NewGuid().ToString("N"));
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(validated, Options));
                EqualizerApoService.AtomicWriteAsync(filePath, bytes, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                LastError = ex;
                return false;
            }
        }
    }

    public static AudioControlDocument CreateDefaultDocument() => new()
    {
        CurrentProfile = new AudioControlProfile(),
        Presets =
        [
            new AudioControlPreset { Id = "flat", Name = "原声", Profile = new AudioControlProfile { Enabled = true } },
            new AudioControlPreset
            {
                Id = "voice", Name = "人声清晰",
                Profile = new AudioControlProfile { Enabled = true, BandGainsDb = [-2, -1, 0, 0, 1, 2, 3, 2, 1, 0] }
            },
            new AudioControlPreset
            {
                Id = "bass", Name = "低音增强",
                Profile = new AudioControlProfile { Enabled = true, BandGainsDb = [3, 4, 3, 1, 0, 0, 0, 0, 0, 0] }
            }
        ]
    };

    private static AudioControlDocument ValidateAndCopy(AudioControlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1)
            throw new JsonException("不支持的音效预设版本");
        EqualizerApoService.ValidateProfile(document.CurrentProfile);
        if (document.Presets is null || document.Presets.Count > MaximumPresetCount)
            throw new ArgumentException("最多保存 32 个音效预设");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<AudioControlPreset>();
        foreach (var preset in document.Presets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Id) || preset.Id.Length > 64
                || preset.Id.Any(char.IsControl) || !ids.Add(preset.Id))
                throw new ArgumentException("音效预设标识为空或重复");
            var name = preset.Name?.Trim() ?? string.Empty;
            if (name.Length is < 1 or > 32 || name.Any(char.IsControl) || !names.Add(name))
                throw new ArgumentException("音效预设名称须为 1 至 32 个字符，且不能重复");
            EqualizerApoService.ValidateProfile(preset.Profile);
            copies.Add(preset with { Name = name, Profile = preset.Profile.Copy() });
        }
        return document with { CurrentProfile = document.CurrentProfile.Copy(), Presets = copies };
    }

    private static bool IsRecoverable(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException;
}

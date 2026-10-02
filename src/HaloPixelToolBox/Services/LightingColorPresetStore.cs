using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaloPixelToolBox.Models;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Services;

/// <summary>
/// Persists named lighting color pairs independently from the automatically saved profile.
/// </summary>
public sealed class LightingColorPresetStore
{
    public const int MaximumPresetCount = 32;

    private const int CurrentSchemaVersion = 1;
    private const int MaximumNameLength = 32;
    private const string PresetFileName = "LightingColorPresets.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object syncRoot = new();
    private readonly string filePath;

    public LightingColorPresetStore()
        : this(Path.Combine(AppPathHelper.LocalProfile, PresetFileName))
    {
    }

    internal LightingColorPresetStore(string filePath)
    {
        this.filePath = filePath;
    }

    /// <summary>
    /// The most recent recoverable persistence error. Load and Save never surface it to the UI.
    /// </summary>
    public Exception? LastError { get; private set; }

    public IReadOnlyList<LightingColorPreset> Load()
    {
        lock (syncRoot)
        {
            LastError = null;

            try
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        var presets = ReadAndNormalize(filePath, out var needsRewrite);
                        if (needsRewrite)
                        {
                            TrySaveCore(presets);
                        }

                        return presets;
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        LastError = exception;
                        TryQuarantine(filePath);
                    }
                }

                var backupPath = GetBackupPath();
                if (!File.Exists(backupPath))
                {
                    return [];
                }

                try
                {
                    var presets = ReadAndNormalize(backupPath, out _);

                    // Restore the last known-good copy only when the invalid primary file was
                    // successfully moved out of the way. Otherwise return the data in memory and
                    // leave both files untouched.
                    if (!File.Exists(filePath))
                    {
                        TrySaveCore(presets);
                    }

                    return presets;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    LastError = exception;
                    return [];
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                LastError = exception;
                return [];
            }
        }
    }

    public void Save(IEnumerable<LightingColorPreset> presets) => TrySave(presets);

    public bool TrySave(IEnumerable<LightingColorPreset> presets)
    {
        lock (syncRoot)
        {
            LastError = null;

            try
            {
                var normalized = NormalizePresets(presets, out _);
                SaveCore(normalized);
                return true;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                LastError = exception;
                return false;
            }
        }
    }

    private static IReadOnlyList<LightingColorPreset> ReadAndNormalize(string path, out bool needsRewrite)
    {
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("The lighting color preset file is empty.");
        }

        var file = JsonSerializer.Deserialize<PresetFile>(json, SerializerOptions)
            ?? throw new JsonException("The lighting color preset file could not be read.");

        var presets = NormalizePresets(file.Presets, out var presetsChanged);
        // Older or malformed files are upgraded in place. A file written by a newer
        // application version remains untouched until the user explicitly changes a preset.
        needsRewrite = file.SchemaVersion < CurrentSchemaVersion
            || file.SchemaVersion == CurrentSchemaVersion && presetsChanged;
        return presets;
    }

    private static List<LightingColorPreset> NormalizePresets(
        IEnumerable<LightingColorPreset?>? presets,
        out bool changed)
    {
        changed = presets is null;
        var normalized = new List<LightingColorPreset>(MaximumPresetCount);
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (presets is null)
        {
            return normalized;
        }

        foreach (var preset in presets)
        {
            if (normalized.Count >= MaximumPresetCount)
            {
                changed = true;
                break;
            }

            if (preset is null)
            {
                changed = true;
                continue;
            }

            var id = NormalizeId(preset.Id, knownIds);
            var name = NormalizeName(preset.Name, normalized.Count + 1);
            var item = preset with
            {
                Id = id,
                Name = name,
                AmbientRed = Math.Clamp(preset.AmbientRed, 0, 255),
                AmbientGreen = Math.Clamp(preset.AmbientGreen, 0, 255),
                AmbientBlue = Math.Clamp(preset.AmbientBlue, 0, 255),
                PixelRed = Math.Clamp(preset.PixelRed, 0, 255),
                PixelGreen = Math.Clamp(preset.PixelGreen, 0, 255),
                PixelBlue = Math.Clamp(preset.PixelBlue, 0, 255)
            };

            changed |= item != preset;
            normalized.Add(item);
        }

        return normalized;
    }

    private static string NormalizeId(string? value, ISet<string> knownIds)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            var normalized = parsed.ToString("N");
            if (knownIds.Add(normalized))
            {
                return normalized;
            }
        }

        string generated;
        do
        {
            generated = Guid.NewGuid().ToString("N");
        }
        while (!knownIds.Add(generated));

        return generated;
    }

    private static string NormalizeName(string? value, int fallbackIndex)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            name = $"配色方案 {fallbackIndex}";
        }

        return name.Length <= MaximumNameLength ? name : name[..MaximumNameLength];
    }

    private void SaveCore(IReadOnlyList<LightingColorPreset> presets)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The lighting color preset directory is invalid.");
        }

        Directory.CreateDirectory(directory);

        var temporaryPath = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new PresetFile
            {
                SchemaVersion = CurrentSchemaVersion,
                Presets = presets.Select(static preset => (LightingColorPreset?)preset).ToList()
            };

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, file, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(filePath))
            {
                File.Move(temporaryPath, filePath);
                return;
            }

            try
            {
                File.Replace(temporaryPath, filePath, GetBackupPath(), ignoreMetadataErrors: true);
            }
            catch (Exception exception) when (
                exception is IOException or PlatformNotSupportedException
                && File.Exists(temporaryPath))
            {
                File.Copy(filePath, GetBackupPath(), overwrite: true);
                File.Move(temporaryPath, filePath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private void TrySaveCore(IReadOnlyList<LightingColorPreset> presets)
    {
        try
        {
            SaveCore(presets);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastError ??= exception;
        }
    }

    private void TryQuarantine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var quarantinePath = Path.Combine(
                directory,
                $"{fileName}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
            File.Move(path, quarantinePath);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastError ??= exception;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private string GetBackupPath() => $"{filePath}.bak";

    private static bool IsRecoverable(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException;

    private sealed class PresetFile
    {
        public int SchemaVersion { get; set; }

        public List<LightingColorPreset?> Presets { get; set; } = [];
    }
}

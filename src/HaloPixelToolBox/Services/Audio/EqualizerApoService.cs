using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using HaloPixelToolBox.Models;
using Microsoft.Win32;

namespace HaloPixelToolBox.Services.Audio;

/// <summary>Controls a separate Equalizer APO configuration without starting FxSound.</summary>
public sealed class EqualizerApoService
{
    public const string ManagedRelativePath = "HaloPixelToolBox/AudioControl.txt";
    private const string ManagedHeader = "# Halo Pixel ToolBox audio configuration v1";
    private const string IncludeBegin = "# BEGIN Halo Pixel ToolBox Audio Control";
    private const string IncludeEnd = "# END Halo Pixel ToolBox Audio Control";
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private static readonly Guid PreMixClassId = new("eacd2258-fcac-4ff4-b36d-419e924a6d79");
    private static readonly Guid PostMixClassId = new("ec1cc9ce-faed-4822-828a-82a81a6f018f");
    private const string EffectsPropertySet = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private static readonly HashSet<int> AudioEffectRoles = [1, 2, 5, 6, 7, 13, 14, 15];
    private static readonly HashSet<int> PostMixEffectRoles = [2, 6, 7, 14, 15];
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string? installOverride;
    private readonly string? configOverride;
    private readonly Func<string, string, (AudioEndpointRegistration Registration, bool EnhancementsDisabled)>? registrationOverride;

    public EqualizerApoService()
    {
    }

    /// <summary>Explicit paths and a registration probe permit isolated integration tests.</summary>
    public EqualizerApoService(
        string installDirectory,
        string configDirectory,
        Func<string, string, (AudioEndpointRegistration Registration, bool EnhancementsDisabled)>? registrationProbe = null)
    {
        installOverride = Path.GetFullPath(installDirectory);
        configOverride = Path.GetFullPath(configDirectory);
        registrationOverride = registrationProbe;
    }

    public AudioBackendStatus Probe(AudioControlProfile profile)
    {
        var (install, config) = DiscoverPaths();
        var configurator = new[] { "DeviceSelector.exe", "Configurator.exe" }
            .Select(name => Path.Combine(install, name))
            .FirstOrDefault(File.Exists) ?? string.Empty;
        var installed = !string.IsNullOrEmpty(install)
            && File.Exists(Path.Combine(install, "EqualizerAPO.dll"))
            && !string.IsNullOrEmpty(configurator);
        var managedPath = string.IsNullOrEmpty(config) ? string.Empty : Path.Combine(config, "HaloPixelToolBox", "AudioControl.txt");
        var status = new AudioBackendStatus
        {
            IsInstalled = installed,
            InstallDirectory = install,
            ConfigDirectory = config,
            ConfiguratorPath = configurator,
            ManagedConfigPath = managedPath,
            Message = installed ? "请选择输出设备并接入独立音效配置" : "尚未安装 Equalizer APO；安装并为音箱启用组件后即可独立调节音效"
        };
        if (!installed)
            return status;
        if (!TryGetEndpointGuid(profile.EndpointId, out var guid))
            return status with { Message = "请选择有效的输出设备" };

        var registrationResult = registrationOverride is null
            ? ProbeEndpointRegistration(install, guid)
            : ProbeRegistrationOverride(install, guid);
        var (registration, enhancementsDisabled, postMixMissing) = registrationResult;
        var connected = false;
        var configurationWritten = false;
        try
        {
            var mainPath = Path.Combine(config, "config.txt");
            if (File.Exists(mainPath))
                connected = HasManagedInclude(ReadBoundedText(mainPath));
            if (File.Exists(managedPath))
            {
                try
                {
                    configurationWritten = ReadBoundedText(managedPath)
                        .Equals(BuildConfiguration(profile), StringComparison.Ordinal);
                }
                catch (ArgumentException)
                {
                    // An invalid current profile never matches an existing configuration.
                }
            }
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return status with
            {
                EndpointRegistration = registration,
                EnhancementsDisabled = enhancementsDisabled,
                RequiresElevation = ex is UnauthorizedAccessException or SecurityException,
                Message = "无法读取 Equalizer APO 配置：" + ex.Message
            };
        }

        return WithEndpointReadiness(status with { ConfigurationWritten = configurationWritten },
            registration, enhancementsDisabled, connected, postMixMissing);
    }

    internal static AudioBackendStatus WithEndpointReadiness(
        AudioBackendStatus status,
        AudioEndpointRegistration registration,
        bool enhancementsDisabled,
        bool connected,
        bool postMixMissing)
    {
        var canApply = connected && registration == AudioEndpointRegistration.Registered && !enhancementsDisabled;
        var message = enhancementsDisabled
            ? "所选设备的音频增强已关闭，请在 Windows 声音设置中启用音频增强"
            : postMixMissing
                ? "所选设备尚未接入后混音处理（Post-Mix）；请打开设备配置器并启用 Post-Mix 安装选项"
                : registration == AudioEndpointRegistration.NotRegistered
                ? "Equalizer APO 已安装，但所选设备尚未接入；请打开设备配置并选择音箱"
                : registration == AudioEndpointRegistration.Unknown
                    ? "无法确认所选设备是否已接入 Equalizer APO；请通过设备配置核对"
                    : !connected
                        ? "所选设备已注册组件，请点击“连接工具箱曲线”；实际效果需播放音频验证"
                        : status.ConfigurationWritten
                            ? "当前音效参数已写入，设备已注册后混音组件；实际效果需播放音频验证"
                            : "组件已连接，但当前参数尚未写入匹配的工具箱曲线；请重新应用参数";
        return status with
        {
            IsIncludeConnected = connected,
            EndpointRegistration = registration,
            EnhancementsDisabled = enhancementsDisabled,
            CanApply = canApply,
            Message = message
        };
    }

    public Task<AudioBackendStatus> ConnectAsync(AudioControlProfile profile, CancellationToken cancellationToken = default)
        => ApplyAsync(profile, connectInclude: true, cancellationToken);

    public async Task<AudioBackendStatus> ApplyAsync(
        AudioControlProfile profile,
        bool connectInclude = false,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile, requireEndpoint: true);
        var configuration = BuildConfiguration(profile);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = Probe(profile);
            if (!status.IsInstalled)
                return status;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var managedDirectory = Path.GetDirectoryName(status.ManagedConfigPath)!;
                EnsureNoReparsePoint(status.ConfigDirectory);
                EnsureNoReparsePoint(managedDirectory);
                Directory.CreateDirectory(managedDirectory);
                if (File.Exists(status.ManagedConfigPath)
                    && !(await ReadBoundedTextWithRetryAsync(status.ManagedConfigPath, cancellationToken).ConfigureAwait(false)).StartsWith(ManagedHeader, StringComparison.Ordinal))
                    throw new IOException("自有配置路径已存在其他内容，未覆盖；请检查 " + status.ManagedConfigPath);
                await AtomicWriteAsync(status.ManagedConfigPath, Encoding.UTF8.GetBytes(configuration), cancellationToken).ConfigureAwait(false);
                if (connectInclude)
                    await ConnectIncludeAsync(status.ConfigDirectory, cancellationToken).ConfigureAwait(false);
                var updated = Probe(profile);
                return updated with
                {
                    Message = ComposeWrittenMessage(updated, profile.Enabled)
                };
            }
            catch (Exception ex) when (IsIoError(ex))
            {
                return Probe(profile) with
                {
                    RequiresElevation = ex is UnauthorizedAccessException or SecurityException,
                    Message = "音效配置未完成：" + ex.Message
                };
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public static string BuildConfiguration(AudioControlProfile profile)
    {
        ValidateProfile(profile, requireEndpoint: true);
        TryGetEndpointGuid(profile.EndpointId, out var guid);
        var text = new StringBuilder();
        text.AppendLine(ManagedHeader);
        text.AppendLine("# Managed only by Halo Pixel ToolBox. Existing user filters are kept separately.");
        text.Append("Device: ").AppendLine(guid);
        text.AppendLine("Channel: all");
        text.AppendLine("Stage: post-mix");
        if (profile.Enabled)
        {
            text.Append("Preamp: ").Append(Format(profile.EffectivePreampDb)).AppendLine(" dB");
            text.Append("GraphicEQ: ");
            for (var i = 0; i < AudioControlProfile.FrequenciesHz.Count; i++)
            {
                if (i > 0)
                    text.Append("; ");
                text.Append(Format(AudioControlProfile.FrequenciesHz[i])).Append(' ').Append(Format(profile.BandGainsDb[i]));
            }
            text.AppendLine();
            if (profile.Balance != 0)
            {
                var factor = Math.Max(0.000001, 1 - Math.Abs(profile.Balance) / 100);
                text.AppendLine(profile.Balance > 0 ? "Channel: L" : "Channel: R");
                text.Append("Preamp: ").Append(Format(20 * Math.Log10(factor))).AppendLine(" dB");
            }
        }
        else
        {
            text.AppendLine("# Effects disabled: no EQ, gain or balance filters are applied.");
        }
        // Device commands always execute, even after a non-matching Device filter. Restore
        // defaults so this Include cannot narrow the scope of subsequent user commands.
        text.AppendLine("Device: all");
        text.AppendLine("Channel: all");
        text.AppendLine("Stage: post-mix");
        return text.ToString();
    }

    public static void ValidateProfile(AudioControlProfile profile, bool requireEndpoint = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!double.IsFinite(profile.PreampDb) || profile.PreampDb < -30 || profile.PreampDb > 12)
            throw new ArgumentOutOfRangeException(nameof(profile.PreampDb), "前级增益必须在 -30 到 12 dB 之间");
        if (!double.IsFinite(profile.Balance) || profile.Balance < -100 || profile.Balance > 100)
            throw new ArgumentOutOfRangeException(nameof(profile.Balance), "左右平衡必须在 -100 到 100 之间");
        if (profile.BandGainsDb is null || profile.BandGainsDb.Length != 10
            || profile.BandGainsDb.Any(value => !double.IsFinite(value) || value < -12 || value > 12))
            throw new ArgumentException("均衡器需要 10 个介于 -12 到 12 dB 的有效值", nameof(profile.BandGainsDb));
        if ((requireEndpoint || !string.IsNullOrEmpty(profile.EndpointId))
            && !TryGetEndpointGuid(profile.EndpointId, out _))
            throw new ArgumentException("输出设备标识无效", nameof(profile.EndpointId));
    }

    public static bool TryGetEndpointGuid(string? endpointId, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 128)
            return false;
        var candidate = endpointId.Trim();
        if (Regex.IsMatch(candidate, @"^\{0\.0\.0\.[0-9a-fA-F]{8}\}\.\{[0-9a-fA-F-]{36}\}$", RegexOptions.CultureInvariant))
            candidate = candidate[(candidate.LastIndexOf('{'))..];
        if (!Guid.TryParseExact(candidate, "B", out var parsed) && !Guid.TryParseExact(candidate, "D", out parsed))
            return false;
        guid = parsed.ToString("B");
        return true;
    }

    private async Task ConnectIncludeAsync(string configDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(configDirectory, "config.txt");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = File.Exists(path);
            var original = existed ? ReadBoundedBytes(path) : [];
            var decoded = DecodeText(original, out var encoding);
            if (HasManagedInclude(decoded))
                return;
            if (decoded.Contains(IncludeBegin, StringComparison.Ordinal) || decoded.Contains(IncludeEnd, StringComparison.Ordinal)
                || decoded.Split('\n').Any(line => Regex.IsMatch(line.Trim(), @"^Include\s*:\s*""?HaloPixelToolBox[\\/]AudioControl\.txt""?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new IOException("发现已修改或手工接入的工具箱 Include，未重复添加；请检查现有接入块");
            var ifDepth = 0;
            foreach (var line in decoded.Split('\n'))
            {
                if (Regex.IsMatch(line, @"^\s*If\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    ifDepth++;
                else if (Regex.IsMatch(line, @"^\s*EndIf\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    ifDepth--;
                if (ifDepth < 0)
                    break;
            }
            if (ifDepth != 0)
                throw new IOException("主配置中 If/EndIf 未闭合，未自动接入；请先修复原配置");
            // Only append a block. Do not rewrite, normalize or remove user configuration.
            var newline = decoded.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var block = (decoded.Length > 0 && !decoded.EndsWith('\n') ? newline : string.Empty)
                + newline + IncludeBegin + newline
                + "Device: all" + newline + "Channel: all" + newline + "Stage: post-mix" + newline
                + "Include: " + ManagedRelativePath + newline + IncludeEnd + newline;
            var tail = encoding.GetBytes(block);
            var combined = new byte[original.Length + tail.Length];
            original.CopyTo(combined, 0);
            tail.CopyTo(combined, original.Length);
            // Detect edits made by another editor before replacement. Retry from their latest
            // bytes so configuration changes made while connecting are not silently lost.
            if (File.Exists(path) != existed || existed && !ReadBoundedBytes(path).AsSpan().SequenceEqual(original))
                continue;
            await AtomicWriteAsync(path, combined, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new IOException("配置正在被其他程序修改，请稍后重新接入");
    }

    private static bool HasManagedInclude(string text)
    {
        var begin = text.IndexOf(IncludeBegin, StringComparison.Ordinal);
        if (begin < 0)
            return false;
        var end = text.IndexOf(IncludeEnd, begin, StringComparison.Ordinal);
        if (end < 0)
            return false;
        if (text.IndexOf(IncludeBegin, begin + IncludeBegin.Length, StringComparison.Ordinal) >= 0
            || text.IndexOf(IncludeEnd, end + IncludeEnd.Length, StringComparison.Ordinal) >= 0)
            return false;
        var depth = 0;
        foreach (var line in text[..begin].Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*If\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                depth++;
            else if (Regex.IsMatch(line, @"^\s*EndIf\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                depth--;
            if (depth < 0)
                return false;
        }
        if (depth != 0)
            return false;
        var lines = text[begin..end].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        return lines.SequenceEqual(new[] { IncludeBegin, "Device: all", "Channel: all", "Stage: post-mix", "Include: " + ManagedRelativePath }, StringComparer.OrdinalIgnoreCase);
    }

    private (string Install, string Config) DiscoverPaths()
    {
        if (installOverride is not null)
            return (installOverride, configOverride!);
        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hive.OpenSubKey(@"SOFTWARE\EqualizerAPO", writable: false);
                    if (key?.GetValue("InstallPath") is string install && Path.IsPathFullyQualified(install))
                    {
                        var config = key.GetValue("ConfigPath") as string;
                        return (Path.GetFullPath(install), !string.IsNullOrWhiteSpace(config) && Path.IsPathFullyQualified(config)
                            ? Path.GetFullPath(config) : Path.Combine(install, "config"));
                    }
                }
                catch (Exception ex) when (IsIoError(ex) || ex is ArgumentException)
                {
                    // A missing or inaccessible registry entry still permits standard-path discovery.
                }
            }
        }
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(programFiles))
                continue;
            var candidate = Path.Combine(programFiles, "EqualizerAPO");
            if (Directory.Exists(candidate))
                return (candidate, Path.Combine(candidate, "config"));
        }
        return (string.Empty, string.Empty);
    }

    private (AudioEndpointRegistration Registration, bool EnhancementsDisabled, bool PostMixMissing) ProbeRegistrationOverride(string install, string guid)
    {
        var overridden = registrationOverride!(install, guid);
        return (overridden.Registration, overridden.EnhancementsDisabled, false);
    }

    private static (AudioEndpointRegistration Registration, bool EnhancementsDisabled, bool PostMixMissing) ProbeEndpointRegistration(string install, string guid)
    {
        if (!OperatingSystem.IsWindows())
            return (AudioEndpointRegistration.Unknown, false, false);
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var endpoint = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + guid, false);
            if (endpoint is null)
                return (AudioEndpointRegistration.Unknown, false, false);
            using var effects = endpoint.OpenSubKey("FxProperties", false);
            if (effects is null)
                return (AudioEndpointRegistration.NotRegistered, false, false);
            var disabled = effects.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5") is int enhancementFlag && enhancementFlag != 0;
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            var entries = new List<KeyValuePair<string, object?>>();
            foreach (var valueName in effects.GetValueNames())
                entries.Add(new(valueName, effects.GetValue(valueName)));
            var classified = ClassifyEndpointEffects(entries, classId =>
            {
                if (!OperatingSystem.IsWindows())
                    return false;
                using var clsid = classes.OpenSubKey(@"CLSID\" + classId.ToString("B") + @"\InprocServer32", false);
                if (clsid?.GetValue(null) is not string server)
                    return false;
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(server.Trim('"')))
                    .Equals(Path.Combine(install, "EqualizerAPO.dll"), StringComparison.OrdinalIgnoreCase);
            });
            return (classified.Registration, disabled, classified.PostMixMissing);
        }
        catch (Exception ex) when (IsIoError(ex) || ex is ArgumentException or NotSupportedException)
        {
            return (AudioEndpointRegistration.Unknown, false, false);
        }
    }

    /// <summary>Only a genuine Post-Mix APO in a post-mix role can execute our Stage: post-mix filters.</summary>
    internal static (AudioEndpointRegistration Registration, bool PostMixMissing) ClassifyEndpointEffects(
        IEnumerable<KeyValuePair<string, object?>> properties,
        Func<Guid, bool> isEqualizerApoServer)
    {
        var anyEqualizerApo = false;
        foreach (var property in properties)
        {
            var parts = property.Key.Split(',');
            if (parts.Length != 2 || !parts[0].Trim().Equals(EffectsPropertySet, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var role)
                || !AudioEffectRoles.Contains(role))
                continue;
            IEnumerable<string> classIds = property.Value is string single ? [single]
                : property.Value is string[] multiple ? multiple : [];
            foreach (var text in classIds)
            {
                if (!Guid.TryParse(text, out var classId) || classId != PreMixClassId && classId != PostMixClassId
                    || !isEqualizerApoServer(classId))
                    continue;
                anyEqualizerApo = true;
                if (classId == PostMixClassId && PostMixEffectRoles.Contains(role))
                    return (AudioEndpointRegistration.Registered, false);
            }
        }
        return (AudioEndpointRegistration.NotRegistered, anyEqualizerApo);
    }

    private static string ComposeWrittenMessage(AudioBackendStatus status, bool enabled)
        => status.CanApply && status.ConfigurationWritten
            ? enabled ? "音效参数已写入独立组件；请播放音频确认实际效果" : "工具箱音效已关闭；其他程序的音效配置保持不变"
            : status.ConfigurationWritten
                ? "参数已保存到组件配置，但尚未确认可应用：" + status.Message
                : "组件中尚未确认存在匹配当前参数的完整配置：" + status.Message;

    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(path);
        var directory = Path.GetDirectoryName(path)!;
        EnsureNoReparsePoint(directory);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".halo-audio-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(path))
                        File.Replace(temporary, path, path + ".halo-backup", ignoreMetadataErrors: true);
                    else
                        File.Move(temporary, path);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(40 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (IsIoError(ex)) { }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝通过符号链接修改音频配置：" + path);
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        if (new FileInfo(path).Length > MaximumConfigBytes)
            throw new IOException("音频配置文件过大，未自动修改");
        return File.ReadAllBytes(path);
    }

    private static string ReadBoundedText(string path) => DecodeText(ReadBoundedBytes(path), out _);

    private static async Task<string> ReadBoundedTextWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > MaximumConfigBytes)
                    throw new IOException("音频配置文件过大，未自动修改");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > MaximumConfigBytes)
                    throw new IOException("音频配置文件过大，未自动修改");
                return DecodeText(bytes, out _);
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(40 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string DecodeText(byte[] bytes, out Encoding encoding)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            encoding = Encoding.Unicode;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            encoding = Encoding.BigEndianUnicode;
        else
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return encoding.GetString(bytes).TrimStart('\uFEFF');
    }

    private static bool IsIoError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or DecoderFallbackException;

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

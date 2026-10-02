using System.Globalization;
using System.Text;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Invalid(Action action, string name)
{
    try { action(); throw new Exception("FAIL: did not reject " + name); }
    catch (ArgumentException) { passed++; }
}
var endpoint = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
var profile = new AudioControlProfile { EndpointId = endpoint, Enabled = true };
Check(EqualizerApoService.TryGetEndpointGuid(endpoint, out var guid) && guid == "{11111111-2222-3333-4444-555555555555}", "canonical endpoint GUID");
Check(EqualizerApoService.TryGetEndpointGuid(guid, out _), "plain GUID accepted");
foreach (var bad in new[] { "all", endpoint + "; all", endpoint + "\nPreamp: 12 dB", "abc " + endpoint, "{0.0.0.00000000}.{not-a-guid}" })
    Check(!EqualizerApoService.TryGetEndpointGuid(bad, out _), "device injection rejected");
var originalCulture = CultureInfo.CurrentCulture;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
var config = EqualizerApoService.BuildConfiguration(profile with { PreampDb = -2.5 });
CultureInfo.CurrentCulture = originalCulture;
Check(config.Contains("Preamp: -2.5 dB") && config.Contains("31.25 0; 62.5 0"), "locale invariant decimal");
Check(config.Contains("Device: " + guid), "configuration selects GUID only");
Check(config.EndsWith("Device: all" + Environment.NewLine + "Channel: all" + Environment.NewLine + "Stage: post-mix" + Environment.NewLine), "scope restored after filters");
var disabled = EqualizerApoService.BuildConfiguration(profile with { Enabled = false, PreampDb = 6, Balance = 100 });
Check(!disabled.Contains("Preamp:") && !disabled.Contains("GraphicEQ:"), "disable removes all effects");
var boosted = profile with { PreampDb = 3, BandGainsDb = [5, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
Check(boosted.EffectivePreampDb == -5, "headroom compensates positive band gain");
Check((boosted with { AutoHeadroom = false }).EffectivePreampDb == 3, "manual preamp preserved without headroom");
Check((boosted with { PreampDb = -9 }).EffectivePreampDb == -9, "existing stronger attenuation preserved");
Check(EqualizerApoService.BuildConfiguration(profile with { Balance = 50 }).Contains("Channel: L" + Environment.NewLine + "Preamp: -6.0206 dB"), "right balance attenuates left");
Check(EqualizerApoService.BuildConfiguration(profile with { Balance = -100 }).Contains("Channel: R" + Environment.NewLine + "Preamp: -120 dB"), "left balance endpoint finite attenuation");
Invalid(() => EqualizerApoService.BuildConfiguration(profile with { PreampDb = double.NaN }), "NaN preamp");
Invalid(() => EqualizerApoService.BuildConfiguration(profile with { PreampDb = 13 }), "preamp bound");
Invalid(() => EqualizerApoService.BuildConfiguration(profile with { Balance = double.PositiveInfinity }), "infinite balance");
Invalid(() => EqualizerApoService.BuildConfiguration(profile with { BandGainsDb = new double[9] }), "band count");
Invalid(() => EqualizerApoService.BuildConfiguration(profile with { BandGainsDb = [13, 0, 0, 0, 0, 0, 0, 0, 0, 0] }), "band bound");

var effectSet = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
var preMixClass = "{eacd2258-fcac-4ff4-b36d-419e924a6d79}";
var postMixClass = "{ec1cc9ce-faed-4822-828a-82a81a6f018f}";
var vendorClass = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";
(AudioEndpointRegistration Registration, bool PostMixMissing) Classify(int role, object value, bool validServer = true)
    => EqualizerApoService.ClassifyEndpointEffects(
        [new KeyValuePair<string, object?>(effectSet + "," + role, value)], _ => validServer);
foreach (var postRole in new[] { 2, 6, 7, 14, 15 })
{
    var result = Classify(postRole, postMixClass);
    Check(result.Registration == AudioEndpointRegistration.Registered && !result.PostMixMissing,
        "post-mix APO accepted in GFX/MFX/EFX or composite post role " + postRole);
    Check(Classify(postRole, new[] { vendorClass, postMixClass }).Registration == AudioEndpointRegistration.Registered,
        "post-mix found in multi-string effects role " + postRole);
    var onlyPre = Classify(postRole, preMixClass);
    Check(onlyPre.Registration != AudioEndpointRegistration.Registered && onlyPre.PostMixMissing,
        "pre-mix class cannot satisfy post-mix role " + postRole);
}
foreach (var preRole in new[] { 1, 5, 13 })
{
    Check(Classify(preRole, preMixClass).PostMixMissing, "only pre-mix attachment requires post-mix role " + preRole);
    Check(Classify(preRole, postMixClass).Registration != AudioEndpointRegistration.Registered,
        "post-mix class in a pre-mix-only role is not ready " + preRole);
}
Check(Classify(7, postMixClass, validServer: false).Registration == AudioEndpointRegistration.NotRegistered,
    "wrong or missing COM server not ready even with expected class ID");
Check(Classify(7, vendorClass).Registration == AudioEndpointRegistration.NotRegistered,
    "unrelated class not accepted even if resolver recognizes its DLL");
Check(Classify(7, postMixClass + ";" + vendorClass).Registration == AudioEndpointRegistration.NotRegistered,
    "malformed composite REG_SZ not mistaken for genuine class ID");
Check(Classify(99, postMixClass).Registration == AudioEndpointRegistration.NotRegistered,
    "unrelated property ID cannot confer post-mix readiness");
var modeProperty = EqualizerApoService.ClassifyEndpointEffects(
    [new KeyValuePair<string, object?>("{d3993a3f-99c2-4402-b5ec-a92a0367664b},7", new[] { postMixClass })], _ => true);
Check(modeProperty.Registration != AudioEndpointRegistration.Registered, "processing-mode values are not effect attachments");
var caseInsensitiveRole = EqualizerApoService.ClassifyEndpointEffects(
    [new KeyValuePair<string, object?>(effectSet.ToUpperInvariant() + ",7", postMixClass.ToUpperInvariant())], _ => true);
Check(caseInsensitiveRole.Registration == AudioEndpointRegistration.Registered, "registry GUID casing does not change readiness");
var preOnly = Classify(5, preMixClass);
var preOnlyStatus = EqualizerApoService.WithEndpointReadiness(new AudioBackendStatus { ConfigurationWritten = true },
    preOnly.Registration, enhancementsDisabled: false, connected: true, preOnly.PostMixMissing);
Check(!preOnlyStatus.CanApply && preOnlyStatus.Message.Contains("Post-Mix") && preOnlyStatus.Message.Contains("配置器"),
    "only pre-mix reports not ready and directs user to configurator post-mix");

var sandbox = Path.Combine(Path.GetTempPath(), "halo-audio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    var install = Path.Combine(sandbox, "installation");
    var configDirectory = Path.Combine(sandbox, "configuration");
    Directory.CreateDirectory(install);
    Directory.CreateDirectory(configDirectory);
    var registryState = AudioEndpointRegistration.Registered;
    var enhancementsDisabled = false;
    var backend = new EqualizerApoService(install, configDirectory, (_, _) => (registryState, enhancementsDisabled));
    Check(!backend.Probe(profile).IsInstalled, "missing component not ready");
    Check(!(await backend.ApplyAsync(profile)).ConfigurationWritten, "missing component not falsely written");
    File.WriteAllText(Path.Combine(install, "EqualizerAPO.dll"), "test placeholder");
    File.WriteAllText(Path.Combine(install, "DeviceSelector.exe"), "test placeholder");
    var mainPath = Path.Combine(configDirectory, "config.txt");
    var originalBytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("# User 音效\r\nPreamp: -3 dB\r\nInclude: custom.txt")).ToArray();
    File.WriteAllBytes(mainPath, originalBytes);
    var staged = await backend.ApplyAsync(profile);
    Check(staged.IsInstalled && staged.ConfigurationWritten && !staged.CanApply && !staged.IsIncludeConnected, "staging clearly differs from applying");
    Check(backend.Probe(profile).ConfigurationWritten, "read-only probe retains staged current configuration");
    Check(File.ReadAllBytes(mainPath).AsSpan().SequenceEqual(originalBytes), "ordinary apply leaves user config untouched");
    var connected = await backend.ConnectAsync(profile);
    Check(connected.ConfigurationWritten && connected.CanApply && connected.IsIncludeConnected, "explicit connection ready only with registration");
    Check(backend.Probe(profile).ConfigurationWritten && backend.Probe(profile).CanApply,
        "refresh probe retains exact current curve after successful write");
    var changedProfile = backend.Probe(profile with { PreampDb = -4 });
    Check(changedProfile.CanApply && !changedProfile.ConfigurationWritten && changedProfile.Message.Contains("尚未写入"),
        "hardware remains ready but profile change is not falsely marked written");
    var joined = File.ReadAllBytes(mainPath);
    Check(joined.AsSpan(0, originalBytes.Length).SequenceEqual(originalBytes), "append preserves original bytes and BOM");
    Check(File.ReadAllBytes(mainPath + ".halo-backup").AsSpan().SequenceEqual(originalBytes), "original config backup");
    var connectedAgain = await backend.ConnectAsync(profile);
    Check(connectedAgain.CanApply && File.ReadAllBytes(mainPath).AsSpan().SequenceEqual(joined), "connection idempotent");
    File.WriteAllText(mainPath, "If: sampleRate == 999999\n" + File.ReadAllText(mainPath) + "EndIf:\n");
    Check(!backend.Probe(profile).CanApply, "conditional managed block not falsely ready");
    File.WriteAllBytes(mainPath, joined);
    File.WriteAllText(mainPath, File.ReadAllText(mainPath).Replace("Channel: all", "Channel: R", StringComparison.Ordinal));
    Check(!backend.Probe(profile).CanApply && !(await backend.ConnectAsync(profile)).CanApply, "modified isolation block not silently overwritten");
    File.WriteAllBytes(mainPath, joined);
    registryState = AudioEndpointRegistration.NotRegistered;
    Check(!backend.Probe(profile).CanApply && backend.Probe(profile).Message.Contains("尚未接入"), "file presence cannot replace endpoint registration");
    registryState = AudioEndpointRegistration.Unknown;
    Check(!backend.Probe(profile).CanApply, "unknown registration not ready");
    registryState = AudioEndpointRegistration.Registered;
    enhancementsDisabled = true;
    Check(!backend.Probe(profile).CanApply && backend.Probe(profile).EnhancementsDisabled, "disabled enhancements prevent ready");
    enhancementsDisabled = false;
    File.WriteAllText(connected.ManagedConfigPath, "Preamp: 8 dB # owned by somebody else");
    Check(!backend.Probe(profile).ConfigurationWritten, "unknown third-party config never marked as matching current curve");
    var collision = await backend.ApplyAsync(profile);
    Check(!collision.ConfigurationWritten && File.ReadAllText(connected.ManagedConfigPath).Contains("somebody else"), "unowned managed file never overwritten");
    File.Delete(connected.ManagedConfigPath);
    File.WriteAllText(mainPath, "Include: HaloPixelToolBox/AudioControl.txt\n");
    var manualCollision = await backend.ConnectAsync(profile);
    Check(!manualCollision.CanApply && File.ReadAllText(mainPath).Count(ch => ch == '\n') == 1, "manual include not duplicated");
    File.WriteAllText(mainPath, "If: sampleRate == 48000\nPreamp: -3 dB\n");
    Check(!(await backend.ConnectAsync(profile)).CanApply, "unclosed conditional not blindly appended");
    File.WriteAllBytes(mainPath, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# UTF16 user\r\nPreamp: -6 dB\r\n")).ToArray());
    Check((await backend.ConnectAsync(profile)).CanApply && File.ReadAllText(mainPath).Contains("Include: HaloPixelToolBox/AudioControl.txt"), "UTF16 preserved and connected");
    var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await backend.ApplyAsync(profile, cancellationToken: cancelled.Token); throw new Exception("cancellation not observed"); }
    catch (OperationCanceledException) { passed++; }
    var beforeRapid = File.ReadAllText(connected.ManagedConfigPath);
    var held = new FileStream(connected.ManagedConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
    var retryTask = backend.ApplyAsync(profile with { PreampDb = -6 });
    await Task.Delay(90);
    held.Dispose();
    Check((await retryTask).ConfigurationWritten, "transient sharing lock recovers");
    Check(File.ReadAllText(connected.ManagedConfigPath).Contains("Preamp: -6 dB"), "retry publishes complete new configuration");
    Check(File.ReadAllText(connected.ManagedConfigPath + ".halo-backup") == beforeRapid, "managed backup contains last complete config");

    var storePath = Path.Combine(sandbox, "profiles", "audio.json");
    var store = new AudioProfileStore(storePath);
    var defaults = store.Load();
    Check(defaults.Presets.Count == 3 && !defaults.CurrentProfile.Enabled, "safe disabled default with presets");
    Check(store.Save(defaults with { CurrentProfile = profile }), "valid profile saved");
    Check(store.Load().CurrentProfile.EndpointId == endpoint, "device preference round trip");
    Check(store.Save(defaults with { CurrentProfile = profile with { PreampDb = -4 } }), "second saved profile backed up");
    File.WriteAllText(storePath, "{corrupt json}");
    Check(store.Load().CurrentProfile.PreampDb == 0 && store.LastError is not null, "corrupt primary falls back to last-good backup");
    Check(store.Save(defaults with { CurrentProfile = profile with { PreampDb = -5 } }), "explicit save recovers corrupt primary");
    Check(Directory.GetFiles(Path.GetDirectoryName(storePath)!, "audio.json.invalid-*").Length == 1, "corrupt bytes quarantined");
    Check(!store.Save(defaults with { Presets = [defaults.Presets[0], defaults.Presets[0]] }), "duplicate preset rejected");
    Check(!store.Save(defaults with { CurrentProfile = profile with { Balance = 101 } }), "store rejects invalid parameters");
    File.WriteAllText(storePath, "{\"schemaVersion\":99}");
    Check(!store.Save(defaults) && File.ReadAllText(storePath).Contains("99"), "future schema remains untouched");
    Check(!Directory.GetFiles(sandbox, ".halo-audio-*.tmp", SearchOption.AllDirectories).Any(), "no abandoned temporary write files");
}
finally
{
    // The absolute test root was created above and contains only disposable test data.
    if (!Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new Exception("unexpected test directory");
    Directory.Delete(sandbox, recursive: true);
}
Console.WriteLine($"Audio control: {passed} checks passed; all writes isolated in a temporary directory.");

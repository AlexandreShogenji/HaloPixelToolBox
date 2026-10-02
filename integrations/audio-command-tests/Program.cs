using System.Text;
using System.Text.Json;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services;
using HaloPixelToolBox.Services.Audio;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(10)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + " -> " + error.GetType().Name + ": " + error.Message); }
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
string Profile(AudioControlProfile profile) => JsonSerializer.Serialize(profile);

foreach (var json in new[]
{
    "{}", "[]", "null", "{\"unknown\":1}", "{\"enabled\":\"true\"}",
    "{\"enabled\":null}", "{\"preset\":null}", "{\"autoHeadroom\":null}",
    "{\"enabled\":null,\"preset\":null,\"autoHeadroom\":null}",
    "{\"preampDb\":31}", "{\"preampDb\":\"3\"}", "{\"preampDb\":null}",
    "{\"balance\":-101}", "{\"balance\":\"NaN\"}",
    "{\"bandGainsDb\":[1,2]}", "{\"bandGainsDb\":[0,0,0,0,0,0,0,0,0,13]}",
    "{\"bandGainsDb\":[0,0,0,0,0,0,0,0,0,\"1\"]}",
    "{\"group\":\"\",\"mode\":\"set\",\"value\":1}", "{\"group\":\"bass\"}",
    "{\"mode\":\"increase\",\"value\":2}", "{\"group\":\"bass\",\"value\":2}",
    "{\"group\":\"bass\",\"mode\":\"increase\",\"value\":0}",
    "{\"group\":\"mid\",\"mode\":\"decrease\",\"value\":-1}",
    "{\"group\":\"wrong\",\"mode\":\"set\",\"value\":1}",
    "{\"group\":\"bass\",\"mode\":\"unknown\",\"value\":1}",
    "{\"preset\":\"不存在的曲线预设\"}",
    "{\"preset\":\"voice\",\"preampDb\":2}", "{\"preset\":\"voice\",\"balance\":1}",
    "{\"preset\":\"voice\",\"autoHeadroom\":true}",
    "{\"preset\":\"voice\",\"group\":\"bass\",\"mode\":\"set\",\"value\":2}",
    "{\"group\":\"bass\",\"mode\":\"set\",\"value\":2,\"bandGainsDb\":[0,0,0,0,0,0,0,0,0,0]}"
})
{
    await Test("invalid audio payload is atomic: " + json, async () =>
    {
        using var fixture = new AudioCase();
        var before = Profile(fixture.Service.Current.Profile);
        var bytes = File.ReadAllBytes(fixture.Store.FilePath);
        var revision = fixture.Service.Current.Revision;
        await Throws<ArgumentException>(() => fixture.Pipe.ConfigureForTestAsync(json));
        Check(Profile(fixture.Service.Current.Profile) == before, "Invalid parameters changed current profile");
        Check(File.ReadAllBytes(fixture.Store.FilePath).SequenceEqual(bytes), "Invalid parameters changed persisted profile");
        Check(fixture.Service.Current.Revision == revision && !File.Exists(fixture.ManagedPath), "Invalid parameters published or wrote APO filters");
    });
}

await Test("relative adjustments clamp only requested frequency group", async () =>
{
    using var fixture = new AudioCase();
    await fixture.Service.UpdateProfileAsync(fixture.Service.Current.Profile with { BandGainsDb = [11, 12, -11, 3, 4, 5, 6, 7, 8, 9] });
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"group\":\"bass\",\"mode\":\"increase\",\"value\":3}");
    Check(result.Data!.Profile.BandGainsDb.SequenceEqual(new double[] { 12, 12, -8, 3, 4, 5, 6, 7, 8, 9 }), "Bass clamp modified unrelated frequencies");
    await fixture.Pipe.ConfigureForTestAsync("{\"group\":\"treble\",\"mode\":\"decrease\",\"value\":12}");
    await fixture.Pipe.ConfigureForTestAsync("{\"group\":\"treble\",\"mode\":\"decrease\",\"value\":12}");
    Check(fixture.Service.Current.Profile.BandGainsDb.Skip(7).All(gain => gain == -12), "Treble decrease did not clamp");
});

await Test("preset changes preserve selected endpoint and enabled flag", async () =>
{
    using var fixture = new AudioCase();
    await fixture.Service.UpdateProfileAsync(fixture.Service.Current.Profile with { Enabled = false, EndpointId = AudioCase.SecondId, PreampDb = 3 });
    var outputChanges = fixture.Endpoints.ChangeCount;
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"preset\":\"人聲清晰\"}");
    Check(result.Data!.Profile.EndpointId == AudioCase.SecondId && !result.Data.Profile.Enabled, "Preset changed routing or forced effect power");
    Check(result.Data.Profile.BandGainsDb.SequenceEqual(new double[] { -2, -1, 0, 0, 1, 2, 3, 2, 1, 0 }), "Traditional Chinese preset lookup failed");
    Check(fixture.Endpoints.ChangeCount == outputChanges, "Preset called Windows output setter");
    await fixture.Service.ApplyPresetAsync("flat");
    Check(fixture.Service.Current.Profile.EndpointId == AudioCase.SecondId && !fixture.Service.Current.Profile.Enabled, "Direct preset route changed output or power");
});

await Test("explicit enabled override can accompany preset", async () =>
{
    using var fixture = new AudioCase();
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"preset\":\"bass\",\"enabled\":true}");
    Check(result.Data!.Profile.Enabled && result.Data.Profile.EndpointId == AudioCase.FirstId, "Explicit effect power not applied");
});

await Test("missing backend reports not_confirmed while saving curve", async () =>
{
    using var fixture = new AudioCase(installed: false);
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"preampDb\":-4,\"enabled\":true}");
    Check(!result.Success && result.Status == DeviceCommandStatus.NotConfirmed && !result.Data!.Backend.IsInstalled, "Missing component falsely reported audible success");
    Check(fixture.Store.Load().CurrentProfile.PreampDb == -4 && fixture.Store.Load().CurrentProfile.Enabled, "Missing component discarded saved settings");
    Check(!File.Exists(fixture.ManagedPath), "Missing component created APO config");
});

await Test("connected backend writes actual filters and reports confirmed configuration", async () =>
{
    using var fixture = new AudioCase();
    await fixture.Service.InitializeBackendAsync();
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"enabled\":true,\"preampDb\":-5}");
    Check(result.Success && result.Data!.Backend.CanApply && result.Data.Backend.ConfigurationWritten, "Installed registered connected backend not recognized");
    Check(File.ReadAllText(fixture.ManagedPath).Contains("Preamp: -5 dB"), "Coordinator did not write requested APO preamp");
});

await Test("store failure keeps old current and avoids APO writes", async () =>
{
    using var fixture = new AudioCase();
    var before = Profile(fixture.Service.Current.Profile);
    File.Delete(fixture.Store.FilePath);
    Directory.CreateDirectory(fixture.Store.FilePath);
    var result = await fixture.Pipe.ConfigureForTestAsync("{\"preampDb\":-8}");
    Check(!result.Success && !result.Data!.OperationSucceeded, "Store failure reported success");
    Check(Profile(fixture.Service.Current.Profile) == before && !File.Exists(fixture.ManagedPath), "Store failure committed current or wrote effects");
});

await Test("Changed publishes increasing revisions and isolates failing subscribers", async () =>
{
    using var fixture = new AudioCase();
    var seen = new List<AudioControlSnapshot>();
    fixture.Service.Changed += _ => throw new InvalidOperationException("test subscriber");
    fixture.Service.Changed += snapshot => seen.Add(snapshot);
    var first = await fixture.Service.RefreshAsync();
    var second = await fixture.Pipe.ConfigureForTestAsync("{\"balance\":20}");
    Check(seen.Count == 2 && seen[0].Revision == first.Revision && seen[1].Revision == second.Data!.Revision && seen[1].Revision == seen[0].Revision + 1, "Changed revisions missing or duplicated");
    Check(ReferenceEquals(fixture.Service.Current, seen[1]), "Published snapshot was not current");
});

await Test("offline endpoint refuses profile mutation", async () =>
{
    using var fixture = new AudioCase();
    var before = Profile(fixture.Service.Current.Profile);
    await Throws<ArgumentException>(() => fixture.Service.UpdateProfileAsync(fixture.Service.Current.Profile with { EndpointId = "{22222222-2222-2222-2222-222222222222}" }));
    Check(Profile(fixture.Service.Current.Profile) == before && !File.Exists(fixture.ManagedPath), "Offline target changed settings");
});

await Test("ambiguous and unknown output references do not switch", async () =>
{
    using var fixture = new AudioCase();
    var ambiguous = await fixture.Pipe.OutputForTestAsync("USB");
    var missing = await fixture.Pipe.OutputForTestAsync("不存在的蓝牙设备");
    Check(ambiguous.Status == DeviceCommandStatus.Conflict && missing.Status == DeviceCommandStatus.NotFound, "Unsafe output lookup not rejected");
    Check(fixture.Endpoints.ChangeCount == 0, "Rejected reference invoked output change");
});

await Test("exact output ID changes only Windows route, not EQ target", async () =>
{
    using var fixture = new AudioCase();
    var result = await fixture.Pipe.OutputForTestAsync(AudioCase.SecondId);
    Check(result.Success && fixture.Endpoints.ChangeCount == 1 && fixture.Endpoints.LastChangedId == AudioCase.SecondId, "Exact endpoint not switched once");
    Check(fixture.Service.Current.Profile.EndpointId == AudioCase.FirstId && fixture.Service.Current.Endpoints.Single(endpoint => endpoint.IsDefault).Id == AudioCase.SecondId, "Output switching changed EQ edit target or failed to refresh defaults");
});

await Test("Windows output rejection remains unconfirmed", async () =>
{
    using var fixture = new AudioCase();
    fixture.Endpoints.AllowChange = false;
    var result = await fixture.Pipe.OutputForTestAsync(AudioCase.SecondId);
    Check(!result.Success && result.Status == DeviceCommandStatus.NotConfirmed && fixture.Service.Current.Endpoints.Single(endpoint => endpoint.IsDefault).Id == AudioCase.FirstId, "Rejected switch falsely changed defaults");
});

await Test("caller curve mutation cannot modify persisted document", async () =>
{
    using var fixture = new AudioCase();
    var profile = fixture.Service.Current.Profile.Copy() with { PreampDb = -2 };
    await fixture.Service.UpdateProfileAsync(profile);
    profile.BandGainsDb[0] = 12;
    await fixture.Service.RefreshAsync();
    Check(fixture.Service.Current.Profile.BandGainsDb[0] == 0 && fixture.Store.Load().CurrentProfile.BandGainsDb[0] == 0, "Caller array alias corrupted document");
});

foreach (var profile in new AudioControlProfile[]
{
    new() { PreampDb = double.NaN }, new() { Balance = double.PositiveInfinity },
    new() { BandGainsDb = [double.NegativeInfinity, 0, 0, 0, 0, 0, 0, 0, 0, 0] }
})
{
    await Test("non-finite direct profile values are rejected before saving", async () =>
    {
        using var fixture = new AudioCase();
        var before = Profile(fixture.Service.Current.Profile);
        await Throws<ArgumentException>(() => fixture.Service.UpdateProfileAsync(profile with { EndpointId = AudioCase.FirstId }));
        Check(Profile(fixture.Service.Current.Profile) == before && !File.Exists(fixture.ManagedPath), "Non-finite value committed or wrote filters");
    });
}

await Test("manual preset save updates matching name and protects built-in flat", async () =>
{
    using var fixture = new AudioCase();
    await fixture.Service.SavePresetAsync("我的曲线");
    var original = fixture.Service.Current.Presets.Single(preset => preset.Name == "我的曲线");
    await fixture.Service.UpdateProfileAsync(fixture.Service.Current.Profile with { PreampDb = -7 });
    await fixture.Service.SavePresetAsync("我的曲线");
    var updated = fixture.Service.Current.Presets.Single(preset => preset.Name == "我的曲线");
    Check(updated.Id == original.Id && updated.Profile.PreampDb == -7, "Save duplicated preset or failed to update curve");
    var revision = fixture.Service.Current.Revision;
    await Throws<ArgumentException>(() => fixture.Service.DeletePresetAsync("flat"));
    Check(fixture.Service.Current.Revision == revision && fixture.Service.Current.Presets.Any(preset => preset.Id == "flat"), "Built-in flat was removed");
    await fixture.Service.DeletePresetAsync(updated.Id);
    Check(!fixture.Service.Current.Presets.Any(preset => preset.Id == updated.Id), "Custom preset not removed");
});

foreach (var json in new[] { "{}", "null", "{\"device\":null}", "{\"device\":\"\"}", "{\"device\":\"USB\",\"enabled\":true}" })
{
    await Test("output schema rejects malformed or extra parameters: " + json, async () =>
    {
        using var fixture = new AudioCase();
        await Throws<ArgumentException>(() => fixture.Pipe.OutputJsonForTestAsync(json));
        Check(fixture.Endpoints.ChangeCount == 0, "Invalid output schema changed default route");
    });
}

await Test("status read failure is not reported as successful", async () =>
{
    using var fixture = new AudioCase();
    fixture.Endpoints.ReadException = new IOException("isolated endpoint read failure");
    var result = await fixture.Pipe.StatusForTestAsync();
    Check(!result.Success && result.Status == DeviceCommandStatus.NotConfirmed && result.Message.Contains("读取音频设备失败"), "Read error falsely returned successful status");
});

await Test("cancellation keeps current snapshot consistent with any persisted profile", async () =>
{
    using var fixture = new AudioCase();
    using var cancellation = new CancellationTokenSource();
    fixture.Endpoints.AfterRead = () => cancellation.Cancel();
    await Throws<OperationCanceledException>(() => fixture.Service.UpdateProfileAsync(
        fixture.Service.Current.Profile with { PreampDb = -6 }, cancellation.Token));
    Check(Profile(fixture.Service.Current.Profile) == Profile(fixture.Store.Load().CurrentProfile), "Cancellation left current snapshot older than persisted profile");
    Check(!File.Exists(fixture.ManagedPath), "Cancelled operation unexpectedly wrote APO filters");
});

await Test("cancellation after profile save publishes the saved unapplied settings", async () =>
{
    using var fixture = new AudioCase();
    using var cancellation = new CancellationTokenSource();
    var gateField = typeof(EqualizerApoService).GetField("writeGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var backendGate = (SemaphoreSlim)gateField.GetValue(fixture.Backend)!;
    await backendGate.WaitAsync();
    try
    {
        var operation = fixture.Service.UpdateProfileAsync(fixture.Service.Current.Profile with { PreampDb = -9 }, cancellation.Token);
        Check(fixture.Store.Load().CurrentProfile.PreampDb == -9 && !operation.IsCompleted, "Fixture did not cancel after persistence and before APO write");
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => operation);
        Check(fixture.Service.Current.Profile.PreampDb == -9 && !fixture.Service.Current.OperationSucceeded, "Committed profile cancellation did not publish saved unapplied snapshot");
        Check(!File.Exists(fixture.ManagedPath), "Backend cancellation wrote filters");
    }
    finally { backendGate.Release(); }
});

Console.WriteLine($"Audio command contracts: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class AudioCase : IDisposable
{
    public const string FirstId = "{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}";
    public const string SecondId = "{0.0.0.00000000}.{33333333-3333-3333-3333-333333333333}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "halo-audio-contracts-" + Guid.NewGuid().ToString("N"));
    public AudioProfileStore Store { get; }
    public EqualizerApoService Backend { get; }
    public WindowsAudioEndpointService Endpoints { get; }
    public AudioControlService Service { get; }
    public DeviceControlPipeServer Pipe { get; }
    public string ManagedPath { get; }

    public AudioCase(bool installed = true)
    {
        Directory.CreateDirectory(root);
        var install = Path.Combine(root, "EqualizerAPO");
        var config = Path.Combine(install, "config");
        Directory.CreateDirectory(config);
        if (installed)
        {
            File.WriteAllText(Path.Combine(install, "EqualizerAPO.dll"), "isolated test placeholder", Encoding.UTF8);
            File.WriteAllText(Path.Combine(install, "DeviceSelector.exe"), "isolated test placeholder", Encoding.UTF8);
        }
        File.WriteAllText(Path.Combine(config, "config.txt"), "# existing user filters\n", Encoding.UTF8);
        ManagedPath = Path.Combine(config, "HaloPixelToolBox", "AudioControl.txt");
        Store = new(Path.Combine(root, "profiles.json"));
        var document = AudioProfileStore.CreateDefaultDocument();
        if (!Store.Save(document with { CurrentProfile = document.CurrentProfile with { EndpointId = FirstId } }))
            throw new IOException("Cannot write test fixture: " + Store.LastError?.Message);
        Backend = new(install, config, (_, _) => (AudioEndpointRegistration.Registered, false));
        Endpoints = new() { Items = [new(FirstId, "扬声器 USB 花再", true, true), new(SecondId, "耳机 USB Monitor")] };
        Service = new(Store, Backend, Endpoints);
        Pipe = new(Service);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(root);
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("halo-audio-contracts-", StringComparison.Ordinal))
            throw new IOException("Invalid test cleanup path");
        Directory.Delete(absolute, true);
    }
}

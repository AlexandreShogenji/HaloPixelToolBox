using System.Text.Json;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Services.DeviceControl;
using HaloPixelToolBox.Core.Services.Lighting;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(10)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Expected invalid categorical parameters");
}
JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

await Test("missing standalone target defaults random without touching unrelated settings", () =>
{
    Check(DeviceSettingCommandDefaults.ReadReference(Json("{}"), "name") == "随机", "Preset default missing");
    Check(DeviceSettingCommandDefaults.ReadReference(Json("{}"), "scene") == "随机", "Scene default missing");
    Check(DeviceSettingCommandDefaults.ReadReference(Json("{}"), "category") == "随机", "Category default missing");
    Check(DeviceSettingCommandDefaults.ReadAmbientEffect(Json("{}")) == ("random", null), "Effect default missing");
    Check(DeviceSettingCommandDefaults.ReadAmbientEffect(Json("{\"effect\":\"潮汐\"}")) == ("set", "潮汐"), "Explicit effect ignored");
    Check(DeviceSettingCommandDefaults.ReadAmbientEffect(Json("{\"mode\":\"next\"}")) == ("next", null), "Explicit next ignored");
    return Task.CompletedTask;
});

foreach (var invalid in new[] { "null", "[]", "{\"name\":null}", "{\"name\":\" \"}", "{\"name\":1}" })
    await Test("explicit invalid target never randomizes: " + invalid, () =>
    {
        Throws(() => DeviceSettingCommandDefaults.ReadReference(Json(invalid), "name"));
        return Task.CompletedTask;
    });
foreach (var invalid in new[] { "null", "[]", "{\"mode\":null}", "{\"mode\":\"\"}", "{\"effect\":null}", "{\"effect\":\"\"}", "{\"mode\":\"set\"}", "{\"mode\":\"random\",\"effect\":\"呼吸\"}" })
    await Test("invalid explicit effect parameters reject: " + invalid, () =>
    {
        Throws(() => DeviceSettingCommandDefaults.ReadAmbientEffect(Json(invalid)));
        return Task.CompletedTask;
    });

var entries = new[] { new DeviceLookupCandidate<int>(1, "贺喜 遥香"), new(2, "一之濑 美空"), new(3, "五百城 茉央") };
foreach (var reference in new[] { "随机", "隨機", "random", "任意" })
    await Test("random never repeats a current item: " + reference, () =>
    {
        foreach (var seed in Enumerable.Range(0, 30))
        {
            var result = DeviceSettingReferenceResolver.Resolve(reference, entries, item => item is 1 or 2, random: new Random(seed));
            Check(result.IsResolved && result.Value == 3, "Selected an excluded item");
        }
        return Task.CompletedTask;
    });
await Test("all-current and singleton catalogs safely remain selectable", () =>
{
    Check(DeviceSettingReferenceResolver.Resolve("随机", entries[..1], _ => true, random: new Random(0)).Value == 1, "Singleton failed");
    Check(DeviceSettingReferenceResolver.Resolve("随机", entries, _ => true, random: new Random(0)).IsResolved, "All-identical catalog failed");
    Check(!DeviceSettingReferenceResolver.Resolve("随机", Array.Empty<DeviceLookupCandidate<int>>()).IsResolved, "Empty catalog falsely succeeded");
    return Task.CompletedTask;
});
foreach (var reference in new[] { "第三项", "第3套", "第三個", "3", "最后一个", "第三套配色方案" })
    await Test("explicit ordinal is honored: " + reference, () =>
    {
        Check(DeviceSettingReferenceResolver.Resolve(reference, entries, _ => true, ["配色", "方案"]).Value == 3, "Ordinal changed to random/name");
        return Task.CompletedTask;
    });
foreach (var reference in new[] { "0", "第四项", "-1", "１．５", "1.5", "+1", "配色方案 -1", "场景 -1", "配色方案 1.5", "", "一个完全不存在的名字", "随机星光不存在", "unknown-random-name" })
    await Test("invalid explicit selection never falls back to random: " + reference, () =>
    {
        Check(!DeviceSettingReferenceResolver.Resolve(reference, entries).IsResolved, "Invalid selection became a valid item");
        return Task.CompletedTask;
    });
await Test("malformed ordinal cannot match a numeric preset name", () =>
{
    var numericNames = new[] { new DeviceLookupCandidate<int>(1, "1"), new(2, "15") };
    foreach (var invalid in new[] { "-1", "配色方案 -1", "1.5", "配色方案 1.5" })
        Check(!DeviceSettingReferenceResolver.Resolve(invalid, numericNames, stopWords: ["配色", "方案"]).IsResolved, "Numeric named preset bypassed invalid ordinal check");
    var rawNames = new[] { new DeviceLookupCandidate<int>(1, "-1"), new(2, "Ocean-1") };
    Check(DeviceSettingReferenceResolver.Resolve("-1", rawNames).Value == 1, "Raw explicit name lost");
    Check(DeviceSettingReferenceResolver.Resolve("Ocean-1", rawNames).Value == 2, "Hyphenated name lost");
    return Task.CompletedTask;
});
await Test("explicit names, traditional and partial names retain their meaning", () =>
{
    Check(DeviceSettingReferenceResolver.Resolve("賀喜 遙香", entries).Value == 1, "Traditional name failed");
    Check(DeviceSettingReferenceResolver.Resolve("美空", entries).Value == 2, "Partial name failed");
    var namedRandom = new[] { new DeviceLookupCandidate<int>(1, "随机星光"), new(2, "暖光") };
    Check(DeviceSettingReferenceResolver.Resolve("随机星光", namedRandom, _ => true).Value == 1, "Explicit name containing random randomized");
    var ambiguous = new[] { new DeviceLookupCandidate<int>(1, "暖光甲"), new(2, "暖光乙") };
    Check(DeviceSettingReferenceResolver.Resolve("暖光", ambiguous).IsAmbiguous, "Ambiguous explicit name randomized");
    return Task.CompletedTask;
});
await Test("random effect differs while explicit next and previous retain order", () =>
{
    foreach (var current in Enumerable.Range(1, 6).Select(value => (AmbientLightEffect)value))
    {
        Check(AmbientLightEffectResolver.Select(new("random", null), current) != current, "Effect repeated");
        var next = AmbientLightEffectResolver.Select(new("next", null), current);
        Check(AmbientLightEffectResolver.Select(new("previous", null), next) == current, "Next/previous order changed");
    }
    return Task.CompletedTask;
});

LightingColorPreset Preset(string name, int value) => new()
{
    Name = name, AmbientRed = value, AmbientGreen = value, AmbientBlue = value,
    PixelRed = value, PixelGreen = value, PixelBlue = value
};
void Reset()
{
    DisplayFeatureProfile.AmbientLightRed = DisplayFeatureProfile.AmbientLightGreen = DisplayFeatureProfile.AmbientLightBlue = 0;
    DisplayFeatureProfile.PixelScreenRed = DisplayFeatureProfile.PixelScreenGreen = DisplayFeatureProfile.PixelScreenBlue = 0;
    HaloPixelLightingService.BeforeAmbientWrite = null;
    HaloPixelLightingService.WriteSucceeds = true;
    HaloPixelLightingService.AmbientWrites.Clear();
    HaloPixelLightingService.PixelWrites.Clear();
}
await Test("random presets exclude duplicate current color pairs under the write gate", async () =>
{
    Reset();
    var presets = new[] { Preset("甲", 0), Preset("同色甲", 0), Preset("乙", 20) };
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    HaloPixelLightingService.BeforeAmbientWrite = async () => { entered.TrySetResult(); await release.Task; };
    var coordinator = new LightingControlCoordinator();
    var first = coordinator.ApplyPresetReferenceAsync("随机", presets);
    await entered.Task;
    var second = coordinator.ApplyPresetReferenceAsync("随机", presets);
    Check(!second.IsCompleted, "Second random selection escaped mutation gate");
    release.SetResult();
    Check((await first).Success && (await second).Success, "Preset application failed");
    Check(HaloPixelLightingService.AmbientWrites.SequenceEqual(new[] { new HaloPixelColor(20, 20, 20), new HaloPixelColor(0, 0, 0) }), "Sequential random selections repeated current pair");
});
await Test("failed writes do not mark a random preset as applied", async () =>
{
    Reset();
    HaloPixelLightingService.WriteSucceeds = false;
    var result = await new LightingControlCoordinator().ApplyPresetReferenceAsync("随机", new[] { Preset("甲", 0), Preset("乙", 20) });
    Check(!result.Success && DisplayFeatureProfile.AmbientLightRed == 0 && DisplayFeatureProfile.PixelScreenRed == 0, "Failed writes changed current pair");
});
await Test("explicit preset ordinal can reapply current colors; bad names write nothing", async () =>
{
    Reset();
    var coordinator = new LightingControlCoordinator();
    var presets = new[] { Preset("贺喜 遥香", 0), Preset("一之濑 美空", 20) };
    Check((await coordinator.ApplyPresetReferenceAsync("第一项", presets)).Success, "Explicit current ordinal was rejected");
    var writes = HaloPixelLightingService.AmbientWrites.Count;
    Check(!(await coordinator.ApplyPresetReferenceAsync("不存在的紫金色", presets)).Success, "Unknown name randomized");
    Check(HaloPixelLightingService.AmbientWrites.Count == writes, "Unknown name wrote hardware");
});
Console.WriteLine($"Device settings: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

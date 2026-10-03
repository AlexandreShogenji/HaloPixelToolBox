using System.Text.Json;
using System.Text.Json.Serialization;
using HaloPixelToolBox.Core.Models.DeviceControl;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Scenes;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Services.Scenes;
using HaloPixelToolBox.Core.Utilities;

internal static class DisplayStateProbe
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var restore = new PersonalSceneRestoreService();
        check(restore.GetCurrentScene().Name == "默认时钟场景", "The restore fallback remains available before any send.");
        check(restore.GetLastRememberedScene() is null, "The fallback must not be reported as a successfully sent scene.");
        var scene = new PersonalSceneDefinition
        {
            Id = "test-clock", Name = "时钟类 03", Category = PersonalSceneCategory.Clock,
            CategoryIndex = 0, SceneIndex = 2, ScreenSettingParameters = [1, 0, 2, 255]
        };
        _ = HaloPixelDisplayService.CreateScenePreviewSnapshot(scene);
        check(restore.GetLastRememberedScene() is null, "Creating a UI preview must not remember a device send.");

        var offline = new HaloPixelDisplayService(new HaloPixelDevice { CurrentDevice = null, InitializeResult = false });
        check(!await offline.ShowScreenSceneAsync(1, 0, 2, 255, scene), "An offline scene write must fail.");
        check(restore.GetLastRememberedScene() is null, "A failed scene write must not replace the empty send history.");
        check(!await restore.RestoreAsync(offline) && restore.GetLastRememberedScene() is null,
            "A failed fallback restoration must not invent a remembered scene.");

        var display = new HaloPixelDisplayService(new HaloPixelDevice());
        check(await display.ShowScreenSceneAsync(1, 0, 2, 255, scene), "A connected scene write failed.");
        check(HaloPixelDisplayService.LastContentSent?.ContentKind == DisplayContentKind.Scene
            && HaloPixelDisplayService.LastContentSent.SceneName == "时钟类 03",
            "A completed scene write must publish its own content kind and scene name.");
        check(new PersonalSceneRestoreService().GetLastRememberedScene()?.Name == "时钟类 03",
            "The successful scene record must be shared across service instances.");
        scene.Name = "mutated caller";
        scene.ScreenSettingParameters[2] = 9;
        var remembered = restore.GetLastRememberedScene()!;
        check(remembered.Name == "时钟类 03" && remembered.ScreenSettingParameters![2] == 2,
            "Caller mutations must not rewrite successful scene history.");
        remembered.Name = "mutated snapshot";
        remembered.ScreenSettingParameters![2] = 8;
        check(restore.GetLastRememberedScene()?.Name == "时钟类 03"
            && restore.GetLastRememberedScene()?.ScreenSettingParameters![2] == 2,
            "Returned scene snapshots must not mutate the shared restore state.");

        check(await display.SendTextAsync(new()
        {
            Text = "任务已完成", Source = DisplayContentKind.TaskStatus,
            ExpectedForegroundRevision = HaloPixelDisplayService.ForegroundRevision
        }), "A permitted temporary task status write failed.");
        check(HaloPixelDisplayService.LastContentSent?.ContentKind == DisplayContentKind.TaskStatus
            && HaloPixelDisplayService.LastContentSent.SceneName is null,
            "Temporary status must honestly describe the latest send as text, not a scene.");
        check(restore.GetLastRememberedScene()?.Name == "时钟类 03",
            "A task status must preserve the distinct record of the last successfully sent personal scene.");
        check(!await offline.ShowScreenSceneAsync(1, 0, 9, 255, scene)
            && restore.GetLastRememberedScene()?.Name == "时钟类 03",
            "A later failed activation must retain the earlier successful scene record.");

        // Verify the added wire metadata without implying these cached fields
        // can be read back from the physical display or survive process restart.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var status = new HaloPixelDeviceStatus(false, null, null, null, null, null, null, DateTimeOffset.Now)
        {
            DisplayState = new(HaloPixelDisplayService.LastContentSent?.ContentKind, restore.GetLastRememberedScene()?.Name)
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, options));
        var state = json.RootElement.GetProperty("displayState");
        check(json.RootElement.GetProperty("activeSceneName").ValueKind == JsonValueKind.Null,
            "The compatible activeSceneName remains nullable after a text send.");
        check(state.GetProperty("lastSentContentKind").GetString() == "taskStatus"
            && state.GetProperty("lastSentPersonalSceneName").GetString() == "时钟类 03",
            "Wire data must distinguish the most recent display content from personal-scene history.");
        check(state.GetProperty("source").GetString() == "application_last_successful_send"
            && !state.GetProperty("sceneReadbackSupported").GetBoolean(),
            "Cached scene information must explicitly deny hardware-readback provenance, including when disconnected.");
    }
}

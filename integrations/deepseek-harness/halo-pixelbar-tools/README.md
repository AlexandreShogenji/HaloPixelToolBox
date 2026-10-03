# Halo PixelBar tools for DeepSeek Harness

This bundle exposes constrained PixelBar tools to DeepSeek Harness:

- `get_pixelbar_status`
- `get_pixelbar_audio_status`
- `configure_pixelbar_audio`
- `set_pixelbar_audio_output`
- `set_pixelbar_ambient_light`
- `set_pixelbar_light_effect`
- `set_pixelbar_light_speed`
- `set_pixelbar_screen`
- `set_pixelbar_volume`
- `show_pixelbar_time`
- `start_pixelbar_spotify_lyrics`
- `get_pixelbar_lyrics_status`
- `set_pixelbar_lyrics_offset`
- `stop_pixelbar_lyrics`
- `configure_pixelbar_lyrics`
- `show_pixelbar_subtitle`
- `activate_pixelbar_scene`
- `restore_pixelbar_default_scene`
- `get_pixelbar_catalog`
- `apply_pixelbar_lighting_preset`
- `activate_pixelbar_scene_by_position`
- `activate_pixelbar_scene_by_reference`
- `get_pixelbar_lighting_automation`
- `configure_pixelbar_lighting_automation`
- `configure_pixelbar`

HaloPixelToolBox owns the USB HID connection. The plugin only connects to the
current Windows user's `HaloPixelToolBox.DeviceControl.v1` named pipe, so DSH
never receives raw HID access.

### Random replacements by default (0.7.3)

“换个配色 / 换个颜色方案 / 换个氛围灯效 / 换个场景” without a target
selects randomly, without asking the user to choose. A named category remains
binding: “换个时钟场景” selects a random clock. Random replacements avoid the
current color pair, effect or last selected scene when another option exists.
With only one option, that option is retained; an empty catalog reports no
available option. Explicit names, ordinals and sequential requests still win;
an ambiguous explicit name is not replaced with a random guess.

- `apply_pixelbar_lighting_preset`: omit `name` or pass `随机`.
- `set_pixelbar_light_effect`: omit both fields or pass `mode: "random"`.
  An `effect` without `mode` means `set`. “下一个灯效” still means `next`.
- `activate_pixelbar_scene_by_reference`: omit `scene` for a random scene in
  the requested `category`; omit both for a random scene across categories.
- `configure_pixelbar`: explicitly use `lightingPreset: "随机"`,
  `lightEffectMode: "random"` or `sceneCategory` with `sceneReference: "随机"`.
  Omitted settings remain unchanged. Volume, power, speed, schedules, lyrics
  source, output devices and authorization are never randomized by this rule.

Update the bundled plugin and restart DSH to load the new descriptions.

### Subtitle length contract (0.7.1)

Generated status text should fit one 55-byte UTF-8 send and preserve the current
phase, verified result or next action. The subtitle tool now rejects oversized
text before transport and reports its encoded size and remaining byte budget
in `subtitleLimit`. User-provided custom text is never silently shortened.
Install or update the plugin from Toolbox settings, then restart DSH to load
the new tool descriptions and validation.

### Independent audio control (0.7.0)

Audio effects use the Toolbox's separate Equalizer APO configuration, without
starting FxSound or depending on its driver. `get_pixelbar_audio_status` returns
active Windows playback endpoints, the selected curve, saved presets and the
backend's actual configuration status. `configure_pixelbar_audio` supports
`enabled`, `preampDb`, `balance`, `autoHeadroom`, ten `bandGainsDb` values,
or a saved `preset`. For “低音加一点” use
`{ "group": "bass", "mode": "increase", "value": 1 }`.
Preset loading keeps the current effect power and target; include `enabled`
explicitly when asked to enable effects. Auto headroom is gain compensation,
not a dynamic limiter.

Component installation and endpoint registration must first be completed from
the Audio Control page. A saved curve may return `success: false` and
`not_confirmed` if the backend is missing, disconnected or unwritable; do not
report that the sound changed. `set_pixelbar_audio_output` requires explicit
user intent to switch Windows default playback output, using an endpoint id or
a uniquely matching device name. It does not reroute applications pinned to
another device. Both operations share state with the page. ASIO and exclusive
playback can bypass system effects.

### Display status is application history (0.7.2)

`get_pixelbar_status` reads volume and power from the device, but the device does
not provide a scene readback. `displayState.source` is
`application_last_successful_send` and `displayState.sceneReadbackSupported` is
`false`. `lastSentContentKind` identifies the last successful display send by
this Toolbox process. `lastSentPersonalSceneName` retains the last successfully
sent personal scene even after a temporary task-status message or subtitle.
It is initially `null`; the restore fallback alone does not prove any send.

The compatibility field `activeSceneName` is `null` when the last app send was
not a scene. This does **not** mean an earlier scene activation failed and must
not trigger another activation attempt by itself. Report the activation tool's
result separately from subsequent display content. Neither field confirms what
is currently visible after another app writes to the device or it disconnects.

### Default scene restoration

“切换为默认场景”、“恢复默认场景” and “返回默认场景” use
`restore_pixelbar_default_scene` with `{}`. This is the same operation as the
App's restore button: it returns to the most recently selected personal
pixel-screen scene, with the App's fallback clock scene used only when no
personal scene has been remembered. It is not a scene named “默认” in the catalog,
and must not be interpreted as the ambient-light “氛围呼吸” effect or a saved
lighting preset. Ambient-light effect, color, speed and power are unchanged.

For a combined request such as “恢复默认场景，把音量调到 13”, use
`configure_pixelbar` with `{ "restoreDefaultScene": true, "volume": 13 }`.
`restoreDefaultScene: true` cannot be combined with `sceneCategory`,
`scenePosition` or `sceneReference`; choose either restoration or an explicitly
selected scene. Both tools allow 120 seconds for custom-scene resource uploads.

## Local installation

HaloPixelToolBox can perform these steps from **Settings → Integrations →
DeepSeek Harness**. It detects both a globally available `dsh` executable and
the `npx @deepseek-ai/dsh` installation used by the DSH web app. The default
`DSH_HOME` is `%USERPROFILE%\.dsh`; its `settings.yaml` remains owned by DSH.

Install the plugin's runtime dependencies once when using a linked checkout,
then build and start HaloPixelToolBox and add the bundle to a DSH profile:

```powershell
pnpm --dir ./integrations/deepseek-harness/halo-pixelbar-tools install
dsh plugin --profile halo-pixelbar add ./integrations/deepseek-harness/halo-pixelbar-tools
dsh --profile halo-pixelbar --dump-config
dsh --profile halo-pixelbar web
```

The default pipe timeout is 15 seconds. Scene activation, combined
configuration, lyric configuration, and lyric-stop calls have a two-minute
minimum because restoring or activating a custom scene may need to upload
resources over HID. A larger `timeoutMs` in the profile's `cordis.patch.yml`
extends every call, including these long operations. You can also override
`pipeName` there when the local deployment requires it.

Pipe connection failures, timeouts, malformed responses and interrupted calls
are returned to DSH as ordinary structured tool results instead of throwing.
Connection and response-format failures use `transportError` / `transport_error`
and remain retryable:

```json
{
  "success": false,
  "status": "transportError",
  "code": "transport_error",
  "message": "Cannot connect to HaloPixelToolBox pipe ...",
  "retryable": true
}
```

Timeouts use `timedOut` / `timed_out` and remain retryable. Interrupted calls
use `cancelled` / `cancelled` and are not retryable. Pipe-level request errors
such as `invalid_params` and `method_not_found` use `requestError`, preserve the
server error code, and are not retryable. This lets the Agent explain the exact
failure and decide whether a retry is useful without leaving an unmatched tool
call in the DSH conversation.

Ambient-light controls use three distinct concepts. `set_pixelbar_ambient_light`
changes power only. `set_pixelbar_light_effect` changes the hardware animation
mode and accepts `set`, `next`, `previous`, or `random`; `set` accepts the
Chinese or English name, a simplified/traditional alias, or a 1-based position.
The six modes are `氛围呼吸` (`Breathing`), `幻彩潮汐` (`ColorTide`),
`纯色静光` (`Static`), `炫彩涟漪` (`Ripple`), `流光逐影` (`Flow`), and
`动态光影` (`Dynamic`). Changing the mode preserves color, speed, brightness,
power, and pixel-screen settings. `set_pixelbar_light_speed` changes only the
current mode's speed, using integer levels from 1 (slowest) to 10 (fastest).

A saved lighting preset is a named two-color configuration for the ambient
light and pixel screen, such as `贺喜遥香`; it is not an animation mode. A
pixel-screen scene is also separate. `get_pixelbar_catalog` returns these in
separate `ambientLightEffects`, `currentAmbientLightEffect`, `lightingPresets`,
and `scenes` fields. For example, “换个氛围灯效” maps to
`set_pixelbar_light_effect` with `mode: "random"`, while “应用贺喜遥香灯光预设”
maps to `apply_pixelbar_lighting_preset`.

Spotify lyrics use the Windows media session for the currently
playing track, match synchronized lyrics in the background, and share the same
session with HaloPixelToolBox's Lyrics Subtitle page. A start call may first
return `preparing`; query `get_pixelbar_lyrics_status` for the final state.

`configure_pixelbar_lyrics` is the general lyrics entry point. Its `provider`
may be omitted to retain the current source (or use Spotify when no source has
been selected). When supplied, it accepts `spotify`, `netEase`, `qqMusic`,
`local`, or `custom`, plus enable/disable, timing offset, scrolling, local LRC
path and scene restoration controls. The device service returns a structured
`notSupported` result for a source that the current process-level bridge cannot
start. The original Spotify-specific tools remain available for compatibility.

`show_pixelbar_subtitle` accepts text, `left`/`center`/`right` alignment and
`none`/`left`/`right` scrolling. Subtitle text is limited by the device protocol
to **55 UTF-8 bytes per send**, including punctuation, spaces and labels. This
fits about 18 common Chinese characters, not 55 Chinese characters; emoji may
use more bytes. Scrolling does not increase the limit. For generated status,
write one concise phase, verified result or required next action, for example
`正在运行测试`, `测试失败，请查看会话`, or `等待授权，请先听详情`. Preserve failure,
uncertainty and approval requirements; keep logs, paths and detailed explanations
in the conversation or spoken prompt. Do not silently summarize or truncate
user-provided custom text.

The plugin checks encoded length before opening the device pipe. An oversized
request returns `success: false`, `status: "invalidArgument"`,
`code: "subtitle_too_long"` and sends nothing. Correct the generated summary or
ask the user to choose shorter custom text; do not retry the same oversized text.
Both accepted-length and rejected requests include `subtitleLimit`:

```json
{ "encoding": "UTF-8", "unit": "bytes", "actual": 57, "max": 55, "remaining": 0, "fits": false }
```

These fields describe the input budget, not whether the device displayed it;
check the returned `success` and status separately. The tool also accepts a color request;
unsupported colors are reported by the device service without sending partial
content.

Lighting automation can be read with
`get_pixelbar_lighting_automation` and changed with
`configure_pixelbar_lighting_automation`. Schedule bounds may be supplied as
minute-of-day integers (`0`-`1439`) or 24-hour `HH:mm` strings:

```json
{
  "displayOffEnabled": true,
  "scheduleEnabled": true,
  "startTime": "23:30",
  "endTime": "07:00"
}
```

For scene requests that do not have exact indexes, use
`activate_pixelbar_scene_by_reference`. The service resolves simplified or
traditional Chinese, partial names, ordinal phrases such as `第一个`, and
category-random requests. `configure_pixelbar` provides the same scene
reference plus light/screen power, hardware effect, and speed fields for
multi-setting requests. `lightEffectMode: "set"` requires
`lightEffectReference`; `next`, `previous`, and `random` require that the
reference be omitted. All original fields and tools remain compatible. Combined
configuration stops after the first failed operation by default. Set
`continueOnError` to `true` only when every requested change should still be
attempted; the result then reports any partial failure alongside the operations
that succeeded.

import net from 'node:net'
import { randomUUID } from 'node:crypto'
import Schema from '@deepseek-ai/schemastery'

export const name = 'halo-pixelbar-tools'
export const inject = ['tools']

export const Config = Schema.object({
  pipeName: Schema.string().default('HaloPixelToolBox.DeviceControl.v1'),
  timeoutMs: Schema.number().default(15000),
})

/** @typedef {{ pipeName: string, timeoutMs: number }} Config */

export function apply(ctx, config) {
  registerTool(ctx, config, {
    name: 'get_pixelbar_status',
    description: 'Read the connected Halo PixelBar volume, ambient light and pixel screen status.',
    parameters: {},
    method: 'get_status',
  })
  registerTool(ctx, config, {
    name: 'get_pixelbar_audio_status',
    description: 'Read HaloPixelToolBox independent Windows audio control: active playback endpoints, editable target, ten EQ bands, preamp, balance, saved audio presets and Equalizer APO readiness. Does not start or require FxSound. Read backend status before claiming an effect is active; saved settings alone do not prove audible processing.',
    parameters: {},
    method: 'get_audio_status',
  })
  registerTool(ctx, config, {
    name: 'configure_pixelbar_audio',
    description: 'Configure independent playback audio effects, shared with the Audio Control page. This is audio EQ, not ambient lighting or pixel-screen effects. Requires Equalizer APO installation and endpoint setup through the app; never installs components automatically. enabled controls effect bypass, not speaker power. preset loads a saved audio curve and keeps target and effect power unless enabled is explicit. “低音加一点” uses group bass, mode increase, value 1 dB. Values are bounded to ±12 dB. Auto headroom is gain compensation, not a dynamic limiter. If the backend is not ready, a curve may be saved but success is false; explain the returned setup requirement. Does not require FxSound and does not change the system default playback device.',
    parameters: {
      enabled: { type: 'boolean', description: 'Enable or bypass the independent audio effects.' },
      preset: { type: 'string', minLength: 1, description: 'Saved audio curve name or id, from get_pixelbar_audio_status. Cannot combine with curve numeric settings; may combine with enabled.' },
      preampDb: { type: 'number', minimum: -30, maximum: 12, description: 'Preamp gain in dB; autoHeadroom may reduce the effective gain to leave room for boosted EQ bands.' },
      balance: { type: 'number', minimum: -100, maximum: 100, description: 'Stereo balance: -100 left, 0 center, +100 right.' },
      autoHeadroom: { type: 'boolean', description: 'Automatically reserve EQ boost headroom; this is gain compensation, not a dynamic limiter.' },
      bandGainsDb: { type: 'array', minItems: 10, maxItems: 10, items: { type: 'number', minimum: -12, maximum: 12 }, description: 'Ten gains for 31.25,62.5,125,250,500,1000,2000,4000,8000,16000 Hz. Cannot combine with preset or group.' },
      group: { type: 'string', enum: ['bass', 'mid', 'treble'], description: 'bass adjusts first 3 bands, mid middle 4, treble last 3; requires mode and value.' },
      mode: { type: 'string', enum: ['set', 'increase', 'decrease'], description: 'Relative or absolute frequency-group adjustment. Requires group and value.' },
      value: { type: 'number', minimum: -12, maximum: 12, description: 'dB value; increase/decrease require a positive amount. Requires group and mode.' },
    },
    parameterConstraints: { minProperties: 1 },
    method: 'configure_audio',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_audio_output',
    description: 'Explicitly change Windows default playback output for console, multimedia and communications. Use only when the user asks to switch audio output. This is independent of FxSound and does not move individual applications pinned to another output. Use an exact endpoint id or uniquely matching device name from get_pixelbar_audio_status; ambiguous names are rejected.',
    parameters: {
      device: { type: 'string', minLength: 1, required: true, description: 'Active playback endpoint id or uniquely matching name.' },
    },
    method: 'set_audio_output',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_ambient_light',
    description: 'Turn the Halo PixelBar ambient-light power on or off. This changes power only; it does not change the hardware animation effect, speed, color, brightness, or a saved lighting preset.',
    parameters: {
      enabled: { type: 'boolean', required: true, description: 'True turns the ambient light on; false turns it off.' },
    },
    method: 'set_ambient_light',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_light_effect',
    description: 'Change or cycle the Halo PixelBar hardware ambient-light animation mode. Use next for “换个氛围灯效/换一种/下一个”, previous for the previous mode, random for a different random mode, or set with an explicit effect name or 1-based position. The six effects are 氛围呼吸/Breathing, 幻彩潮汐/ColorTide, 纯色静光/Static, 炫彩涟漪/Ripple, 流光逐影/Flow, and 动态光影/Dynamic. This changes only the ambient animation mode and keeps color, speed, brightness, power, and pixel-screen settings; it is not a saved two-color lighting preset such as 贺喜遥香.',
    parameters: {
      mode: { type: 'string', enum: ['set', 'next', 'previous', 'random'], required: true, description: 'Use set for an explicit effect, next for “换个/下一个灯效”, previous for the previous effect, or random for a different random effect.' },
      effect: { type: 'string', minLength: 1, description: 'Required only when mode is set; omit it for next, previous, and random. Accepts a Chinese or English effect name, simplified/traditional alias, or 1-based position from 1 to 6.' },
    },
    method: 'set_ambient_light_effect',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_light_speed',
    description: 'Set or adjust the speed of the current Halo PixelBar ambient-light animation mode. This changes speed only; it does not select another hardware light effect, saved lighting preset, pixel-screen scene, or subtitle scroll speed. Use increase by 1 for “灯速加快一点/快一档”, decrease by 1 for “慢一点”, set 10 for fastest, and set 1 for slowest.',
    parameters: {
      mode: { type: 'string', enum: ['set', 'increase', 'decrease'], required: true, description: 'Use set for an absolute speed, increase to make the light effect faster, or decrease to make it slower.' },
      value: { type: 'integer', minimum: 1, maximum: 10, required: true, description: 'Absolute 1-10 speed for set, or number of steps for increase/decrease. 1 is slowest and 10 is fastest.' },
    },
    method: 'set_ambient_light_speed',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_screen',
    description: 'Turn the Halo PixelBar 256x32 pixel screen on or off.',
    parameters: {
      enabled: { type: 'boolean', required: true, description: 'True turns the pixel screen on; false turns it off.' },
    },
    method: 'set_pixel_screen',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_volume',
    description: 'Set the Halo PixelBar speaker volume to an integer from 0 to 16.',
    parameters: {
      volume: { type: 'integer', minimum: 0, maximum: 16, required: true, description: 'Integer speaker volume from 0 to 16.' },
    },
    method: 'set_volume',
  })
  registerTool(ctx, config, {
    name: 'show_pixelbar_time',
    description: 'Calibrate the Halo PixelBar clock from Windows local time and switch the screen to its clock UI.',
    parameters: {},
    method: 'show_time',
  })
  registerTool(ctx, config, {
    name: 'start_pixelbar_spotify_lyrics',
    description: 'Select and start the HaloPixelToolBox lyrics-subtitle pipeline for the song currently playing in Spotify. Use this for requests such as “切到 Spotify 歌词”; Spotify lyrics are a subtitle source, not a PixelBar scene. The call can return preparing while lyrics are matched in the background; use get_pixelbar_lyrics_status to check progress.',
    parameters: {
      offsetMs: { type: 'integer', minimum: -30000, maximum: 30000, description: 'Optional timing offset in milliseconds. Positive displays lyrics later; negative displays them earlier.' },
      scroll: { type: 'boolean', description: 'Whether long lyric lines may scroll from right to left. Defaults to true.' },
    },
    method: 'start_spotify_lyrics',
  })
  registerTool(ctx, config, {
    name: 'get_pixelbar_lyrics_status',
    description: 'Read the shared HaloPixelToolBox lyrics-subtitle session state, current Spotify track, lyric source, current line and timing offset.',
    parameters: {},
    method: 'get_lyrics_status',
  })
  registerTool(ctx, config, {
    name: 'set_pixelbar_lyrics_offset',
    description: 'Adjust the active or next Spotify lyrics timing. Positive milliseconds delay the lyrics; negative milliseconds make them appear earlier.',
    parameters: {
      offsetMs: { type: 'integer', minimum: -30000, maximum: 30000, required: true, description: 'Absolute lyrics offset in milliseconds; for example -500 means 提前半秒.' },
    },
    method: 'set_lyrics_offset',
  })
  registerTool(ctx, config, {
    name: 'stop_pixelbar_lyrics',
    description: 'Stop the shared HaloPixelToolBox lyrics-subtitle session and optionally restore the most recently selected personal scene.',
    parameters: {
      restoreScene: { type: 'boolean', description: 'Restore the most recently selected personal scene after stopping. Defaults to true.' },
    },
    method: 'stop_lyrics',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'configure_pixelbar_lyrics',
    description: 'Configure the shared HaloPixelToolBox lyrics-subtitle session. Select a source, start or stop it, adjust timing and scrolling, or provide a local LRC path. Spotify is currently the source that can be started directly by the Agent; unsupported sources return a structured device result.',
    parameters: {
      provider: { type: 'string', enum: ['spotify', 'netEase', 'qqMusic', 'local', 'custom'], description: 'Optional lyrics provider. Use spotify for Spotify 当前播放, netEase for 网易云音乐, qqMusic for QQ 音乐, local for a local LRC file, or custom for a custom provider.' },
      enabled: { type: 'boolean', description: 'True starts or updates lyrics; false stops lyrics.' },
      offsetMs: { type: 'integer', minimum: -30000, maximum: 30000, description: 'Optional timing offset in milliseconds. Positive displays lyrics later; negative displays them earlier.' },
      scroll: { type: 'boolean', description: 'Whether long lyric lines may scroll from right to left.' },
      localLrcPath: { type: 'string', description: 'Optional absolute path to an LRC file. Primarily used with the local provider or as a Spotify lyric fallback.' },
      restoreScene: { type: 'boolean', description: 'When disabling lyrics, restore the most recently selected personal scene. Defaults to true.' },
    },
    method: 'configure_lyrics',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'show_pixelbar_subtitle',
    description: 'Show custom text on the Halo PixelBar subtitle display with alignment, scrolling and an optional color request.',
    parameters: {
      text: { type: 'string', minLength: 1, maxLength: 55, required: true, description: 'Subtitle text to display. The device protocol allows at most 55 UTF-8 bytes, so Chinese text uses more than one byte per character and has a lower practical character limit.' },
      layout: { type: 'string', enum: ['left', 'center', 'right'], description: 'Text alignment. Defaults to center.' },
      scroll: { type: 'string', enum: ['none', 'left', 'right'], description: 'Scrolling direction. Defaults to none; left moves text from right to left.' },
      color: { type: 'string', description: 'Requested text color, such as #FFFFFF, white or 白色. The device service returns notSupported when the current protocol cannot represent the requested color.' },
    },
    method: 'show_subtitle',
  })
  registerTool(ctx, config, {
    name: 'activate_pixelbar_scene',
    description: 'Activate a Halo PixelBar scene by its category and scene indexes.',
    parameters: {
      categoryIndex: { type: 'integer', minimum: 0, maximum: 255, required: true, description: 'Zero-based scene category index shown by HaloPixelToolBox.' },
      sceneIndex: { type: 'integer', minimum: 0, maximum: 255, required: true, description: 'Zero-based scene index within the category.' },
    },
    method: 'activate_scene',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'restore_pixelbar_default_scene',
    description: 'Restore the PixelBar display using the same operation as the App “恢复默认场景” button. Use this for “切换为默认场景/恢复默认场景/返回默认场景”. It restores the most recently selected personal pixel-screen scene; if no personal scene was remembered, the App uses its fallback clock scene. “默认场景” is a restore operation, not a scene name to search for in the catalog and not the ambient-light Breathing/氛围呼吸 effect. Do not guess a scene category or replace this request with a light-effect or lighting-preset change. This does not change ambient-light effect, color, speed, or power.',
    parameters: {},
    method: 'restore_default_scene',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'get_pixelbar_catalog',
    description: 'List the six hardware ambient-light animation modes, the current ambient effect, saved two-color lighting presets, and pixel-screen scene categories. The result separates ambientLightEffects, currentAmbientLightEffect, lightingPresets, and scenes. Pass a category such as 自定义 to list its scenes and 1-based positions. “默认场景” means restore_pixelbar_default_scene, not a catalog scene name; do not search for it or infer an ambient-light default.',
    parameters: {
      category: { type: 'string', description: 'Optional scene category name, for example 自定义、时钟、游戏 or Custom.' },
    },
    method: 'get_catalog',
  })
  registerTool(ctx, config, {
    name: 'apply_pixelbar_lighting_preset',
    description: 'Apply a saved, named HaloPixelToolBox two-color lighting preset, which sets ambient-light and pixel-screen colors together. Use this only when the user names or explicitly asks for a saved lighting preset/配色, such as 贺喜遥香. Do not use it for “换个氛围灯效”, “下一个灯效”, or another hardware animation mode. Minor spacing and one-character traditional/simplified differences are tolerated.',
    parameters: {
      name: { type: 'string', required: true, description: 'Saved two-color lighting preset name, for example 贺喜遥香. This is not an ambient-light animation effect name.' },
    },
    method: 'apply_lighting_preset',
  })
  registerTool(ctx, config, {
    name: 'activate_pixelbar_scene_by_position',
    description: 'Activate a scene using a human-readable category and a 1-based position. Resource-upload scenes can take up to two minutes.',
    parameters: {
      category: { type: 'string', required: true, description: 'Scene category such as 自定义、时钟、游戏 or Custom.' },
      position: { type: 'integer', minimum: 1, maximum: 255, required: true, description: '1-based position within the category; 第一个 is 1.' },
    },
    method: 'activate_scene_by_position',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'activate_pixelbar_scene_by_reference',
    description: 'Activate a Halo PixelBar scene using human-readable category and scene references. Both names may be simplified or traditional Chinese, partial names, ordinal phrases such as 第一个, or 随机/random when supported by the category resolver.',
    parameters: {
      category: { type: 'string', minLength: 1, required: true, description: 'Scene category reference, for example 自定义、時鐘、游戏 or Custom.' },
      scene: { type: 'string', minLength: 1, required: true, description: 'Scene name or reference, for example 第一个、随机、像素时钟 or a partial scene name.' },
    },
    method: 'activate_scene_by_reference',
    minimumTimeoutMs: 120000,
  })
  registerTool(ctx, config, {
    name: 'get_pixelbar_lighting_automation',
    description: 'Read the automatic lighting configuration and current runtime state, including display-off handling and the scheduled lights-off time range.',
    parameters: {},
    method: 'get_lighting_automation',
  })
  registerTool(ctx, config, {
    name: 'configure_pixelbar_lighting_automation',
    description: 'Configure automatic lights-off behavior. Enable lights-off when the PC display turns off, or set a daily time range with either minute-of-day values or HH:mm strings.',
    parameters: {
      displayOffEnabled: { type: 'boolean', description: 'Turn lights off when Windows reports that the display has turned off.' },
      scheduleEnabled: { type: 'boolean', description: 'Enable or disable the daily lights-off schedule.' },
      startMinutes: { type: 'integer', minimum: 0, maximum: 1439, description: 'Optional schedule start as minutes after midnight.' },
      endMinutes: { type: 'integer', minimum: 0, maximum: 1439, description: 'Optional schedule end as minutes after midnight.' },
      startTime: { type: 'string', pattern: '^([01]\\d|2[0-3]):[0-5]\\d$', description: 'Optional schedule start in 24-hour HH:mm form, for example 23:30.' },
      endTime: { type: 'string', pattern: '^([01]\\d|2[0-3]):[0-5]\\d$', description: 'Optional schedule end in 24-hour HH:mm form, for example 07:00.' },
    },
    method: 'configure_lighting_automation',
  })
  registerTool(ctx, config, {
    name: 'configure_pixelbar',
    description: 'Preferred tool for a single request that changes multiple PixelBar settings. It can toggle lights and screen, change or cycle the hardware ambient-light animation mode, adjust that mode\'s speed, apply a named saved two-color lighting preset, activate a pixel-screen scene, restore the default personal scene, and set volume in one call. For “恢复默认场景并调整音量”等多项操作, use restoreDefaultScene true with the other requested settings, and omit sceneCategory, scenePosition, and sceneReference. Restoring default means the App restoration operation, not a scene named 默认 or an ambient-light default. A light effect, saved lighting preset, and pixel-screen scene are distinct settings.',
    parameters: {
      ambientLightEnabled: { type: 'boolean', description: 'Optional ambient-light power state.' },
      pixelScreenEnabled: { type: 'boolean', description: 'Optional 256x32 pixel-screen power state.' },
      lightEffectMode: { type: 'string', enum: ['set', 'next', 'previous', 'random'], description: 'Optional hardware ambient-light animation operation. set requires lightEffectReference; next, previous, and random require that lightEffectReference be omitted.' },
      lightEffectReference: { type: 'string', minLength: 1, description: 'Hardware ambient-light effect name, simplified/traditional alias, or 1-based position. Provide only with lightEffectMode set.' },
      lightSpeedMode: { type: 'string', enum: ['set', 'increase', 'decrease'], description: 'Optional ambient-light speed operation; provide with lightSpeedValue.' },
      lightSpeedValue: { type: 'integer', minimum: 1, maximum: 10, description: 'Absolute 1-10 speed for set, or number of steps for increase/decrease; provide with lightSpeedMode.' },
      lightingPreset: { type: 'string', description: 'Optional saved two-color lighting preset name, such as 贺喜遥香. Do not use this field for a hardware ambient-light animation effect.' },
      restoreDefaultScene: { type: 'boolean', description: 'Set true to restore the most recently selected personal pixel-screen scene, or the App fallback clock when none was remembered. Equivalent to the App “恢复默认场景” button and does not change ambient lighting. When true, omit sceneCategory, scenePosition, and sceneReference; false performs no restoration.' },
      sceneCategory: { type: 'string', description: 'Optional human-readable scene category; provide with scenePosition or sceneReference.' },
      scenePosition: { type: 'integer', minimum: 1, maximum: 255, description: 'Optional 1-based scene position; provide with sceneCategory.' },
      sceneReference: { type: 'string', description: 'Optional scene name, partial name, ordinal phrase or random reference; provide with sceneCategory.' },
      volume: { type: 'integer', minimum: 0, maximum: 16, description: 'Optional integer speaker volume from 0 to 16.' },
      continueOnError: { type: 'boolean', description: 'Whether to continue later operations after one operation fails. Defaults to false; set true to attempt every requested change and return a partial-failure result.' },
    },
    parameterConstraints: {
      not: {
        properties: { restoreDefaultScene: { const: true } },
        required: ['restoreDefaultScene'],
        anyOf: [
          { required: ['sceneCategory'] },
          { required: ['scenePosition'] },
          { required: ['sceneReference'] },
        ],
      },
    },
    method: 'configure_device',
    minimumTimeoutMs: 120000,
  })
}

function registerTool(ctx, config, definition) {
  const effectiveTimeoutMs = Math.max(
    1000,
    Number(config.timeoutMs) || 15000,
    Number(definition.minimumTimeoutMs) || 0,
  )
  const required = []
  const properties = Object.fromEntries(Object.entries(definition.parameters).map(([name, parameter]) => {
    const { required: isRequired, ...schema } = parameter
    if (isRequired) required.push(name)
    return [name, schema]
  }))

  ctx.tools.register({
    name: definition.name,
    description: definition.description,
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties,
      ...(required.length > 0 ? { required } : {}),
      ...(definition.parameterConstraints ?? {}),
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      render: (_args, value) => [{ type: 'text', text: JSON.stringify(value) }],
    },
    async execute(args, exec) {
      try {
        return await callDevice(
          config,
          definition.method,
          args,
          exec?.signal,
          effectiveTimeoutMs,
        )
      } catch (error) {
        return toFailureResult(error)
      }
    },
    timeoutMs: effectiveTimeoutMs,
  })
}

function callDevice(config, method, parameters, signal, effectiveTimeoutMs) {
  const pipePath = `\\\\.\\pipe\\${config.pipeName}`
  const timeoutMs = Math.max(1000, Number(effectiveTimeoutMs) || 15000)
  const request = JSON.stringify({ id: randomUUID(), method, parameters }) + '\n'

  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipePath)
    let buffer = ''
    let settled = false
    const finish = (callback) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      signal?.removeEventListener('abort', onAbort)
      socket.destroy()
      callback()
    }
    const onAbort = () => finish(() => reject(new DeviceCallError(
      signal?.reason instanceof Error ? signal.reason.message : 'PixelBar tool call was interrupted',
      'cancelled',
      'cancelled',
      false,
    )))
    const timer = setTimeout(() => {
      finish(() => reject(new DeviceCallError(
        `HaloPixelToolBox did not reply within ${timeoutMs} ms`,
        'timedOut',
        'timed_out',
        true,
      )))
    }, timeoutMs)

    if (signal?.aborted) {
      onAbort()
      return
    }
    signal?.addEventListener('abort', onAbort, { once: true })

    socket.setEncoding('utf8')
    socket.on('connect', () => socket.write(request))
    socket.on('data', (chunk) => {
      buffer += chunk
      if (buffer.length > 64 * 1024) {
        finish(() => reject(new DeviceCallError(
          'HaloPixelToolBox returned an oversized response',
          'transportError',
          'transport_error',
          true,
        )))
        return
      }

      const newline = buffer.indexOf('\n')
      if (newline < 0) return

      try {
        const response = JSON.parse(buffer.slice(0, newline))
        if (response.error) {
          const code = typeof response.error.code === 'string' && response.error.code.length > 0
            ? response.error.code
            : 'device_error'
          const message = typeof response.error.message === 'string' && response.error.message.length > 0
            ? response.error.message
            : 'HaloPixelToolBox rejected the request'
          finish(() => reject(new DeviceCallError(
            message,
            'requestError',
            code,
            false,
          )))
        } else {
          finish(() => resolve(response.result))
        }
      } catch (error) {
        finish(() => reject(new DeviceCallError(
          `Invalid HaloPixelToolBox response: ${error instanceof Error ? error.message : String(error)}`,
          'transportError',
          'transport_error',
          true,
        )))
      }
    })
    socket.on('error', (error) => finish(() => reject(new DeviceCallError(
      `Cannot connect to HaloPixelToolBox pipe ${config.pipeName}: ${error.message}`,
      'transportError',
      'transport_error',
      true,
    ))))
    socket.on('end', () => {
      if (!settled) finish(() => reject(new DeviceCallError(
        'HaloPixelToolBox closed the pipe without a response',
        'transportError',
        'transport_error',
        true,
      )))
    })
  })
}

class DeviceCallError extends Error {
  constructor(message, status, code, retryable) {
    super(message)
    this.name = 'DeviceCallError'
    this.status = status
    this.code = code
    this.retryable = retryable
  }
}

function toFailureResult(error) {
  if (error instanceof DeviceCallError) {
    return {
      success: false,
      status: error.status,
      code: error.code,
      message: error.message,
      retryable: error.retryable,
    }
  }

  return {
    success: false,
    status: 'transportError',
    code: 'transport_error',
    message: error instanceof Error ? error.message : String(error),
    retryable: true,
  }
}

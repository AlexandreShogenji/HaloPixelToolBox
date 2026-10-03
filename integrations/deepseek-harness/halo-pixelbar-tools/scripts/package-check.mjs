import { execFileSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { readFileSync } from 'node:fs'
import net from 'node:net'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { apply } from '../index.js'

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const registrations = []

apply({
  tools: {
    register(definition) {
      registrations.push(definition)
    },
  },
}, {
  pipeName: 'HaloPixelToolBox.DeviceControl.v1',
  timeoutMs: 15000,
})

assert(registrations.length === 25, `expected 25 registered tools, received ${registrations.length}`)
assert(new Set(registrations.map(({ name }) => name)).size === registrations.length, 'tool names must be unique')

const tools = Object.fromEntries(registrations.map((definition) => [definition.name, definition]))
assertDescriptionIncludes('get_pixelbar_status', ['not hardware readback', 'lastSentContentKind', 'lastSentPersonalSceneName', 'null does not mean a prior scene activation failed', 'Never retry scene activation solely'])
assertSchema('configure_pixelbar_audio', 'preampDb', { type: 'number', minimum: -30, maximum: 12 })
assertSchema('configure_pixelbar_audio', 'balance', { type: 'number', minimum: -100, maximum: 100 })
assertSchema('configure_pixelbar_audio', 'bandGainsDb', { type: 'array', minItems: 10, maxItems: 10 })
assertRequired('set_pixelbar_audio_output', ['device'])
assertEnum('configure_pixelbar_audio', 'group', ['bass', 'mid', 'treble'])
assertDescriptionIncludes('configure_pixelbar_audio', ['not ambient lighting', 'success is false', 'not a dynamic limiter'])
assertSchema('set_pixelbar_volume', 'volume', { type: 'integer', minimum: 0, maximum: 16 })
assertSchema('set_pixelbar_light_effect', 'mode', { type: 'string' })
assertSchema('set_pixelbar_light_effect', 'effect', { type: 'string', minLength: 1 })
assertSchema('set_pixelbar_light_speed', 'value', { type: 'integer', minimum: 1, maximum: 10 })
assertSchema('activate_pixelbar_scene', 'categoryIndex', { type: 'integer', minimum: 0, maximum: 255 })
assertSchema('activate_pixelbar_scene', 'sceneIndex', { type: 'integer', minimum: 0, maximum: 255 })
assertSchema('activate_pixelbar_scene_by_position', 'position', { type: 'integer', minimum: 1, maximum: 255 })
assertSchema('activate_pixelbar_scene_by_reference', 'category', { type: 'string', minLength: 1 })
assertSchema('activate_pixelbar_scene_by_reference', 'scene', { type: 'string', minLength: 1 })
assertSchema('show_pixelbar_subtitle', 'text', { type: 'string', minLength: 1, maxLength: 55 })
assertSchema('show_pixelbar_subtitle', 'layout', { type: 'string' })
assertSchema('show_pixelbar_subtitle', 'scroll', { type: 'string' })
assertSchema('configure_pixelbar_lyrics', 'provider', { type: 'string' })
assertSchema('configure_pixelbar_lyrics', 'offsetMs', { type: 'integer', minimum: -30000, maximum: 30000 })
assertSchema('configure_pixelbar_lighting_automation', 'startMinutes', { type: 'integer', minimum: 0, maximum: 1439 })
assertSchema('configure_pixelbar_lighting_automation', 'endMinutes', { type: 'integer', minimum: 0, maximum: 1439 })
assertSchema('configure_pixelbar_lighting_automation', 'startTime', { type: 'string', pattern: '^([01]\\d|2[0-3]):[0-5]\\d$' })
assertSchema('configure_pixelbar_lighting_automation', 'endTime', { type: 'string', pattern: '^([01]\\d|2[0-3]):[0-5]\\d$' })
assertSchema('configure_pixelbar', 'ambientLightEnabled', { type: 'boolean' })
assertSchema('configure_pixelbar', 'pixelScreenEnabled', { type: 'boolean' })
assertSchema('configure_pixelbar', 'restoreDefaultScene', { type: 'boolean' })
assertSchema('configure_pixelbar', 'lightEffectMode', { type: 'string' })
assertSchema('configure_pixelbar', 'lightEffectReference', { type: 'string', minLength: 1 })
assertSchema('configure_pixelbar', 'lightSpeedMode', { type: 'string' })
assertSchema('configure_pixelbar', 'lightSpeedValue', { type: 'integer', minimum: 1, maximum: 10 })
assertSchema('configure_pixelbar', 'scenePosition', { type: 'integer', minimum: 1, maximum: 255 })
assertSchema('configure_pixelbar', 'sceneReference', { type: 'string' })
assertSchema('configure_pixelbar', 'volume', { type: 'integer', minimum: 0, maximum: 16 })
assertSchema('configure_pixelbar', 'continueOnError', { type: 'boolean' })
assertRequired('show_pixelbar_subtitle', ['text'])
assertDescriptionIncludes('show_pixelbar_subtitle', ['55 UTF-8 bytes', '18 common Chinese characters', 'scrolling does not increase', 'Preserve failure, uncertainty and approval', 'Do not silently shorten user-provided'])
assertRequired('restore_pixelbar_default_scene', [])
assertDeepEqual(tools.restore_pixelbar_default_scene.parameters.properties, {}, 'default restoration must not ask for a fabricated category or scene')
assertRequired('activate_pixelbar_scene_by_reference', [])
assertRequired('apply_pixelbar_lighting_preset', [])
assertRequired('set_pixelbar_light_effect', [])
assertSchema('apply_pixelbar_lighting_preset', 'name', { type: 'string', minLength: 1, default: '随机' })
assertSchema('activate_pixelbar_scene_by_reference', 'category', { default: '随机' })
assertSchema('activate_pixelbar_scene_by_reference', 'scene', { default: '随机' })
assertDescriptionIncludes('set_pixelbar_light_effect', ['use random immediately', 'next only for explicit'])
assertDescriptionIncludes('apply_pixelbar_lighting_preset', ['use 随机 immediately', 'ambiguous explicit names'])
assertDescriptionIncludes('activate_pixelbar_scene_by_reference', ['default to random', 'Honor explicit category'])
for (const property of ['lightingPreset', 'lightEffectMode', 'sceneCategory', 'sceneReference', 'volume']) {
  assert(!Object.hasOwn(tools.configure_pixelbar.parameters.properties[property], 'default'), `combined ${property} must never acquire an implicit change`)
}
assertEnum('set_pixelbar_light_effect', 'mode', ['set', 'next', 'previous', 'random'])
assertEnum('configure_pixelbar', 'lightEffectMode', ['set', 'next', 'previous', 'random'])
assertDescriptionIncludes('set_pixelbar_light_effect', ['换个氛围灯效', 'not a saved two-color lighting preset'])
assertDescriptionIncludes('set_pixelbar_light_speed', ['changes speed only', 'does not select another hardware light effect'])
assertDescriptionIncludes('apply_pixelbar_lighting_preset', ['saved lighting preset/配色', 'Do not use it for “换个氛围灯效”'])
assertDescriptionIncludes('get_pixelbar_catalog', ['ambientLightEffects', 'currentAmbientLightEffect', 'lightingPresets', 'scenes'])
assertDescriptionIncludes('get_pixelbar_catalog', ['restore_pixelbar_default_scene', 'not a catalog scene name'])
assertDescriptionIncludes('restore_pixelbar_default_scene', ['恢复默认场景', 'most recently selected personal', 'fallback clock scene', 'not the ambient-light Breathing/氛围呼吸 effect', 'does not change ambient-light'])
assertDescriptionIncludes('configure_pixelbar', ['restoreDefaultScene true', 'omit sceneCategory, scenePosition, and sceneReference'])
assertDeepEqual(tools.configure_pixelbar.parameters.not, {
  properties: { restoreDefaultScene: { const: true } },
  required: ['restoreDefaultScene'],
  anyOf: [
    { required: ['sceneCategory'] },
    { required: ['scenePosition'] },
    { required: ['sceneReference'] },
  ],
}, 'default-scene restoration schema must exclude every explicit-scene selector when true')
assert(tools.stop_pixelbar_lyrics?.timeoutMs === 120000, 'stop_pixelbar_lyrics must allow scene restoration for 120 seconds')
assert(tools.configure_pixelbar_lyrics?.timeoutMs === 120000, 'configure_pixelbar_lyrics must allow scene restoration for 120 seconds')
assert(tools.activate_pixelbar_scene_by_reference?.timeoutMs === 120000, 'scene references must allow resource uploads for 120 seconds')
assert(tools.restore_pixelbar_default_scene?.timeoutMs === 120000, 'default restoration must allow custom-scene resource uploads for 120 seconds')
assert(tools.get_pixelbar_status?.timeoutMs === 15000, 'ordinary tools must use the configured 15-second timeout')

const aborted = new AbortController()
aborted.abort(new Error('package-check abort'))
const transportResult = await tools.get_pixelbar_status.execute({}, { signal: aborted.signal })
assert(transportResult?.success === false, 'cancelled calls must return success false')
assert(transportResult?.status === 'cancelled', 'cancelled calls must return cancelled status')
assert(transportResult?.code === 'cancelled', 'cancelled calls must return cancelled code')
assert(transportResult?.retryable === false, 'cancelled calls must not be marked retryable')
assert(typeof transportResult?.message === 'string' && transportResult.message.length > 0, 'cancelled calls need a message')

for (const text of ['字'.repeat(19), 'x'.repeat(56), '🙂'.repeat(14), '字'.repeat(18) + 'ab']) {
  // An already-aborted transport proves oversized input is rejected before any pipe call.
  const result = await tools.show_pixelbar_subtitle.execute({ text }, { signal: aborted.signal })
  assert(result?.success === false && result.status === 'invalidArgument' && result.code === 'subtitle_too_long', 'UTF-8 overflow must be rejected before transport, without truncating input')
  assert(result?.retryable === false && result.message.includes('未发送'), 'overflow must explain that nothing was sent and require a corrected request')
  assertDeepEqual(result.subtitleLimit, { encoding: 'UTF-8', unit: 'bytes', actual: Buffer.byteLength(text, 'utf8'), max: 55, remaining: 0, fits: false }, 'overflow must report actual and maximum encoded byte counts')
}
const cancelledSubtitle = await tools.show_pixelbar_subtitle.execute({ text: '测试' }, { signal: aborted.signal })
assert(cancelledSubtitle.success === false && cancelledSubtitle.status === 'cancelled', 'valid length must never convert a failed send into success')
assertDeepEqual(cancelledSubtitle.subtitleLimit, { encoding: 'UTF-8', unit: 'bytes', actual: 6, max: 55, remaining: 49, fits: true }, 'transport failures must still expose the available byte budget')

if (process.platform === 'win32') {
  const protocolPipeName = `HaloPixelToolBox.PackageCheck.${randomUUID()}`
  const protocolPipePath = `\\\\.\\pipe\\${protocolPipeName}`
  const protocolServer = net.createServer((socket) => {
    socket.once('data', () => {
      socket.end(`${JSON.stringify({
        error: {
          code: 'invalid_params',
          message: 'package-check invalid parameters',
        },
      })}\n`)
    })
  })
  await new Promise((resolve, reject) => {
    protocolServer.once('error', reject)
    protocolServer.listen(protocolPipePath, resolve)
  })

  const protocolRegistrations = []
  apply({
    tools: {
      register(definition) {
        protocolRegistrations.push(definition)
      },
    },
  }, {
    pipeName: protocolPipeName,
    timeoutMs: 15000,
  })
  const protocolTools = Object.fromEntries(protocolRegistrations.map((definition) => [definition.name, definition]))
  const protocolResult = await protocolTools.get_pixelbar_status.execute({}, {})
  await new Promise((resolve, reject) => protocolServer.close((error) => error ? reject(error) : resolve()))

  assert(protocolResult?.success === false, 'pipe request errors must return success false')
  assert(protocolResult?.status === 'requestError', 'pipe request errors must return requestError status')
  assert(protocolResult?.code === 'invalid_params', 'pipe request errors must preserve their stable code')
  assert(protocolResult?.retryable === false, 'pipe request errors must not be marked retryable')
  assert(protocolResult?.message === 'package-check invalid parameters', 'pipe request errors must preserve their message')

  const effectPipeName = `HaloPixelToolBox.PackageCheck.Effect.${randomUUID()}`
  const effectPipePath = `\\\\.\\pipe\\${effectPipeName}`
  const effectRequests = []
  const effectServer = net.createServer((socket) => {
    let requestBuffer = ''
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      requestBuffer += chunk
      const newline = requestBuffer.indexOf('\n')
      if (newline < 0) return
      effectRequests.push(JSON.parse(requestBuffer.slice(0, newline)))
      socket.end(`${JSON.stringify({
        result: {
          success: true,
          status: 'succeeded',
          code: 'succeeded',
          message: 'package-check effect accepted',
        },
      })}\n`)
    })
  })
  await new Promise((resolve, reject) => {
    effectServer.once('error', reject)
    effectServer.listen(effectPipePath, resolve)
  })

  const effectRegistrations = []
  apply({
    tools: {
      register(definition) {
        effectRegistrations.push(definition)
      },
    },
  }, {
    pipeName: effectPipeName,
    timeoutMs: 15000,
  })
  const effectTools = Object.fromEntries(effectRegistrations.map((definition) => [definition.name, definition]))
  const directEffectResult = await effectTools.set_pixelbar_light_effect.execute({ mode: 'next' }, {})
  const combinedEffectResult = await effectTools.configure_pixelbar.execute({
    lightEffectMode: 'set',
    lightEffectReference: '幻彩潮汐',
  }, {})
  const restoredDefaultResult = await effectTools.restore_pixelbar_default_scene.execute({}, {})
  const combinedRestoreResult = await effectTools.configure_pixelbar.execute({ restoreDefaultScene: true, volume: 13 }, {})
  const boundarySubtitles = ['x'.repeat(55), '字'.repeat(18) + '!', '🙂'.repeat(13) + 'abc']
  for (const text of boundarySubtitles) {
    const subtitleResult = await effectTools.show_pixelbar_subtitle.execute({ text, scroll: 'left' }, {})
    assert(subtitleResult.success === true, 'a 55-byte subtitle must reach the fake device')
    assertDeepEqual(subtitleResult.subtitleLimit, { encoding: 'UTF-8', unit: 'bytes', actual: 55, max: 55, remaining: 0, fits: true }, 'successful requests must expose input byte counts without claiming device success from length alone')
  }
  // Preserve omitted selectors through the real plugin transport so the device service
  // can apply defaults. A combined request must never acquire unrelated random changes.
  const randomCases = [
    ['apply_pixelbar_lighting_preset', {}, 'apply_lighting_preset'],
    ['apply_pixelbar_lighting_preset', { name: '第三个' }, 'apply_lighting_preset'],
    ['set_pixelbar_light_effect', {}, 'set_ambient_light_effect'],
    ['set_pixelbar_light_effect', { effect: '幻彩潮汐' }, 'set_ambient_light_effect'],
    ['set_pixelbar_light_effect', { mode: 'previous' }, 'set_ambient_light_effect'],
    ['activate_pixelbar_scene_by_reference', {}, 'activate_scene_by_reference'],
    ['activate_pixelbar_scene_by_reference', { category: '时钟' }, 'activate_scene_by_reference'],
    ['activate_pixelbar_scene_by_reference', { category: '时钟', scene: '第三个' }, 'activate_scene_by_reference'],
    ['configure_pixelbar', { volume: 13 }, 'configure_device'],
    ['configure_pixelbar', { lightingPreset: '随机', lightEffectMode: 'random', sceneCategory: '时钟', sceneReference: '随机' }, 'configure_device'],
  ]
  for (const [tool, parameters] of randomCases) {
    const result = await effectTools[tool].execute(parameters, {})
    assert(result.success === true, `${tool} must reach the fake device with optional selectors`)
  }
  await new Promise((resolve, reject) => effectServer.close((error) => error ? reject(error) : resolve()))

  assert(directEffectResult?.success === true, 'direct effect tool must return the fake pipe result')
  assert(combinedEffectResult?.success === true, 'combined effect fields must return the fake pipe result')
  assert(restoredDefaultResult?.success === true, 'default restoration must return the fake pipe result')
  assert(combinedRestoreResult?.success === true, 'combined restoration must return the fake pipe result')
  assert(effectRequests.length === 7 + randomCases.length, 'every explicit and defaulted call must issue exactly one request')
  assert(effectRequests[0]?.method === 'set_ambient_light_effect', 'direct effect tool must use set_ambient_light_effect')
  assertDeepEqual(effectRequests[0]?.parameters, { mode: 'next' }, 'direct effect tool must preserve mode only')
  assert(effectRequests[1]?.method === 'configure_device', 'combined effect fields must use configure_device')
  assertDeepEqual(
    effectRequests[1]?.parameters,
    { lightEffectMode: 'set', lightEffectReference: '幻彩潮汐' },
    'combined effect fields must be forwarded unchanged',
  )
  assert(effectRequests[2]?.method === 'restore_default_scene', 'default restoration must use restore_default_scene rather than a light-effect or scene-name method')
  assertDeepEqual(effectRequests[2]?.parameters, {}, 'default restoration must forward empty parameters')
  assert(effectRequests[3]?.method === 'configure_device', 'combined restoration must use configure_device')
  assertDeepEqual(effectRequests[3]?.parameters, { restoreDefaultScene: true, volume: 13 }, 'combined default restoration must preserve its flag and requested volume')
  for (let index = 0; index < boundarySubtitles.length; index++) {
    assert(effectRequests[index + 4]?.method === 'show_subtitle', 'boundary subtitles must use the subtitle method')
    assertDeepEqual(effectRequests[index + 4]?.parameters, { text: boundarySubtitles[index], scroll: 'left' }, 'valid custom text including emoji must reach the device unchanged')
  }
  for (let index = 0; index < randomCases.length; index++) {
    const [tool, parameters, method] = randomCases[index]
    assert(effectRequests[index + 7]?.method === method, `${tool} must preserve its device method`)
    assertDeepEqual(effectRequests[index + 7]?.parameters, parameters, `${tool} must preserve explicit selectors and not inject unrelated settings`)
  }
}

const extendedRegistrations = []
apply({
  tools: {
    register(definition) {
      extendedRegistrations.push(definition)
    },
  },
}, {
  pipeName: 'HaloPixelToolBox.DeviceControl.v1',
  timeoutMs: 180000,
})
assert(
  extendedRegistrations.every(({ timeoutMs }) => timeoutMs === 180000),
  'a configured timeout above the long-operation minimum must extend every tool call',
)

const shortRegistrations = []
apply({
  tools: {
    register(definition) {
      shortRegistrations.push(definition)
    },
  },
}, {
  pipeName: 'HaloPixelToolBox.DeviceControl.v1',
  timeoutMs: 5000,
})
const shortTools = Object.fromEntries(shortRegistrations.map((definition) => [definition.name, definition]))
assert(shortTools.get_pixelbar_status?.timeoutMs === 5000, 'ordinary tools must accept a shorter configured timeout')
assert(shortTools.stop_pixelbar_lyrics?.timeoutMs === 120000, 'long operations must retain their two-minute minimum')
assert(shortTools.restore_pixelbar_default_scene?.timeoutMs === 120000, 'default restoration must retain its two-minute minimum')

for (const definition of registrations) {
  assert(typeof definition.description === 'string' && definition.description.length > 0, `${definition.name} needs a description`)
  assert(definition.parameters?.type === 'object', `${definition.name} needs an object parameter schema`)
  assert(definition.parameters?.additionalProperties === false, `${definition.name} must reject unknown parameters`)
  assert(typeof definition.execute === 'function', `${definition.name} needs an execute handler`)
}

const expectedPackageFiles = [
  'package/package.json',
  'package/index.js',
  'package/cordis.patch.yml',
  'package/README.md',
  'package/scripts/pipe-smoke-test.mjs',
  'package/scripts/package-check.mjs',
]
const packagedFiles = listPackagedFiles(process.argv[2])
for (const expectedFile of expectedPackageFiles) {
  assert(packagedFiles.has(expectedFile), `package is missing ${expectedFile}`)
}

const sourcePackage = JSON.parse(readFileSync(resolve(packageRoot, 'package.json'), 'utf8'))
let verifiedPackageVersion = sourcePackage.version
if (process.argv[2]) {
  const tarballPath = resolve(packageRoot, process.argv[2])
  const packagedPackage = JSON.parse(readTarballEntry(tarballPath, 'package/package.json').toString('utf8'))
  assert(
    packagedPackage.version === sourcePackage.version,
    `package version ${packagedPackage.version ?? 'missing'} does not match source version ${sourcePackage.version}`,
  )
  verifiedPackageVersion = packagedPackage.version

  for (const relativePath of [
    'index.js',
    'scripts/pipe-smoke-test.mjs',
    'scripts/package-check.mjs',
  ]) {
    const source = readFileSync(resolve(packageRoot, relativePath))
    const packaged = readTarballEntry(tarballPath, `package/${relativePath}`)
    assert(packaged.equals(source), `packaged ${relativePath} does not match the reviewed source file`)
  }
}

console.log(JSON.stringify({
  registeredTools: registrations.map(({ name }) => name),
  checkedPackageFiles: expectedPackageFiles,
  verifiedPackageVersion,
  source: process.argv[2] ? 'tarball' : 'npm pack --dry-run',
}, null, 2))

function assertSchema(toolName, propertyName, expected) {
  const schema = tools[toolName]?.parameters?.properties?.[propertyName]
  assert(schema, `${toolName}.${propertyName} schema is missing`)
  for (const [key, value] of Object.entries(expected)) {
    assert(schema[key] === value, `${toolName}.${propertyName}.${key} should be ${JSON.stringify(value)}`)
  }
}

function assertRequired(toolName, expected) {
  const required = tools[toolName]?.parameters?.required ?? []
  assert(
    required.length === expected.length && expected.every((name) => required.includes(name)),
    `${toolName}.required should be ${JSON.stringify(expected)}`,
  )
}

function assertEnum(toolName, propertyName, expected) {
  const actual = tools[toolName]?.parameters?.properties?.[propertyName]?.enum
  assertDeepEqual(actual, expected, `${toolName}.${propertyName}.enum should match`)
}

function assertDescriptionIncludes(toolName, fragments) {
  const description = tools[toolName]?.description ?? ''
  for (const fragment of fragments) {
    assert(description.includes(fragment), `${toolName} description should include ${JSON.stringify(fragment)}`)
  }
}

function assertDeepEqual(actual, expected, message) {
  assert(JSON.stringify(actual) === JSON.stringify(expected), message)
}

function listPackagedFiles(tarballPath) {
  if (tarballPath) {
    const output = execFileSync('tar', ['-tf', resolve(packageRoot, tarballPath)], {
      cwd: packageRoot,
      encoding: 'utf8',
    })
    return new Set(output.split(/\r?\n/u).filter(Boolean).map(normalizePackagePath))
  }

  const [command, args] = process.platform === 'win32'
    ? ['cmd.exe', ['/d', '/s', '/c', 'npm pack --dry-run --json --ignore-scripts']]
    : ['npm', ['pack', '--dry-run', '--json', '--ignore-scripts']]
  const output = execFileSync(command, args, {
    cwd: packageRoot,
    encoding: 'utf8',
  })
  const report = JSON.parse(output)
  const packageReport = Array.isArray(report) ? report[0] : Object.values(report)[0]
  assert(packageReport && Array.isArray(packageReport.files), 'npm pack dry-run returned an unexpected report')
  return new Set(packageReport.files.map(({ path }) => normalizePackagePath(`package/${path}`)))
}

function readTarballEntry(tarballPath, entryPath) {
  return execFileSync('tar', ['-xOf', tarballPath, entryPath], {
    cwd: packageRoot,
    encoding: 'buffer',
  })
}

function normalizePackagePath(path) {
  return path.replaceAll('\\', '/').replace(/^\.\//u, '')
}

function assert(condition, message) {
  if (!condition) throw new Error(message)
}

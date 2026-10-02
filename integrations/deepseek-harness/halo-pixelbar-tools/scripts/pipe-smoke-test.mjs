import net from 'node:net'
import { randomUUID } from 'node:crypto'

const pipeName = process.argv.slice(2).find((argument) => !argument.startsWith('--'))
  ?? 'HaloPixelToolBox.DeviceControl.v1'
const pipePath = `\\\\.\\pipe\\${pipeName}`
const statusResponse = await sendWithRetry('get_status', {})
assert(statusResponse.result?.success === true, 'get_status should return a successful result')

const lyricsStatusResponse = await sendWithRetry('get_lyrics_status', {})
assert(lyricsStatusResponse.result?.success === true, 'get_lyrics_status should return a successful result')
assert(typeof lyricsStatusResponse.result?.data?.state === 'string', 'lyrics status should include a state')

const automationResponse = await sendWithRetry('get_lighting_automation', {})
assert(automationResponse.result?.success === true, 'get_lighting_automation should return a successful result')
assert(
  Number.isInteger(automationResponse.result?.data?.configuration?.startMinutes),
  'lighting automation should include startMinutes',
)

const unsupportedLyricsResponse = await sendWithRetry('configure_lyrics', {
  provider: 'custom',
  enabled: true,
})
assert(unsupportedLyricsResponse.result?.status === 'notSupported', 'unsupported lyrics providers should be explicit')
assert(unsupportedLyricsResponse.result?.code === 'not_supported', 'unsupported lyrics providers need a stable code')

const unsupportedSubtitleColorResponse = await sendWithRetry('show_subtitle', {
  text: 'Halo Pixel',
  color: '#4080FF',
})
assert(unsupportedSubtitleColorResponse.result?.status === 'notSupported', 'unsupported subtitle colors should be explicit')

const oversizedSubtitleResponse = await sendWithRetry('show_subtitle', {
  text: 'A'.repeat(56),
})
assert(oversizedSubtitleResponse.result?.status === 'invalidArgument', 'subtitle text above 55 UTF-8 bytes should be rejected')

const invalidAutomationResponse = await sendWithRetry('configure_lighting_automation', {
  scheduleEnabled: true,
  startMinutes: 60,
  endMinutes: 60,
})
assert(invalidAutomationResponse.result?.status === 'invalidArgument', 'equal schedule bounds should be rejected')

const unknownSceneReferenceResponse = await sendWithRetry('activate_scene_by_reference', {
  category: '完全不存在的分类',
  scene: '第一个',
})
assert(unknownSceneReferenceResponse.result?.status === 'notFound', 'unknown scene categories should be rejected before HID writes')

let verifiedWrite
let verifiedBatchWrite
if (process.argv.includes('--verify-write')) {
  const currentVolume = statusResponse.result?.data?.volume
  assert(Number.isInteger(currentVolume), 'current device volume is required for the write check')
  const writeResponse = await sendWithRetry('set_volume', { volume: currentVolume })
  assert(writeResponse.result?.success === true, 'writing the current volume should be confirmed')
  verifiedWrite = writeResponse.result

  const batchWriteResponse = await sendWithRetry('configure_device', { volume: currentVolume })
  assert(batchWriteResponse.result?.success === true, 'writing the current volume through configure_device should be confirmed')
  assert(batchWriteResponse.result?.code === 'succeeded', 'successful configure_device results need the stable succeeded code')
  verifiedBatchWrite = batchWriteResponse.result
}

const invalidVolumeResponse = await sendWithRetry('set_volume', { volume: 17 })
assert(invalidVolumeResponse.result?.status === 'invalidArgument', 'volume 17 should be rejected')

const invalidLightSpeedResponse = await sendWithRetry('set_ambient_light_speed', { mode: 'set', value: 11 })
assert(invalidLightSpeedResponse.result?.status === 'invalidArgument', 'light speed 11 should be rejected')

const invalidBatchPairResponse = await sendWithRetry('configure_device', { lightSpeedMode: 'increase' })
assert(invalidBatchPairResponse.error?.code === 'invalid_params', 'incomplete batch parameter pairs should be rejected')

const unknownMethodResponse = await sendWithRetry('not_a_device_method', {})
assert(unknownMethodResponse.error?.code === 'method_not_found', 'unknown methods should be rejected')

const oversizedRequestResponse = await sendRawRequest(Buffer.concat([
  Buffer.alloc(64 * 1024 + 1, 0x20),
  Buffer.from('\n'),
]))
assert(oversizedRequestResponse.error?.code === 'request_too_large', 'requests above 64 KiB should be rejected while reading')

console.log(JSON.stringify({
  status: statusResponse.result,
  lyricsStatus: lyricsStatusResponse.result,
  automation: automationResponse.result,
  unsupportedLyrics: unsupportedLyricsResponse.result,
  unsupportedSubtitleColor: unsupportedSubtitleColorResponse.result,
  oversizedSubtitle: oversizedSubtitleResponse.result,
  invalidAutomation: invalidAutomationResponse.result,
  unknownSceneReference: unknownSceneReferenceResponse.result,
  verifiedWrite,
  verifiedBatchWrite,
  invalidVolume: invalidVolumeResponse.result,
  invalidLightSpeed: invalidLightSpeedResponse.result,
  invalidBatchPair: invalidBatchPairResponse.error,
  unknownMethod: unknownMethodResponse.error,
  oversizedRequest: oversizedRequestResponse.error,
}, null, 2))

async function sendWithRetry(method, parameters) {
  let lastError
  for (let attempt = 1; attempt <= 20; attempt += 1) {
    try {
      return await sendRequest(method, parameters)
    } catch (error) {
      lastError = error
      await new Promise((resolve) => setTimeout(resolve, 250))
    }
  }
  throw lastError
}

function sendRequest(method, parameters) {
  const request = JSON.stringify({ id: randomUUID(), method, parameters }) + '\n'
  return sendRawRequest(request)
}

function sendRawRequest(request) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipePath)
    let buffer = ''
    const timer = setTimeout(() => {
      socket.destroy()
      reject(new Error('Pipe smoke test timed out'))
    }, 8000)

    socket.setEncoding('utf8')
    socket.on('connect', () => socket.write(request))
    socket.on('data', (chunk) => {
      buffer += chunk
      const newline = buffer.indexOf('\n')
      if (newline < 0) return

      clearTimeout(timer)
      socket.destroy()
      try {
        resolve(JSON.parse(buffer.slice(0, newline)))
      } catch (error) {
        reject(error)
      }
    })
    socket.on('error', (error) => {
      clearTimeout(timer)
      reject(error)
    })
  })
}

function assert(condition, message) {
  if (!condition) throw new Error(message)
}

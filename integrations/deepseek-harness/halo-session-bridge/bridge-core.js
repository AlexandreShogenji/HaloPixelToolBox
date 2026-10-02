import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto'
import { createServer } from 'node:http'
import { readFileSync, rmSync } from 'node:fs'
import { chmod, mkdir, open, readFile, rename, rm, stat, writeFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { dirname, isAbsolute, join, resolve } from 'node:path'

export const PROTOCOL_VERSION = 1

const LOOPBACK_HOST = '127.0.0.1'
const DEFAULT_HISTORY_LIMIT = 50
const MAX_HISTORY_LIMIT = 100
const DEFAULT_MAX_ENTRY_TEXT_BYTES = 16 * 1024
const DEFAULT_MAX_PAGE_TEXT_BYTES = 256 * 1024
const DEFAULT_REQUEST_TIMEOUT_MS = 15_000
const DEFAULT_COMMAND_TIMEOUT_MS = 120_000
const MIN_COMMAND_TIMEOUT_SECONDS = 30
const MAX_COMMAND_TIMEOUT_SECONDS = 300
const MAX_COMMAND_BODY_BYTES = 16 * 1024
const MAX_COMMAND_TEXT_BYTES = 12 * 1024
const MAX_TASK_DIRECTORY_NAME_LENGTH = 96
const MAX_TASK_BASE_DIRECTORY_LENGTH = 4096
const MAX_INTERACTION_TEXT_BYTES = 16 * 1024
const TASK_PROMPT_SEARCH_MESSAGES = 250
const TASK_PROMPT_SEARCH_PAGES_PER_STATUS = 2
const TASK_AGENT_PRESET = 'standard'
const MAX_TOOL_OUTCOMES = 32
const MAX_TOOL_RESULT_BYTES = 8 * 1024
const COMMAND_DEDUPE_LIMIT = 256
const COMMAND_DEDUPE_TTL_MS = 24 * 60 * 60 * 1000
const DEVICE_AGENT_PRESET = 'halo-device'
const LEGACY_VOICE_PROMPT_PREFIX = '你是 Halo PixelBar 的语音助手。请严格执行下面的用户口令；优先调用可用的 PixelBar 工具完成操作；完成后只用一句简短中文说明结果，不使用 Markdown。不要改变、扩展或猜测用户原意。用户口令：'
const LEGACY_VOICE_IDENTITY_PREFIX = '你是 Halo PixelBar 的语音助手。'
const LEGACY_VOICE_EXACT_PROMPT_FINGERPRINTS = new Set([
  '244:1b2cc772cbb75f4c92a0a605bd8492b485009aa787d3f8fcc05896ad5d25c023',
])
const RUNTIME_CONTEXT_PREFIX = 'Current runtime context. This snapshot supersedes earlier runtime-context snapshots.'
const LEGACY_CLASSIFIER_PAGE_MESSAGES = 2
const DEFAULT_LEGACY_SESSION_PROBES_PER_LIST = 20
const DEFAULT_LEGACY_SESSION_INSPECTS_PER_LIST = 12
const DEFAULT_LEGACY_CLASSIFICATION_BUDGET_MS = 6_000
const CLASSIFICATION_CACHE_SCHEMA_VERSION = 1
const CLASSIFICATION_RULE_VERSION = 'pixelbar-device-v8'
const MAX_CLASSIFICATION_CACHE_ENTRIES = 5_000
const MAX_CLASSIFICATION_CACHE_BYTES = 2 * 1024 * 1024
const CLASSIFICATION_CACHE_LOCK_STALE_MS = 30_000
const CLASSIFICATION_CACHE_LOCK_ATTEMPTS = 8
const PIXELBAR_TOOL_NAMES = new Set([
  'get_pixelbar_status',
  'set_pixelbar_ambient_light',
  'set_pixelbar_light_effect',
  'set_pixelbar_light_speed',
  'set_pixelbar_screen',
  'set_pixelbar_volume',
  'show_pixelbar_time',
  'start_pixelbar_spotify_lyrics',
  'get_pixelbar_lyrics_status',
  'set_pixelbar_lyrics_offset',
  'stop_pixelbar_lyrics',
  'configure_pixelbar_lyrics',
  'show_pixelbar_subtitle',
  'activate_pixelbar_scene',
  'get_pixelbar_catalog',
  'apply_pixelbar_lighting_preset',
  'activate_pixelbar_scene_by_position',
  'activate_pixelbar_scene_by_reference',
  'get_pixelbar_lighting_automation',
  'configure_pixelbar_lighting_automation',
  'configure_pixelbar',
])
const MANUAL_DEVICE_DISQUALIFYING_EVENTS = new Set([
  'command/run',
  'todo/write',
  'goal/change',
  'subagent/descriptor',
  'team/member',
  'team/task',
  'team/message/queued',
  'team/message/delivered',
  'image/offload',
  'schedule/change',
  'compaction/start',
  'compaction/summary',
  'compaction/end',
  'compaction/prune',
])

/**
 * Start a loopback-only, bearer-authenticated session bridge.
 * @param {{sessionController: object, workspaceController?: object, taskInteractions?: object, config?: object, logger?: object}} options
 */
export async function startSessionBridge({
  sessionController,
  workspaceController,
  taskInteractions,
  config = {},
  logger = console,
}) {
  assertSessionController(sessionController)
  const normalized = normalizeConfig(config)
  const token = randomBytes(32).toString('base64url')
  const startedAt = new Date().toISOString()
  const sockets = new Set()
  const commandCoordinator = createDeviceCommandCoordinator(sessionController, normalized, logger)
  const sessionCommandCoordinator = createSessionCommandCoordinator(sessionController, normalized)
  const deviceSessionClassifier = createDeviceSessionClassifier(sessionController, normalized, logger)
  const taskCoordinator = createTaskCoordinator({
    sessionController,
    workspaceController,
    taskInteractions,
    commandCoordinator,
    config: normalized,
    logger,
  })

  const server = createServer((request, response) => {
    void handleRequest({
      request,
      response,
      token,
      sessionController,
      workspaceController,
      commandCoordinator,
      sessionCommandCoordinator,
      deviceSessionClassifier,
      taskCoordinator,
      config: normalized,
      startedAt,
    }).catch((error) => {
      if (response.headersSent || response.writableEnded) {
        response.destroy(error instanceof Error ? error : undefined)
        return
      }
      logger?.warn?.(safeError(error))
      sendError(response, 500, 'internal_error', 'The DSH session bridge could not complete the request.')
    })
  })

  server.on('connection', (socket) => {
    sockets.add(socket)
    socket.once('close', () => sockets.delete(socket))
  })

  await listen(server, normalized.port)
  const address = server.address()
  if (address === null || typeof address === 'string') {
    await closeServer(server, sockets)
    throw new Error('halo-session-bridge: HTTP server did not expose a TCP port')
  }

  const baseUrl = `http://${LOOPBACK_HOST}:${address.port}/`
  const descriptor = Object.freeze({
    protocolVersion: PROTOCOL_VERSION,
    home: normalized.home,
    profile: normalized.profile,
    pid: process.pid,
    baseUrl,
    token,
    createdAt: startedAt,
    ...(normalized.webUrl === undefined ? {} : { webUrl: normalized.webUrl }),
    ...(normalized.ownedStartupId === undefined ? {} : { ownedStartupId: normalized.ownedStartupId }),
  })
  const descriptorPath = join(normalized.discoveryDirectory, `${process.pid}.json`)

  try {
    await writeDescriptor(descriptorPath, descriptor)
  } catch (error) {
    await closeServer(server, sockets)
    throw error
  }

  const onProcessExit = () => removeOwnedDescriptorSync(descriptorPath, token)
  process.once('exit', onProcessExit)
  logger?.info?.(`Halo session bridge listening at ${baseUrl}; descriptor ${descriptorPath}`)
  let closed = false
  return {
    descriptor,
    descriptorPath,
    async close() {
      if (closed) return
      closed = true
      process.off('exit', onProcessExit)
      taskCoordinator.close()
      await removeOwnedDescriptor(descriptorPath, token)
      await closeServer(server, sockets)
    },
  }
}

export function normalizeSessionSummary(
  summary,
  deviceControlKind = isCanonicalDeviceSession(summary) ? 'canonical' : null,
  isArchived = false,
) {
  const title = summary?.projections?.values?.title
  const updatedAt = toIsoTime(summary?.updatedAt)
  return {
    id: typeof summary?.sessionId === 'string' ? summary.sessionId : String(summary?.sessionId ?? ''),
    title: typeof title === 'string' && title.trim().length > 0
      ? truncateUtf8(title.trim(), 1024).text
      : null,
    workingDirectory: typeof summary?.cwd === 'string'
      ? truncateUtf8(summary.cwd, 4096).text
      : null,
    updatedAt,
    runtimeStatus: runtimeStatus(summary),
    isDeviceControl: deviceControlKind !== null,
    deviceControlKind,
    isArchived,
  }
}

export function normalizeHistoryPage(
  page,
  maxEntryTextBytes = DEFAULT_MAX_ENTRY_TEXT_BYTES,
  maxPageTextBytes = DEFAULT_MAX_PAGE_TEXT_BYTES,
  sessionId = '',
) {
  const records = Array.isArray(page?.records) ? page.records : []
  const surface = foldSurfaceRecords(records)
  const entries = []
  let pageBytes = 0
  let pageTruncated = false
  let truncatedCursor

  // History is paged backwards but returned chronologically. Retain the newest
  // bounded suffix so a total-byte cut cannot silently skip later messages.
  for (let index = surface.length - 1; index >= 0; index -= 1) {
    const event = surface[index]
    const entry = eventToEntry(event, maxEntryTextBytes, sessionId)
    if (entry === null) continue

    const available = maxPageTextBytes - pageBytes
    if (available <= 0) {
      pageTruncated = true
      break
    }
    const bounded = truncateUtf8(entry.text, available)
    if (bounded.text.length === 0 && entry.text.length > 0) {
      pageTruncated = true
      break
    }
    entries.unshift({
      ...entry,
      text: bounded.text,
      truncated: entry.truncated || bounded.truncated,
    })
    truncatedCursor = eventCursor(event)
    pageBytes += Buffer.byteLength(bounded.text, 'utf8')
    if (bounded.truncated) {
      pageTruncated = true
      break
    }
  }

  const firstSequence = records
    .map(record => record?.event?.seq)
    .find(sequence => Number.isSafeInteger(sequence) && sequence >= 0)

  return {
    entries,
    beforeSeq: pageTruncated ? (truncatedCursor ?? firstSequence ?? null) : (firstSequence ?? null),
    hasMore: Boolean(page?.hasMore) || pageTruncated,
    truncated: pageTruncated || entries.some(entry => entry.truncated),
  }
}

/**
 * Identify canonical and legacy PixelBar control Sessions without activating
 * an Agent. Canonical sessions are projection-only. Legacy headless sessions
 * are discovered incrementally from a small tail page containing the exact
 * historical voice wrapper; classification is cached by durable update key.
 */
export function createDeviceSessionClassifier(sessionController, config = {}, logger = console) {
  const cache = new Map()
  const persistentCache = createPersistentClassificationCache(config, logger)
  let queueTail = Promise.resolve()
  let persistenceDirty = false
  const now = typeof config.now === 'function' ? config.now : Date.now
  const maximumProbes = Number.isSafeInteger(config.maxLegacySessionProbesPerList)
    ? config.maxLegacySessionProbesPerList
    : DEFAULT_LEGACY_SESSION_PROBES_PER_LIST
  const maximumInspects = Number.isSafeInteger(config.maxLegacySessionInspectsPerList)
    ? config.maxLegacySessionInspectsPerList
    : DEFAULT_LEGACY_SESSION_INSPECTS_PER_LIST

  const classifyOnce = async (summaries, signal) => {
    await persistentCache.load(cache)
    const startedAt = now()
    const result = new Map()
    const presentIds = new Set()
    let remainingProbes = maximumProbes
    const inspectBudget = { remaining: maximumInspects }
    let dirty = false

    const remember = (sessionId, summary, value) => {
      // Without either revision marker there is no way to prove that the
      // Session history still matches a previous classification. Return the
      // result for this listing, but force a fresh read on the next listing.
      if (!hasSessionClassificationRevision(summary)) return
      const next = classificationCacheEntry(summary, value)
      const current = cache.get(sessionId)
      if (!sameClassificationCacheEntry(current, next)) dirty = true
      cache.set(sessionId, next)
    }

    for (const summary of summaries) {
      if (signal?.aborted) throw signal.reason ?? new Error('session classification cancelled')
      const sessionId = sessionSummaryId(summary)
      if (sessionId.length === 0) continue
      presentIds.add(sessionId)

      if (isCanonicalDeviceSession(summary)) {
        remember(sessionId, summary, 'canonical')
        result.set(sessionId, 'canonical')
        continue
      }

      const key = sessionClassificationKey(summary)
      const cached = hasSessionClassificationRevision(summary) ? cache.get(sessionId) : undefined
      if (cached?.key === key) {
        result.set(sessionId, cached.value)
        continue
      }

      if (summary?.blank === true) {
        remember(sessionId, summary, null)
        result.set(sessionId, null)
        continue
      }

      // This is a soft wall-clock guard: never abort an in-flight Controller
      // read, but do not start another cold probe after the list round expires.
      // Deferred IDs stay absent from the result and cache so the next refresh
      // can continue from them. Cached and projection-only rows above remain
      // cheap and are still returned after the deadline.
      if (remainingProbes <= 0 || now() - startedAt >= DEFAULT_LEGACY_CLASSIFICATION_BUDGET_MS) continue
      remainingProbes -= 1
      try {
        const classified = await probeDeviceSession(
          sessionController,
          summary,
          sessionId,
          inspectBudget,
          signal,
        )
        if (classified !== undefined) {
          remember(sessionId, summary, classified)
          result.set(sessionId, classified)
        }
      } catch (error) {
        if (signal?.aborted) throw error
        logger?.warn?.(`halo-session-bridge: device session classification failed: ${safeError(error)}`)
        result.set(sessionId, null)
      }
    }

    for (const sessionId of cache.keys()) {
      if (!presentIds.has(sessionId)) {
        cache.delete(sessionId)
        dirty = true
      }
    }
    if (dirty) persistenceDirty = true
    if (persistenceDirty) persistenceDirty = !(await persistentCache.save(cache))
    return result
  }

  return {
    classify(summaries, signal) {
      const task = queueTail.then(() => classifyOnce(
        Array.isArray(summaries) ? summaries : [],
        signal,
      ))
      queueTail = task.catch(() => {})
      return task
    },
  }
}

function classificationCacheEntry(summary, value) {
  const updatedAt = Number.isFinite(summary?.updatedAt) ? Number(summary.updatedAt) : null
  const projectionCursor = Number.isSafeInteger(summary?.projections?.asOfSeq)
    ? summary.projections.asOfSeq
    : null
  return {
    key: sessionClassificationKey(summary),
    value,
    updatedAt,
    projectionCursor,
  }
}

function sameClassificationCacheEntry(left, right) {
  return left?.key === right?.key && left?.value === right?.value
}

function createPersistentClassificationCache(config, logger) {
  const path = typeof config.classificationCachePath === 'string'
    && config.classificationCachePath.length > 0
    ? config.classificationCachePath
    : undefined
  const scope = typeof config.classificationCacheScope === 'string'
    && /^[a-f0-9]{64}$/u.test(config.classificationCacheScope)
    ? config.classificationCacheScope
    : undefined
  let loadPromise

  const load = async cache => {
    if (path === undefined || scope === undefined) return
    if (loadPromise === undefined) {
      loadPromise = (async () => {
        try {
          const stored = await readClassificationCache(path, scope)
          for (const [sessionId, entry] of stored) cache.set(sessionId, entry)
        } catch (error) {
          logger?.warn?.(`halo-session-bridge: classification cache read failed: ${safeError(error)}`)
        }
      })()
    }
    await loadPromise
  }

  const save = async cache => {
    if (path === undefined || scope === undefined) return true
    try {
      await withClassificationCacheLock(path, async () => {
        let stored = new Map()
        try {
          stored = await readClassificationCache(path, scope)
        } catch (error) {
          logger?.warn?.(`halo-session-bridge: classification cache merge read failed: ${safeError(error)}`)
        }
        const merged = mergeClassificationCaches(stored, cache)
        await writeClassificationCache(path, scope, merged)
      })
      return true
    } catch (error) {
      // Cache persistence is an optimization. Permission, corruption, and
      // lock contention must never fail the authenticated list endpoint.
      logger?.warn?.(`halo-session-bridge: classification cache write skipped: ${safeError(error)}`)
      return false
    }
  }

  return { load, save }
}

async function readClassificationCache(path, scope) {
  let text
  try {
    text = await readFile(path, 'utf8')
  } catch (error) {
    if (error?.code === 'ENOENT') return new Map()
    throw error
  }
  if (Buffer.byteLength(text, 'utf8') > MAX_CLASSIFICATION_CACHE_BYTES) {
    throw new Error('classification cache exceeds its size limit')
  }
  let document
  try {
    document = JSON.parse(text)
  } catch {
    throw new Error('classification cache is not valid JSON')
  }
  if (document?.schemaVersion !== CLASSIFICATION_CACHE_SCHEMA_VERSION
    || document?.ruleVersion !== CLASSIFICATION_RULE_VERSION
    || document?.scope !== scope) return new Map()
  if (!Array.isArray(document.entries)
    || document.entries.length > MAX_CLASSIFICATION_CACHE_ENTRIES) {
    throw new Error('classification cache entries are invalid')
  }

  const result = new Map()
  for (const candidate of document.entries) {
    const entry = parseClassificationCacheEntry(candidate)
    if (entry !== undefined) result.set(candidate.sessionId, entry)
  }
  return result
}

function parseClassificationCacheEntry(candidate) {
  if (candidate === null || typeof candidate !== 'object'
    || typeof candidate.sessionId !== 'string'
    || candidate.sessionId.length === 0
    || candidate.sessionId.length > 256
    || typeof candidate.key !== 'string'
    || !/^[a-f0-9]{64}$/u.test(candidate.key)
    || !['canonical', 'legacy', null].includes(candidate.value)
    || !(candidate.updatedAt === null || Number.isFinite(candidate.updatedAt))
    || !(candidate.projectionCursor === null
      || Number.isSafeInteger(candidate.projectionCursor))) return undefined
  return {
    key: candidate.key,
    value: candidate.value,
    updatedAt: candidate.updatedAt,
    projectionCursor: candidate.projectionCursor,
  }
}

function mergeClassificationCaches(stored, current) {
  const merged = new Map(stored)
  for (const [sessionId, entry] of current) {
    const previous = merged.get(sessionId)
    merged.set(sessionId, fresherClassificationCacheEntry(previous, entry))
  }
  if (merged.size <= MAX_CLASSIFICATION_CACHE_ENTRIES) return merged
  return new Map([...merged]
    .sort((left, right) => compareClassificationCacheRecency(left[1], right[1]))
    .slice(0, MAX_CLASSIFICATION_CACHE_ENTRIES))
}

function compareClassificationCacheRecency(left, right) {
  const leftRecency = classificationCacheRecency(left)
  const rightRecency = classificationCacheRecency(right)
  if (leftRecency === rightRecency) return 0
  return rightRecency > leftRecency ? 1 : -1
}

function fresherClassificationCacheEntry(left, right) {
  if (left === undefined) return right
  if (right === undefined) return left
  const leftUpdated = classificationCacheRecency(left)
  const rightUpdated = classificationCacheRecency(right)
  if (leftUpdated !== rightUpdated) return rightUpdated > leftUpdated ? right : left
  const leftCursor = left.projectionCursor ?? -1
  const rightCursor = right.projectionCursor ?? -1
  if (leftCursor !== rightCursor) return rightCursor > leftCursor ? right : left
  if (left.key === right.key) {
    return classificationKindRank(right.value) >= classificationKindRank(left.value) ? right : left
  }
  return classificationKindRank(right.value) > classificationKindRank(left.value) ? right : left
}

function classificationCacheRecency(entry) {
  return Number.isFinite(entry?.updatedAt) ? entry.updatedAt : Number.NEGATIVE_INFINITY
}

function classificationKindRank(value) {
  if (value === 'canonical') return 2
  if (value === 'legacy') return 1
  return 0
}

async function writeClassificationCache(path, scope, entries) {
  const document = {
    schemaVersion: CLASSIFICATION_CACHE_SCHEMA_VERSION,
    ruleVersion: CLASSIFICATION_RULE_VERSION,
    scope,
    entries: [...entries]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([sessionId, entry]) => ({ sessionId, ...entry })),
  }
  const body = `${JSON.stringify(document)}\n`
  if (Buffer.byteLength(body, 'utf8') > MAX_CLASSIFICATION_CACHE_BYTES) {
    throw new Error('classification cache serialization exceeds its size limit')
  }
  await mkdir(dirname(path), { recursive: true, mode: 0o700 })
  const temporary = `${path}.${process.pid}.${randomBytes(8).toString('hex')}.tmp`
  try {
    await writeFile(temporary, body, { encoding: 'utf8', mode: 0o600, flag: 'wx' })
    await rename(temporary, path)
    await chmod(path, 0o600).catch(() => {})
  } finally {
    await rm(temporary, { force: true }).catch(() => {})
  }
}

async function withClassificationCacheLock(path, operation) {
  await mkdir(dirname(path), { recursive: true, mode: 0o700 })
  const lockPath = `${path}.lock`
  let handle
  for (let attempt = 0; attempt < CLASSIFICATION_CACHE_LOCK_ATTEMPTS; attempt += 1) {
    try {
      handle = await open(lockPath, 'wx', 0o600)
      break
    } catch (error) {
      if (error?.code !== 'EEXIST') throw error
      await reclaimStaleClassificationCacheLock(lockPath)
      await new Promise(resolvePromise => setTimeout(resolvePromise, 20 * (attempt + 1)))
    }
  }
  if (handle === undefined) throw new Error('classification cache is busy')
  try {
    await operation()
  } finally {
    await handle.close().catch(() => {})
    await rm(lockPath, { force: true }).catch(() => {})
  }
}

async function reclaimStaleClassificationCacheLock(lockPath) {
  let details
  try {
    details = await stat(lockPath)
  } catch (error) {
    if (error?.code === 'ENOENT') return
    throw error
  }
  if (Date.now() - details.mtimeMs < CLASSIFICATION_CACHE_LOCK_STALE_MS) return
  const reaperPath = `${lockPath}.reap`
  let reaper
  try {
    reaper = await open(reaperPath, 'wx', 0o600)
  } catch (error) {
    if (error?.code === 'EEXIST') {
      const reaperDetails = await stat(reaperPath).catch(() => undefined)
      if (reaperDetails !== undefined
        && Date.now() - reaperDetails.mtimeMs >= CLASSIFICATION_CACHE_LOCK_STALE_MS) {
        await rm(reaperPath, { force: true }).catch(() => {})
      }
      return
    }
    throw error
  }
  try {
    const current = await stat(lockPath).catch(() => undefined)
    if (current !== undefined
      && Date.now() - current.mtimeMs >= CLASSIFICATION_CACHE_LOCK_STALE_MS) {
      await rm(lockPath, { force: true })
    }
  } finally {
    await reaper.close().catch(() => {})
    await rm(reaperPath, { force: true }).catch(() => {})
  }
}

async function probeDeviceSession(sessionController, summary, sessionId, inspectBudget, signal) {
  let projections = summary?.projections
  const projectedValues = projections?.values
  if ((!projectedValues || !Object.hasOwn(projectedValues, 'agentPreset'))
    && typeof sessionController.projections === 'function') {
    try {
      projections = await sessionController.projections({ sessionId }, signal) ?? projections
    } catch (error) {
      if (signal?.aborted) throw error
    }
  }
  if (projections?.values?.agentPreset === DEVICE_AGENT_PRESET) return 'canonical'

  const throughSeq = projections?.asOfSeq
  if (Number.isSafeInteger(throughSeq)) {
    if (throughSeq < 0) return null
    const page = await sessionController.page({
      address: { kind: 'session', sessionId },
      throughSeq,
      maxMessages: LEGACY_CLASSIFIER_PAGE_MESSAGES,
    }, signal)
    if (containsLegacyVoicePrompt(page?.records)) return 'legacy'
  }

  if (inspectBudget.remaining <= 0) return undefined
  inspectBudget.remaining -= 1
  const inspection = await sessionController.inspect(sessionId, signal)
  if (inspection?.meta?.agentPreset === DEVICE_AGENT_PRESET) return 'canonical'
  const events = Array.isArray(inspection?.events) ? inspection.events.slice(-256) : []
  if (containsLegacyVoicePrompt(events.map(event => ({ type: 'event', event })))) return 'legacy'
  return isHighConfidenceManualDeviceHistory(inspection?.events) ? 'legacy' : null
}

function containsLegacyVoicePrompt(records) {
  const surface = foldSurfaceRecords(Array.isArray(records) ? records : [])
  return surface.some(event => {
    if (event.type !== 'user/message') return false
    const message = event.data?.message ?? event.data
    const text = contentText(message?.content)
    if (text.startsWith(LEGACY_VOICE_PROMPT_PREFIX)
      && text.slice(LEGACY_VOICE_PROMPT_PREFIX.length).trim().length > 0) return true

    // Four early voice-path probe Sessions used one fixed, whole-message
    // prompt without a command delimiter or suffix. Match its audited exact
    // fingerprint rather than a title or the broad identity sentence.
    if (!text.startsWith(LEGACY_VOICE_IDENTITY_PREFIX)) return false
    const bytes = Buffer.byteLength(text, 'utf8')
    if (bytes !== 244) return false
    const digest = createHash('sha256').update(text, 'utf8').digest('hex')
    return LEGACY_VOICE_EXACT_PROMPT_FINGERPRINTS.has(`${bytes}:${digest}`)
  })
}

/**
 * Classify a former manual DSH control Session only from its complete cold
 * inspection. One known Halo tool call is required, every observed tool call
 * must be from that exact allow-list, and durable workflow evidence for coding
 * or another task rejects the Session. Titles and prose are intentionally not
 * used as positive evidence.
 */
function isHighConfidenceManualDeviceHistory(events) {
  if (!Array.isArray(events) || events.length === 0) return false
  let sawPixelBarTool = false

  for (const event of events) {
    if (event === null || typeof event !== 'object') continue
    if (MANUAL_DEVICE_DISQUALIFYING_EVENTS.has(event.type)) return false
    if (event.type === 'user/message' && userMessageHasNonTextContent(event)) return false

    for (const toolName of eventToolCallNames(event)) {
      if (!PIXELBAR_TOOL_NAMES.has(toolName)) return false
      sawPixelBarTool = true
    }
  }
  return sawPixelBarTool
}

function eventToolCallNames(event) {
  if (event.type === 'tool/call') {
    return [typeof event.data?.name === 'string' ? event.data.name : null]
  }
  if (event.type !== 'assistant/message') return []
  const message = event.data?.message ?? event.data
  if (!Array.isArray(message?.content)) return []
  return message.content
    .filter(block => block?.type === 'tool-call')
    .map(block => typeof block.name === 'string' ? block.name : null)
}

function userMessageHasNonTextContent(event) {
  const message = event.data?.message ?? event.data
  if (typeof message?.content === 'string' || message?.content === undefined) return false
  if (!Array.isArray(message.content)) return true
  return message.content.some(block => block?.type !== 'text')
}

function sessionSummaryId(summary) {
  return typeof summary?.sessionId === 'string'
    ? summary.sessionId
    : String(summary?.sessionId ?? '')
}

function sessionClassificationKey(summary) {
  const projections = summary?.projections
  const updatedAt = Number.isFinite(summary?.updatedAt) ? Number(summary.updatedAt) : null
  const projectionCursor = Number.isSafeInteger(projections?.asOfSeq)
    ? projections.asOfSeq
    : null
  const rawAgentPreset = projections?.values?.agentPreset ?? summary?.agentPreset
  const agentPreset = typeof rawAgentPreset === 'string' ? rawAgentPreset : null
  return createHash('sha256')
    .update(JSON.stringify([updatedAt, projectionCursor, agentPreset]), 'utf8')
    .digest('hex')
}

function hasSessionClassificationRevision(summary) {
  return Number.isFinite(summary?.updatedAt)
    || Number.isSafeInteger(summary?.projections?.asOfSeq)
}

function isCanonicalDeviceSession(summary) {
  return (summary?.projections?.values?.agentPreset ?? summary?.agentPreset) === DEVICE_AGENT_PRESET
}

/**
 * Answerer owned only by explicitly managed long-task Sessions. Every live
 * request keeps its own deferred identity; ordinary chat text is never
 * interpreted as either a question answer or an approval decision.
 */
export function createTaskInteractionBroker({
  agents,
  userQuestions,
  now = () => Date.now(),
  idFactory = randomUUID,
} = {}) {
  const managed = new Set()
  const pending = new Map()
  const revisions = new Map()
  const queuedContinuedReplies = new Set()
  const continuedRepliesInFlight = new Set()
  let closed = false

  const bump = sessionId => {
    revisions.set(sessionId, (revisions.get(sessionId) ?? 0) + 1)
  }

  const remove = interaction => {
    if (pending.get(interaction.id) !== interaction) return
    pending.delete(interaction.id)
    interaction.signal?.removeEventListener('abort', interaction.onAbort)
    bump(interaction.sessionId)
  }

  const defer = interaction => new Promise((resolve, reject) => {
    interaction.resolve = value => {
      remove(interaction)
      resolve(value)
    }
    interaction.reject = error => {
      remove(interaction)
      reject(error)
    }
    interaction.onAbort = () => {
      if (interaction.type === 'approval') interaction.resolve('cancelled')
      else interaction.reject(interaction.signal?.reason ?? new Error('user question was cancelled'))
    }
    pending.set(interaction.id, interaction)
    interaction.signal?.addEventListener('abort', interaction.onAbort, { once: true })
    bump(interaction.sessionId)
    if (interaction.signal?.aborted) interaction.onAbort()
  })

  return {
    get continuedQuestionsSupported() {
      return typeof userQuestions?.answer === 'function' && typeof agents?.get === 'function'
    },
    adopt(sessionId) {
      if (closed) throw new HttpError(503, 'task_interactions_closed', 'Task interactions are no longer available.')
      managed.add(sessionId)
    },
    isManaged(sessionId) {
      return managed.has(sessionId)
    },
    release(sessionId) {
      managed.delete(sessionId)
      for (const interaction of [...pending.values()]) {
        if (interaction.sessionId !== sessionId) continue
        if (interaction.type === 'approval') interaction.resolve('cancelled')
        else interaction.reject(new Error('task interaction monitoring was released'))
      }
      bump(sessionId)
    },
    revision(sessionId) {
      return revisions.get(sessionId) ?? 0
    },
    handleApproval(request, next) {
      const sessionId = interactionSessionId(request?.agent)
      if (closed || sessionId === undefined || !managed.has(sessionId)) return next()
      const interaction = {
        id: `approval:${idFactory()}`,
        type: 'approval',
        sessionId,
        createdAt: new Date(now()).toISOString(),
        toolName: boundedInteractionText(request?.toolName),
        callId: optionalInteractionText(request?.callId),
        reason: optionalInteractionText(
          request?.displayReason?.zh
          ?? request?.displayReason?.en
          ?? request?.reason,
        ),
        signal: request?.signal,
      }
      return defer(interaction)
    },
    handleQuestion(request, next) {
      const sessionId = interactionSessionId(request?.agent)
      if (closed || sessionId === undefined || !managed.has(sessionId)) return next()
      const callId = optionalInteractionCallId(request?.wait?.callId)
      const id = callId === undefined ? `question:${idFactory()}` : `question:${callId}`
      if (pending.has(id)) return Promise.reject(new Error('duplicate live user question identity'))
      const questions = normalizeInteractionQuestions(request?.questions)
      const interaction = {
        id,
        type: 'question',
        sessionId,
        callId: callId ?? null,
        state: 'open',
        createdAt: new Date(now()).toISOString(),
        questions,
        signal: request?.signal,
      }
      return defer(interaction)
    },
    list(sessionId, projectedActive = []) {
      if (!managed.has(sessionId)) return []
      const activeCallIds = new Set()
      const live = [...pending.values()]
        .filter(interaction => interaction.sessionId === sessionId)
        .map(publicInteraction)
      const liveIds = new Set(live.map(interaction => interaction.id))
      const continued = []
      for (const value of Array.isArray(projectedActive) ? projectedActive : []) {
        const callId = optionalInteractionCallId(value?.callId)
        if (callId === undefined || value?.state !== 'continued') continue
        activeCallIds.add(callId)
        const id = `question:${callId}`
        if (liveIds.has(id)) continue
        const queuedKey = `${sessionId}\0${id}`
        if (queuedContinuedReplies.has(queuedKey)) continue
        continued.push({
          id,
          type: 'question',
          sessionId,
          callId,
          state: 'continued',
          createdAt: null,
          questions: normalizeInteractionQuestions(value.questions),
          answerable: this.continuedQuestionsSupported,
          replyQueued: false,
        })
      }
      const queuedPrefix = `${sessionId}\0question:`
      for (const key of [...queuedContinuedReplies]) {
        if (key.startsWith(queuedPrefix) && !activeCallIds.has(key.slice(queuedPrefix.length))) {
          queuedContinuedReplies.delete(key)
        }
      }
      return [...live, ...continued]
    },
    async respond(input, projectedActive, ensureAgent) {
      const sessionId = requiredSessionId(input?.sessionId)
      if (closed || !managed.has(sessionId)) {
        throw new HttpError(409, 'task_interactions_released', 'Task interaction ownership has been released.')
      }
      const interactionId = optionalBoundedString(input?.interactionId, 'interactionId', 512)
      if (interactionId === undefined) {
        throw new HttpError(400, 'invalid_interaction_id', 'interactionId is required.')
      }
      const type = input?.type
      if (type !== 'approval' && type !== 'question') {
        throw new HttpError(400, 'invalid_interaction_type', 'type must be approval or question.')
      }
      const live = pending.get(interactionId)
      if (live !== undefined) {
        if (live.sessionId !== sessionId || live.type !== type) {
          throw new HttpError(409, 'interaction_identity_mismatch', 'The interaction identity does not match this Session and type.')
        }
        if (type === 'approval') {
          const outcome = requiredApprovalOutcome(input.outcome)
          live.resolve(outcome)
          return { accepted: true, sessionId, interactionId, type, outcome }
        }
        const answer = answerBatchFromMap(input.answers, live.questions)
        live.resolve(answer)
        return { accepted: true, sessionId, interactionId, type }
      }

      if (type === 'approval') {
        throw new HttpError(409, 'interaction_not_pending', 'The approval request is no longer pending; approvals cannot be answered from audit history.')
      }
      const callId = interactionId.startsWith('question:')
        ? interactionId.slice('question:'.length)
        : ''
      const continued = (Array.isArray(projectedActive) ? projectedActive : [])
        .find(value => value?.state === 'continued' && String(value?.callId ?? '') === callId)
      if (continued === undefined) {
        throw new HttpError(409, 'interaction_not_pending', 'The user question is no longer pending.')
      }
      if (!this.continuedQuestionsSupported) {
        throw new HttpError(501, 'continued_question_response_not_supported', 'This DSH runtime cannot answer a continued user question.')
      }
      const queuedKey = `${sessionId}\0${interactionId}`
      if (queuedContinuedReplies.has(queuedKey) || continuedRepliesInFlight.has(queuedKey)) {
        throw new HttpError(409, 'interaction_reply_queued', 'A reply is already queued for this user question.')
      }
      const answer = answerBatchFromMap(input.answers, normalizeInteractionQuestions(continued.questions))
      // Claim this interaction before either await. UI and microphone replies
      // must not both enter DSH while attachment or acknowledgement is pending.
      continuedRepliesInFlight.add(queuedKey)
      try {
        let agent = agents.get(sessionId)
        if (agent === undefined) {
          await ensureAgent()
          agent = agents.get(sessionId)
        }
        if (!managed.has(sessionId)) {
          throw new HttpError(409, 'task_interactions_released', 'Task interaction ownership was released before the reply could be queued.')
        }
        if (agent === undefined) {
          throw new HttpError(409, 'task_session_unavailable', 'DSH did not attach the task Session for the continued answer.')
        }
        const accepted = await userQuestions.answer(agent, callId, answer)
        if (accepted !== true) {
          throw new HttpError(409, 'interaction_not_pending', 'The user question is no longer continued.')
        }
        queuedContinuedReplies.add(queuedKey)
        bump(sessionId)
        return { accepted: true, sessionId, interactionId, type }
      } finally {
        continuedRepliesInFlight.delete(queuedKey)
      }
    },
    close() {
      if (closed) return
      closed = true
      for (const interaction of [...pending.values()]) {
        if (interaction.type === 'approval') interaction.resolve('cancelled')
        else interaction.reject(new Error('task interaction bridge closed'))
      }
      managed.clear()
      queuedContinuedReplies.clear()
      continuedRepliesInFlight.clear()
    },
  }
}

function createTaskCoordinator({
  sessionController,
  workspaceController,
  taskInteractions,
  commandCoordinator,
  config,
  logger,
}) {
  const managed = new Set()
  const metadata = new Map()
  const statusRevisions = new Map()
  const promptDedupe = new Map()
  const verifiedOrdinary = new Set()
  let closed = false

  const assertManaged = sessionId => {
    if (!managed.has(sessionId)) {
      throw new HttpError(409, 'task_not_adopted', 'Create or explicitly adopt this ordinary Session before using task endpoints.')
    }
  }
  const markManaged = (sessionId, hasSubmittedPrompt = false) => {
    managed.add(sessionId)
    verifiedOrdinary.add(sessionId)
    const existing = metadata.get(sessionId)
    metadata.set(sessionId, {
      hasSubmittedPrompt: existing?.hasSubmittedPrompt === true || hasSubmittedPrompt,
      lastPromptRequestId: existing?.lastPromptRequestId ?? null,
      promptAfterSeq: existing?.promptAfterSeq ?? null,
      promptEventSeq: existing?.promptEventSeq ?? null,
      promptSearchBeforeSeq: existing?.promptSearchBeforeSeq ?? null,
      promptSearchThroughSeq: existing?.promptSearchThroughSeq ?? null,
      promptSearchedThroughSeq: existing?.promptSearchedThroughSeq ?? null,
      promptSearchExhausted: existing?.promptSearchExhausted === true,
      targetTurn: existing?.targetTurn ?? null,
    })
    taskInteractions?.adopt(sessionId)
  }

  const sessionFacts = async (sessionId, signal) => {
    const summary = await findSessionSummary(sessionController, sessionId, signal)
    if (summary === undefined) throw new HttpError(404, 'session_not_found', 'The requested task Session does not exist.')
    if (summary?.origin === 'subagent') {
      throw new HttpError(409, 'task_session_not_ordinary', 'Subagent Sessions cannot be adopted as top-level voice tasks.')
    }
    if (commandCoordinator.sessionId === sessionId || isCanonicalDeviceSession(summary)) {
      throw new HttpError(409, 'task_session_is_device_control', 'Halo device control Sessions cannot be adopted or controlled as long-running tasks.')
    }
    if (!verifiedOrdinary.has(sessionId) && summary?.blank !== true) {
      const kind = await probeDeviceSession(
        sessionController,
        summary,
        sessionId,
        { remaining: 1 },
        signal,
      )
      if (kind !== null) {
        throw new HttpError(409, 'task_session_is_device_control', 'A Session with verified Halo device-control history cannot be adopted as a long-running task.')
      }
    }
    const archivedIds = await readArchivedSessionIds(workspaceController, signal)
    if (archivedIds.has(sessionId)) {
      throw new HttpError(409, 'task_session_archived', 'Restore the Session before adopting or controlling it as a task.')
    }
    return summary
  }

  const readProjection = async (sessionId, signal) => {
    if (typeof sessionController.projections !== 'function') return undefined
    return sessionController.projections({ sessionId }, signal)
  }

  const readTail = async (sessionId, signal, maxMessages = 100) => {
    let iterator
    try {
      iterator = sessionController.follow({
        address: { kind: 'session', sessionId },
        maxMessages,
      }, signal)[Symbol.asyncIterator]()
      const opening = await iterator.next()
      if (opening.done || opening.value?.type !== 'snapshot') {
        throw new Error('DSH follow did not return an opening snapshot for the task Session')
      }
      return opening.value
    } finally {
      await iterator?.return?.().catch(() => {})
    }
  }

  const ensureAttached = async (sessionId, summary, signal) => {
    const preset = summary?.projections?.values?.agentPreset ?? summary?.agentPreset
    let cwd = typeof summary?.cwd === 'string' && summary.cwd.length > 0 ? summary.cwd : undefined
    let agentPreset = typeof preset === 'string' && preset.length > 0 ? preset : undefined
    if ((cwd === undefined || agentPreset === undefined) && typeof sessionController.inspect === 'function') {
      const inspection = await sessionController.inspect(sessionId, signal)
      cwd ??= typeof inspection?.meta?.cwd === 'string' ? inspection.meta.cwd : undefined
      agentPreset ??= typeof inspection?.meta?.agentPreset === 'string' ? inspection.meta.agentPreset : undefined
    }
    if (cwd === undefined || agentPreset === undefined) {
      throw new HttpError(409, 'task_session_unavailable', 'The task Session has no verifiable working directory or Agent preset.')
    }
    await sessionController.create({ sessionId, cwd, agentPreset })
  }

  return {
    capabilities: {
      taskCreate: typeof sessionController.create === 'function',
      taskPrompt: typeof sessionController.prompt === 'function'
        && typeof sessionController.follow === 'function',
      taskMonitor: typeof sessionController.follow === 'function',
      taskRespond: taskInteractions !== undefined,
      taskCancel: typeof sessionController.cancel === 'function',
      taskAdopt: typeof sessionController.list === 'function',
      taskRelease: true,
      continuedQuestionResponse: taskInteractions?.continuedQuestionsSupported === true,
    },
    async create(body, signal) {
      if (closed) throw new HttpError(503, 'task_bridge_closed', 'Task management is no longer available.')
      if (typeof sessionController.create !== 'function') {
        throw new HttpError(501, 'task_create_not_supported', 'This DSH runtime does not expose Session creation.')
      }
      const title = requiredTitle(body?.title)
      const baseDirectory = requiredTaskBaseDirectory(body?.baseDirectory)
      const directoryName = requiredTaskDirectoryName(body?.directoryName)
      const taskDirectory = resolve(baseDirectory, directoryName)
      if (dirname(taskDirectory).toLowerCase() !== resolve(baseDirectory).toLowerCase()) {
        throw new HttpError(400, 'invalid_task_directory', 'directoryName must resolve to one direct child of baseDirectory.')
      }
      await mkdir(baseDirectory, { recursive: true })
      try {
        await mkdir(taskDirectory, { recursive: false })
      } catch (error) {
        if (error?.code === 'EEXIST') {
          throw new HttpError(409, 'task_directory_exists', 'The task directory already exists and will not be overwritten.')
        }
        throw error
      }

      let created
      try {
        created = await sessionController.create({ cwd: taskDirectory, agentPreset: config.taskAgentPreset })
      } catch (error) {
        // A transport error does not prove DSH rejected creation. Preserve the
        // exclusive directory so a possibly accepted Session never loses its cwd.
        throw error
      }
      const sessionId = requiredReturnedSessionId(created?.sessionId)
      markManaged(sessionId, false)
      if (typeof sessionController.rename === 'function') {
        try {
          await sessionController.rename({ sessionId, title })
        } catch (error) {
          logger?.warn?.(`halo-session-bridge: could not name task session: ${safeError(error)}`)
        }
      }
      let projected
      try {
        projected = await findSessionSummary(sessionController, sessionId, signal)
      } catch (error) {
        logger?.warn?.(`halo-session-bridge: could not project new task session: ${safeError(error)}`)
      }
      const normalized = normalizeSessionSummary(projected === undefined
        ? {
            sessionId,
            cwd: taskDirectory,
            agentAvailable: true,
            running: false,
            projections: { values: { title, agentPreset: created?.agentPreset ?? config.taskAgentPreset } },
          }
        : projected)
      return {
        session: {
          ...normalized,
          title: normalized.title.length > 0 ? normalized.title : title,
          workingDirectory: normalized.workingDirectory.length > 0
            ? normalized.workingDirectory
            : taskDirectory,
        },
      }
    },
    async adopt(body, signal) {
      const sessionId = requiredSessionId(body?.sessionId)
      const summary = await sessionFacts(sessionId, signal)
      markManaged(sessionId, summary?.blank === false)
      return { sessionId, session: normalizeSessionSummary(summary) }
    },
    async prompt(body, signal) {
      if (typeof sessionController.prompt !== 'function' || typeof sessionController.follow !== 'function') {
        throw new HttpError(501, 'task_prompt_not_supported', 'This DSH runtime does not expose correlated Session prompt admission.')
      }
      const sessionId = requiredSessionId(body?.sessionId)
      assertManaged(sessionId)
      const summary = await sessionFacts(sessionId, signal)
      const requestId = requiredRequestId(body?.requestId)
      const prompt = requiredTaskPrompt(body?.prompt)
      const fingerprint = JSON.stringify([sessionId, prompt])
      const previous = promptDedupe.get(requestId)
      if (previous !== undefined) {
        if (previous.fingerprint !== fingerprint) {
          throw new HttpError(409, 'request_id_conflict', 'requestId was already used for different task content or a different Session.')
        }
        if (previous.status === 'accepted') return { accepted: true, sessionId, requestId }
        throw new HttpError(409, 'task_prompt_outcome_unknown', 'This requestId was already submitted but its admission outcome is not known; it will not be sent again.')
      }
      const dedupeEntry = { fingerprint, status: 'preparing' }
      promptDedupe.set(requestId, dedupeEntry)
      trimTaskPromptDedupe(promptDedupe)
      let promptAfterSeq
      try {
        const openingBeforePrompt = await readTail(sessionId, signal, 1)
        promptAfterSeq = Number.isSafeInteger(openingBeforePrompt.cursor)
          ? openingBeforePrompt.cursor
          : -1
        assertManaged(sessionId)
      } catch (error) {
        if (promptDedupe.get(requestId) === dedupeEntry) promptDedupe.delete(requestId)
        throw error
      }
      dedupeEntry.status = 'pending'
      try {
        const accepted = await sessionController.prompt({
          sessionId,
          requestId,
          mode: 'queue',
          content: [{ type: 'text', text: prompt }],
        }, signal)
        if (accepted?.accepted !== true) throw new Error('DSH did not acknowledge the task prompt')
        dedupeEntry.status = 'accepted'
      } catch (error) {
        dedupeEntry.status = 'unknown'
        throw error
      }
      const meta = metadata.get(sessionId) ?? {}
      metadata.set(sessionId, {
        ...meta,
        hasSubmittedPrompt: true,
        lastPromptRequestId: requestId,
        promptAfterSeq,
        promptEventSeq: null,
        promptSearchBeforeSeq: null,
        promptSearchThroughSeq: null,
        promptSearchedThroughSeq: promptAfterSeq,
        promptSearchExhausted: false,
        targetTurn: null,
      })
      return { accepted: true, sessionId, requestId }
    },
    async status(sessionId, signal) {
      assertManaged(sessionId)
      const summary = await sessionFacts(sessionId, signal)
      const [opening, projection] = await Promise.all([
        readTail(sessionId, signal),
        readProjection(sessionId, signal),
      ])
      const events = (Array.isArray(opening.records) ? opening.records : [])
        .filter(record => record?.type === 'event' && Number.isSafeInteger(record?.event?.seq))
        .map(record => record.event)
      const lastEventSeq = Number.isSafeInteger(opening.cursor)
        ? opening.cursor
        : (events.at(-1)?.seq ?? -1)
      let meta = metadata.get(sessionId) ?? { hasSubmittedPrompt: false, lastPromptRequestId: null }
      let latestTurnEvents
      if (meta.lastPromptRequestId === null) {
        const latestTurnStartIndex = events.findLastIndex(event => event.type === 'turn/start')
        latestTurnEvents = latestTurnStartIndex < 0 ? events : events.slice(latestTurnStartIndex)
      } else {
        const openingPromptIdentity = findTrackedPrompt(events, meta.lastPromptRequestId)
        let promptIdentity = openingPromptIdentity
        if (promptIdentity === null && !Number.isSafeInteger(meta.targetTurn)
          && meta.promptSearchExhausted !== true && Number.isSafeInteger(meta.promptAfterSeq)) {
          const backfill = await backfillTrackedPrompt({
            sessionController,
            sessionId,
            requestId: meta.lastPromptRequestId,
            promptAfterSeq: meta.promptAfterSeq,
            throughSeq: lastEventSeq,
            initialBeforeSeq: meta.promptSearchBeforeSeq,
            initialThroughSeq: meta.promptSearchThroughSeq,
            searchedThroughSeq: meta.promptSearchedThroughSeq,
            openingEvents: events,
            openingHasMore: opening.hasMore === true,
            signal,
          })
          promptIdentity = backfill.identity
          meta = {
            ...meta,
            promptSearchBeforeSeq: backfill.nextBeforeSeq,
            promptSearchThroughSeq: backfill.nextThroughSeq,
            promptSearchedThroughSeq: backfill.searchedThroughSeq,
            promptSearchExhausted: backfill.exhausted,
          }
          metadata.set(sessionId, meta)
        }
        if (promptIdentity !== null) {
          meta = {
            ...meta,
            promptEventSeq: promptIdentity.promptEventSeq,
            targetTurn: promptIdentity.targetTurn ?? meta.targetTurn,
            promptSearchBeforeSeq: null,
            promptSearchThroughSeq: null,
            promptSearchExhausted: true,
          }
          metadata.set(sessionId, meta)
        }

        if (Number.isSafeInteger(meta.targetTurn)) {
          latestTurnEvents = events.filter(event => event.data?.turn === meta.targetTurn)
        } else if (Number.isSafeInteger(meta.promptEventSeq) && openingPromptIdentity !== null) {
          latestTurnEvents = events.filter(event => event.seq >= meta.promptEventSeq)
          const targetTurn = latestTurnEvents
            .map(event => event.data?.turn)
            .find(Number.isSafeInteger)
          if (targetTurn !== undefined) {
            meta = { ...meta, targetTurn }
            metadata.set(sessionId, meta)
            latestTurnEvents = latestTurnEvents.filter(event => event.data?.turn === targetTurn)
          }
        } else {
          // An idle summary does not prove the inbox was empty. Until the exact
          // durable rpcId is found, no later turn/end is attributed to this prompt.
          latestTurnEvents = []
        }
      }
      const lastTurnEnd = latestTurnEvents.findLast(event => event.type === 'turn/end')
      const lastAssistant = latestTurnEvents.findLast(event => event.type === 'assistant/message')
      const finalTextValue = contentText(lastAssistant?.data?.message?.content ?? lastAssistant?.data?.content)
      const finalText = finalTextValue.length === 0
        ? null
        : truncateUtf8(finalTextValue, MAX_INTERACTION_TEXT_BYTES).text
      const projectedActive = projection?.values?.userQuestions?.active
        ?? opening?.projections?.values?.userQuestions?.active
        ?? []
      const pendingInteractions = enrichTaskInteractions(
        taskInteractions?.list(sessionId, projectedActive) ?? [],
        events,
      )
      const hasHistoryPrompt = summary?.blank === false || events.some(event => [
        'turn/start',
        'user/message',
        'assistant/message',
        'tool/call',
        'tool/result',
        'turn/end',
      ].includes(event.type))
      const hasSubmittedPrompt = meta.hasSubmittedPrompt === true || hasHistoryPrompt
      const taskStatus = taskStatusFrom({
        summary,
        hasSubmittedPrompt,
        lastTurnEnd,
        pendingInteractions,
      })
      const progressText = taskProgressText({ events: latestTurnEvents, pendingInteractions, taskStatus })
      const fingerprint = JSON.stringify([
        lastEventSeq,
        runtimeStatus(summary),
        taskStatus,
        finalText,
        progressText,
        pendingInteractions,
        taskInteractions?.revision(sessionId) ?? 0,
      ])
      const previous = statusRevisions.get(sessionId)
      const revision = previous?.fingerprint === fingerprint ? previous.revision : (previous?.revision ?? 0) + 1
      assertManaged(sessionId)
      statusRevisions.set(sessionId, { fingerprint, revision })
      return {
        sessionId,
        revision,
        runtimeStatus: runtimeStatus(summary),
        taskStatus,
        hasSubmittedPrompt,
        requestId: meta.lastPromptRequestId ?? null,
        lastEventSeq,
        turn: Number.isSafeInteger(lastTurnEnd?.data?.turn) ? lastTurnEnd.data.turn : null,
        lastTurnReason: typeof lastTurnEnd?.data?.reason?.kind === 'string'
          ? lastTurnEnd.data.reason.kind
          : null,
        finalText,
        progressText,
        pendingInteractions,
      }
    },
    async respond(body, signal) {
      if (taskInteractions === undefined) {
        throw new HttpError(501, 'task_response_not_supported', 'This DSH runtime bridge has no interactive answerer.')
      }
      const sessionId = requiredSessionId(body?.sessionId)
      assertManaged(sessionId)
      const summary = await sessionFacts(sessionId, signal)
      const projection = await readProjection(sessionId, signal)
      const projectedActive = projection?.values?.userQuestions?.active ?? []
      return taskInteractions.respond(
        body,
        projectedActive,
        () => ensureAttached(sessionId, summary, signal),
      )
    },
    async cancel(body, signal) {
      if (typeof sessionController.cancel !== 'function') {
        throw new HttpError(501, 'task_cancel_not_supported', 'This DSH runtime does not expose active-turn cancellation.')
      }
      const sessionId = requiredSessionId(body?.sessionId)
      assertManaged(sessionId)
      const summary = await sessionFacts(sessionId, signal)
      if (summary?.running !== true) {
        throw new HttpError(409, 'task_not_running', 'The task Session has no active turn to cancel.')
      }
      assertManaged(sessionId)
      const accepted = await sessionController.cancel({ sessionId })
      if (accepted?.accepted !== true) throw new Error('DSH did not acknowledge task cancellation')
      return { accepted: true, sessionId }
    },
    release(body) {
      const sessionId = requiredSessionId(body?.sessionId)
      managed.delete(sessionId)
      verifiedOrdinary.delete(sessionId)
      metadata.delete(sessionId)
      statusRevisions.delete(sessionId)
      taskInteractions?.release(sessionId)
      return { sessionId, released: true }
    },
    close() {
      if (closed) return
      closed = true
      taskInteractions?.close()
      managed.clear()
      verifiedOrdinary.clear()
      metadata.clear()
      statusRevisions.clear()
      promptDedupe.clear()
    },
  }
}

function publicInteraction(interaction) {
  if (interaction.type === 'approval') {
    return {
      id: interaction.id,
      type: 'approval',
      sessionId: interaction.sessionId,
      createdAt: interaction.createdAt,
      toolName: interaction.toolName,
      callId: interaction.callId ?? null,
      reason: interaction.reason ?? null,
      outcomes: ['allowed-once', 'rejected'],
      answerable: true,
    }
  }
  return {
    id: interaction.id,
    type: 'question',
    sessionId: interaction.sessionId,
    callId: interaction.callId,
    state: interaction.state,
    createdAt: interaction.createdAt,
    questions: interaction.questions,
    answerable: true,
    replyQueued: false,
  }
}

function interactionSessionId(agent) {
  const value = agent?.id ?? agent?.session?.id
  return typeof value === 'string' && value.length > 0 && value.length <= 256 ? value : undefined
}

function boundedInteractionText(value) {
  return truncateUtf8(String(value ?? ''), MAX_INTERACTION_TEXT_BYTES).text
}

function optionalInteractionText(value) {
  if (typeof value !== 'string' || value.length === 0) return undefined
  return truncateUtf8(value, MAX_INTERACTION_TEXT_BYTES).text
}

function optionalInteractionCallId(value) {
  if (value === undefined || value === null || value === '') return undefined
  if (typeof value !== 'string' || value.length > 240 || Buffer.byteLength(value, 'utf8') > 512) {
    throw new HttpError(502, 'invalid_dsh_interaction', 'DSH supplied an invalid user-question call identity.')
  }
  return value
}

function normalizeInteractionQuestions(value) {
  if (!Array.isArray(value) || value.length === 0 || value.length > 8) {
    throw new HttpError(502, 'invalid_dsh_question', 'DSH supplied an empty or invalid user-question batch.')
  }
  const seenIds = new Set()
  return value.map(question => {
    const id = typeof question?.id === 'string' && question.id.length > 0 && question.id.length <= 200
      ? question.id
      : undefined
    const text = optionalInteractionText(question?.question)
    if (id === undefined || text === undefined || seenIds.has(id)) {
      throw new HttpError(502, 'invalid_dsh_question', 'DSH supplied a user question without an id or text.')
    }
    seenIds.add(id)
    if (question?.options !== undefined && !Array.isArray(question.options)) {
      throw new HttpError(502, 'invalid_dsh_question', 'DSH supplied an invalid question option list.')
    }
    if (Array.isArray(question?.options) && question.options.length > 32) {
      throw new HttpError(502, 'invalid_dsh_question', 'DSH supplied too many options for one user question.')
    }
    const seenLabels = new Set()
    const options = Array.isArray(question?.options)
      ? question.options.map(option => {
          const label = typeof option?.label === 'string' && option.label.length > 0
            && Buffer.byteLength(option.label, 'utf8') <= MAX_INTERACTION_TEXT_BYTES
            ? option.label
            : undefined
          if (label === undefined || seenLabels.has(label)) {
            throw new HttpError(502, 'invalid_dsh_question', 'DSH supplied an invalid or duplicate question option label.')
          }
          seenLabels.add(label)
          const description = optionalInteractionText(option?.description)
          return { label, ...(description === undefined ? {} : { description }) }
        })
      : []
    return {
      id,
      header: optionalInteractionText(question?.header) ?? null,
      question: text,
      options,
      multiSelect: question?.multiSelect === true,
    }
  })
}

function answerBatchFromMap(value, questions) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new HttpError(400, 'invalid_question_answers', 'answers must be an object keyed by question id.')
  }
  const expectedIds = new Set(questions.map(question => question.id))
  const suppliedIds = Object.keys(value)
  if (suppliedIds.length !== expectedIds.size || suppliedIds.some(id => !expectedIds.has(id))) {
    throw new HttpError(400, 'invalid_question_answers', 'answers must name each pending question id exactly once.')
  }
  return {
    answers: questions.map(question => {
      const item = value[question.id]
      if (typeof item !== 'string' || Buffer.byteLength(item, 'utf8') > MAX_COMMAND_TEXT_BYTES) {
        throw new HttpError(400, 'invalid_question_answers', `answers.${question.id} must be a string within the text limit.`)
      }
      const labels = new Set(question.options.map(option => option.label))
      if (item.length === 0) {
        return { id: question.id, selected: [] }
      }
      if (item.trim().length === 0) {
        throw new HttpError(400, 'invalid_question_answers', `answers.${question.id} must not contain only whitespace.`)
      }
      if (labels.has(item)) {
        return { id: question.id, selected: [item] }
      }
      if (question.multiSelect) {
        const selected = [...new Set(item.split('|').map(label => label.trim()))]
        if (selected.length > 0 && selected.every(label => label.length > 0 && labels.has(label))) {
          return { id: question.id, selected }
        }
      }
      return { id: question.id, selected: [], custom: item }
    }),
  }
}

function requiredApprovalOutcome(value) {
  if (value !== 'allowed-once' && value !== 'rejected') {
    throw new HttpError(400, 'invalid_approval_outcome', 'outcome must be allowed-once or rejected.')
  }
  return value
}

function findTrackedPrompt(events, requestId) {
  if (!Array.isArray(events) || typeof requestId !== 'string') return null
  const ordered = events
    .filter(event => event !== null && typeof event === 'object' && Number.isSafeInteger(event.seq))
    .toSorted((left, right) => left.seq - right.seq)
  const promptIndex = ordered.findIndex(event => {
    if (event.type !== 'user/message') return false
    const source = event.data?.source ?? event.data?.message?.source
    return source?.kind === 'user' && source.rpcId === requestId
  })
  if (promptIndex < 0) return null

  const prompt = ordered[promptIndex]
  let targetTurn = Number.isSafeInteger(prompt.data?.turn) ? prompt.data.turn : null
  if (targetTurn === null) {
    for (let index = promptIndex - 1; index >= 0; index -= 1) {
      const candidate = ordered[index]
      if (candidate.type === 'turn/start' && Number.isSafeInteger(candidate.data?.turn)) {
        targetTurn = candidate.data.turn
        break
      }
    }
  }
  if (targetTurn === null) {
    for (let index = promptIndex + 1; index < ordered.length; index += 1) {
      const candidate = ordered[index]
      if (candidate.type === 'turn/start') break
      if (Number.isSafeInteger(candidate.data?.turn)) {
        targetTurn = candidate.data.turn
        break
      }
    }
  }
  return { promptEventSeq: prompt.seq, targetTurn }
}

async function backfillTrackedPrompt({
  sessionController,
  sessionId,
  requestId,
  promptAfterSeq,
  throughSeq,
  initialBeforeSeq,
  initialThroughSeq,
  searchedThroughSeq,
  openingEvents,
  openingHasMore,
  signal,
}) {
  const openingIdentity = findTrackedPrompt(openingEvents, requestId)
  if (openingIdentity !== null) {
    return {
      identity: openingIdentity,
      nextBeforeSeq: null,
      nextThroughSeq: null,
      searchedThroughSeq: Number.isSafeInteger(searchedThroughSeq)
        ? searchedThroughSeq
        : promptAfterSeq,
      exhausted: true,
    }
  }

  const baseline = promptAfterSeq
  let fullySearchedThrough = Number.isSafeInteger(searchedThroughSeq)
    ? Math.max(baseline, searchedThroughSeq)
    : baseline
  let scanThrough = Number.isSafeInteger(initialThroughSeq) && initialThroughSeq > baseline
    ? initialThroughSeq
    : null
  let beforeSeq = scanThrough === null || !Number.isSafeInteger(initialBeforeSeq)
    ? null
    : initialBeforeSeq

  if (scanThrough === null) {
    if (!Number.isSafeInteger(throughSeq) || throughSeq <= fullySearchedThrough) {
      return {
        identity: null,
        nextBeforeSeq: null,
        nextThroughSeq: null,
        searchedThroughSeq: fullySearchedThrough,
        exhausted: false,
      }
    }
    scanThrough = throughSeq

    // The opening snapshot is already the newest part of this fixed scan.
    // Continue before its oldest event instead of rereading the same window.
    if (openingHasMore !== true) {
      return {
        identity: null,
        nextBeforeSeq: null,
        nextThroughSeq: null,
        searchedThroughSeq: Math.max(fullySearchedThrough, scanThrough),
        exhausted: false,
      }
    }
    const openingSeqs = openingEvents
      .map(event => event?.seq)
      .filter(Number.isSafeInteger)
    if (openingSeqs.length > 0) {
      beforeSeq = Math.min(...openingSeqs)
      if (beforeSeq <= fullySearchedThrough + 1) {
        return {
          identity: null,
          nextBeforeSeq: null,
          nextThroughSeq: null,
          searchedThroughSeq: Math.max(fullySearchedThrough, scanThrough),
          exhausted: false,
        }
      }
    }
  }

  for (let pageIndex = 0; pageIndex < TASK_PROMPT_SEARCH_PAGES_PER_STATUS; pageIndex += 1) {
    const page = await sessionController.page({
      address: { kind: 'session', sessionId },
      throughSeq: scanThrough,
      ...(Number.isSafeInteger(beforeSeq) ? { beforeSeq } : {}),
      maxMessages: TASK_PROMPT_SEARCH_MESSAGES,
    }, signal)
    const pageEvents = (Array.isArray(page?.records) ? page.records : [])
      .filter(record => record?.type === 'event' && Number.isSafeInteger(record?.event?.seq))
      .map(record => record.event)
      .filter(event => event.seq > fullySearchedThrough && event.seq <= scanThrough)
    const identity = findTrackedPrompt(pageEvents, requestId)
    if (identity !== null) {
      return {
        identity,
        nextBeforeSeq: null,
        nextThroughSeq: null,
        searchedThroughSeq: fullySearchedThrough,
        exhausted: true,
      }
    }

    const pageSeqs = pageEvents.map(event => event.seq)
    const nextBeforeSeq = pageSeqs.length > 0 ? Math.min(...pageSeqs) : null
    const reachedLowerBound = nextBeforeSeq !== null && nextBeforeSeq <= fullySearchedThrough + 1
    if (page?.hasMore !== true || reachedLowerBound) {
      fullySearchedThrough = Math.max(fullySearchedThrough, scanThrough)
      return {
        identity: null,
        nextBeforeSeq: null,
        nextThroughSeq: null,
        searchedThroughSeq: fullySearchedThrough,
        exhausted: false,
      }
    }
    if (nextBeforeSeq === null
      || (Number.isSafeInteger(beforeSeq) && nextBeforeSeq >= beforeSeq)) {
      // Do not guess that an empty/non-advancing page covered the range.
      break
    }
    beforeSeq = nextBeforeSeq
  }

  return {
    identity: null,
    nextBeforeSeq: beforeSeq,
    nextThroughSeq: scanThrough,
    searchedThroughSeq: fullySearchedThrough,
    exhausted: false,
  }
}

function taskStatusFrom({ summary, hasSubmittedPrompt, lastTurnEnd, pendingInteractions }) {
  if (pendingInteractions.some(interaction => interaction.type === 'approval')) return 'waitingApproval'
  if (pendingInteractions.some(interaction => interaction.type === 'question')) return 'waitingInput'
  if (summary?.running === true) return 'running'
  if (!hasSubmittedPrompt) return 'idle'
  const reason = lastTurnEnd?.data?.reason?.kind
  if (reason === 'completed') return 'completed'
  if (reason === 'aborted') return 'cancelled'
  if (typeof reason === 'string') return 'failed'
  // Prompt admission may precede the first running/list frame. Keep it running
  // until a durable turn/end supplies a terminal state.
  return 'running'
}

function trimTaskPromptDedupe(dedupe) {
  while (dedupe.size > COMMAND_DEDUPE_LIMIT) {
    const removable = [...dedupe].find(([, value]) => value.status === 'accepted' || value.status === 'unknown')
    if (removable === undefined) return
    dedupe.delete(removable[0])
  }
}

function taskProgressText({ events, pendingInteractions, taskStatus }) {
  const approval = pendingInteractions.find(interaction => interaction.type === 'approval')
  if (approval !== undefined) {
    const toolName = typeof approval.toolName === 'string' && approval.toolName.length > 0
      ? approval.toolName
      : '工具调用'
    return truncateUtf8(`等待授权：${toolName}`, 512).text
  }
  const question = pendingInteractions.find(interaction => interaction.type === 'question')
  if (question !== undefined) {
    const first = Array.isArray(question.questions) ? question.questions[0] : undefined
    const prompt = first?.header ?? first?.question ?? '需要回答'
    return truncateUtf8(`等待回答：${prompt}`, 512).text
  }

  const latestTurnStartIndex = events.findLastIndex(event => event.type === 'turn/start')
  const turnEvents = latestTurnStartIndex < 0 ? events : events.slice(latestTurnStartIndex)
  const calls = new Map()
  let latestToolState = null
  for (const event of turnEvents) {
    if (event.type === 'tool/call') {
      const callId = typeof event.data?.callId === 'string' ? event.data.callId : ''
      const name = typeof event.data?.name === 'string' && event.data.name.length > 0
        ? truncateUtf8(event.data.name, 256).text
        : '工具'
      if (callId.length > 0) calls.set(callId, name)
      latestToolState = `正在执行：${name}`
    } else if (event.type === 'tool/result') {
      const message = event.data?.message
      const callId = typeof message?.toolCallId === 'string' ? message.toolCallId : ''
      const name = calls.get(callId) ?? '工具'
      const failed = message?.isError === true || event.data?.error !== undefined
      latestToolState = failed ? `执行失败：${name}` : `已完成：${name}`
    }
  }
  if (taskStatus === 'running' && latestToolState !== null) {
    return truncateUtf8(latestToolState, 512).text
  }
  switch (taskStatus) {
    case 'completed': return '任务已完成'
    case 'failed': return '任务执行失败'
    case 'cancelled': return '任务已取消'
    case 'running': return '正在处理任务'
    default: return '等待任务'
  }
}

function enrichTaskInteractions(interactions, events) {
  if (!Array.isArray(interactions) || interactions.length === 0) return []
  const calls = new Map()
  for (const event of events) {
    if (event.type !== 'tool/call' || typeof event.data?.callId !== 'string') continue
    calls.set(event.data.callId, event.data)
  }
  return interactions.map(interaction => {
    if (interaction.type !== 'approval' || typeof interaction.callId !== 'string') return interaction
    const call = calls.get(interaction.callId)
    if (call === undefined) return interaction
    const rawArguments = typeof call.arguments === 'string'
      ? call.arguments
      : JSON.stringify(call.arguments ?? {})
    const bounded = truncateUtf8(rawArguments, MAX_TOOL_RESULT_BYTES)
    const operation = `工具参数（原始）：${bounded.text}${bounded.truncated ? '（已截断）' : ''}`
    return {
      ...interaction,
      reason: interaction.reason === null || interaction.reason.length === 0
        ? operation
        : truncateUtf8(`${interaction.reason}\n${operation}`, MAX_INTERACTION_TEXT_BYTES).text,
    }
  })
}

async function handleRequest({
  request,
  response,
  token,
  sessionController,
  workspaceController,
  commandCoordinator,
  sessionCommandCoordinator,
  deviceSessionClassifier,
  taskCoordinator,
  config,
  startedAt,
}) {
  applySecurityHeaders(response)
  if (!authorized(request.headers.authorization, token)) {
    response.setHeader('WWW-Authenticate', 'Bearer')
    sendError(response, 401, 'unauthorized', 'A valid bridge bearer token is required.')
    return
  }
  if (typeof request.url !== 'string' || request.url.length > 8192) {
    sendError(response, 414, 'invalid_url', 'The request URL is invalid or too long.')
    return
  }

  const url = new URL(request.url, `http://${LOOPBACK_HOST}`)
  if (url.pathname === '/v1/tasks/create') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.create(body, signal),
      )
      sendJson(response, 201, result)
    } catch (error) {
      sendOperationError(response, error, 'task_create_failed')
    }
    return
  }

  if (url.pathname === '/v1/tasks/adopt') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.adopt(body, signal),
      )
      sendJson(response, 200, result)
    } catch (error) {
      sendOperationError(response, error, 'task_adopt_failed')
    }
    return
  }

  if (url.pathname === '/v1/tasks/prompt') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.prompt(body, signal),
      )
      sendJson(response, 202, result)
    } catch (error) {
      sendOperationError(response, error, 'task_prompt_failed')
    }
    return
  }

  if (url.pathname === '/v1/tasks/interactions/respond') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.respond(body, signal),
      )
      sendJson(response, 202, result)
    } catch (error) {
      sendOperationError(response, error, 'task_response_failed')
    }
    return
  }

  if (url.pathname === '/v1/tasks/cancel') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.cancel(body, signal),
      )
      sendJson(response, 202, result)
    } catch (error) {
      sendOperationError(response, error, 'task_cancel_failed')
    }
    return
  }

  if (url.pathname === '/v1/tasks/release') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      sendJson(response, 200, taskCoordinator.release(body))
    } catch (error) {
      sendOperationError(response, error, 'task_release_failed')
    }
    return
  }

  if (url.pathname === '/v1/session-command') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    if (!sessionCommandCoordinator.supported) {
      sendError(response, 501, 'prompt_not_supported', 'This DSH runtime does not expose prompt and follow.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const command = parseDeviceCommand(body)
      if (command.sessionId === undefined) {
        throw new HttpError(400, 'invalid_session_id', 'sessionId is required for a normal Session command.')
      }
      await rejectArchivedSessionCommand(
        workspaceController,
        command.sessionId,
        config.requestTimeoutMs,
      )
      await rejectDeviceSessionOperation(
        sessionController,
        commandCoordinator.sessionId,
        command.sessionId,
      )
      const result = await sessionCommandCoordinator.execute(command)
      sendJson(response, result.status === 'completed' ? 200 : 202, result)
    } catch (error) {
      sendOperationError(response, error, 'session_command_failed')
    }
    return
  }

  if (url.pathname === '/v1/sessions/create') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    if (typeof sessionController.create !== 'function' || typeof sessionController.rename !== 'function') {
      sendError(response, 501, 'session_create_not_supported', 'This DSH runtime does not expose create and rename.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const title = requiredTitle(body.title)
      const created = await sessionController.create({})
      const sessionId = requiredReturnedSessionId(created?.sessionId)
      const renamed = await sessionController.rename({ sessionId, title })
      const acceptedTitle = typeof renamed?.title === 'string' ? renamed.title : title
      const projected = await findSessionSummary(sessionController, sessionId)
      sendJson(response, 201, {
        session: projected === undefined
          ? normalizeSessionSummary({
              sessionId,
              projections: { values: { title: acceptedTitle, agentPreset: created?.agentPreset } },
            })
          : normalizeSessionSummary(projected),
      })
    } catch (error) {
      sendOperationError(response, error, 'session_create_failed')
    }
    return
  }

  if (url.pathname === '/v1/sessions/rename') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    if (typeof sessionController.rename !== 'function') {
      sendError(response, 501, 'session_rename_not_supported', 'This DSH runtime does not expose rename.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const sessionId = requiredSessionId(body.sessionId)
      await rejectDeviceSessionOperation(sessionController, commandCoordinator.sessionId, sessionId)
      const renamed = await sessionController.rename({ sessionId, title: requiredTitle(body.title) })
      sendJson(response, 200, {
        sessionId,
        title: typeof renamed?.title === 'string' ? renamed.title : requiredTitle(body.title),
      })
    } catch (error) {
      sendOperationError(response, error, 'session_rename_failed')
    }
    return
  }

  if (url.pathname === '/v1/sessions/archive') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    if (!supportsRecoverableArchive(workspaceController)) {
      sendError(response, 501, 'session_archive_not_supported', 'This DSH runtime does not expose recoverable Session archive and restore.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const sessionId = requiredSessionId(body.sessionId)
      await rejectDeviceSessionOperation(sessionController, commandCoordinator.sessionId, sessionId)
      if (typeof body.archived !== 'boolean') {
        throw new HttpError(400, 'invalid_archived', 'archived must be true or false.')
      }
      const changed = body.archived
        ? await workspaceController.archiveSession({ sessionId })
        : await workspaceController.unarchiveSession({ sessionId })
      const confirmedIds = changed?.archivedSessionIds
      if (!Array.isArray(confirmedIds) || confirmedIds.includes(sessionId) !== body.archived) {
        throw new Error('DSH did not confirm the requested Session archive state')
      }
      sendJson(response, 200, { sessionId, archived: body.archived })
    } catch (error) {
      sendOperationError(response, error, 'session_archive_failed')
    }
    return
  }

  if (url.pathname === '/v1/device-command') {
    if (request.method !== 'POST') {
      response.setHeader('Allow', 'POST')
      sendError(response, 405, 'method_not_allowed', 'This endpoint requires POST.')
      return
    }
    if (!commandCoordinator.supported) {
      sendError(response, 501, 'prompt_not_supported', 'This DSH runtime does not expose create, prompt, and follow.')
      return
    }
    try {
      const body = await readJsonBody(request, MAX_COMMAND_BODY_BYTES)
      const command = parseDeviceCommand(body)
      const result = await commandCoordinator.execute(command)
      sendJson(response, result.status === 'completed' ? 200 : 202, result)
    } catch (error) {
      if (response.headersSent || response.writableEnded) return
      if (error instanceof HttpError) {
        sendError(response, error.status, error.code, error.message)
        return
      }
      const mapped = dshCommandError(error)
      sendError(response, mapped.status, mapped.code, mapped.message)
    }
    return
  }

  if (request.method !== 'GET') {
    response.setHeader('Allow', 'GET')
    sendError(response, 405, 'method_not_allowed', 'This endpoint requires GET.')
    return
  }

  if (url.pathname === '/v1/tasks/status') {
    const sessionId = url.searchParams.get('sessionId')
    if (sessionId === null || sessionId.length === 0 || sessionId.length > 256) {
      sendError(response, 400, 'invalid_session_id', 'sessionId is required and must be at most 256 characters.')
      return
    }
    try {
      const result = await runWithRequestSignal(
        request,
        response,
        config.requestTimeoutMs,
        signal => taskCoordinator.status(sessionId, signal),
      )
      sendJson(response, 200, result)
    } catch (error) {
      sendOperationError(response, error, 'task_status_failed')
    }
    return
  }

  if (url.pathname === '/v1/status') {
    sendJson(response, 200, {
      protocolVersion: PROTOCOL_VERSION,
      ready: true,
      pid: process.pid,
      home: config.home,
      profile: config.profile,
      deviceAgentPreset: config.deviceAgentPreset ?? null,
      deviceSessionId: commandCoordinator.sessionId,
      startedAt,
      ...(config.ownedStartupId === undefined ? {} : { ownedStartupId: config.ownedStartupId }),
      capabilities: {
        sessions: true,
        history: true,
        prompt: commandCoordinator.supported,
        deviceCommand: commandCoordinator.supported,
        deviceHistoryGrouping: true,
        sessionPrompt: sessionCommandCoordinator.supported,
        sessionCreate: typeof sessionController.create === 'function'
          && typeof sessionController.rename === 'function',
        sessionRename: typeof sessionController.rename === 'function',
        sessionArchive: supportsRecoverableArchive(workspaceController),
        sessionRestore: supportsRecoverableArchive(workspaceController),
        cancel: false,
        ...taskCoordinator.capabilities,
      },
    })
    return
  }

  if (url.pathname === '/v1/sessions') {
    await withRequestSignal(request, response, config.requestTimeoutMs, async signal => {
      const result = await sessionController.list({}, signal)
      const summaries = Array.isArray(result?.items) ? result.items : []
      const topLevel = summaries.filter(summary => summary?.origin !== 'subagent')
      const archivedIds = await readArchivedSessionIds(workspaceController, signal)
      const archived = await readMissingArchivedSessionSummaries(
        sessionController,
        archivedIds,
        new Set(summaries.map(sessionSummaryId)),
        signal,
      )
      const deviceSessions = await deviceSessionClassifier.classify(topLevel, signal)
      const sessions = topLevel
        .map(summary => normalizeSessionSummary(
          summary,
          deviceSessions.get(sessionSummaryId(summary)) ?? null,
          archivedIds.has(sessionSummaryId(summary)),
        ))
        .concat(archived.map(summary => normalizeSessionSummary(summary, undefined, true)))
        .filter(summary => summary.id.length > 0)
      sendJson(response, 200, {
        home: config.home,
        profile: config.profile,
        sessions,
      })
    })
    return
  }

  if (url.pathname === '/v1/history') {
    const sessionId = url.searchParams.get('sessionId')
    if (sessionId === null || sessionId.length === 0 || sessionId.length > 256) {
      sendError(response, 400, 'invalid_session_id', 'sessionId is required and must be at most 256 characters.')
      return
    }
    let beforeSeq
    let limit
    try {
      beforeSeq = parseOptionalNonNegativeInteger(url.searchParams.get('beforeSeq'), 'beforeSeq')
      limit = parseLimit(url.searchParams.get('limit'))
    } catch (error) {
      if (error instanceof HttpInputError) {
        sendError(response, 400, 'invalid_history_query', error.message)
        return
      }
      throw error
    }

    await withRequestSignal(request, response, config.requestTimeoutMs, async signal => {
      // The initial page needs the exact current tail. inspect() is the
      // Controller's documented non-activating read. Older pages already carry
      // an exclusive dense-log cursor, so beforeSeq - 1 is their exact upper cut
      // and avoids re-reading the complete log on every pagination request.
      let throughSeq
      if (beforeSeq === undefined) {
        const inspection = await sessionController.inspect(sessionId, signal)
        const events = Array.isArray(inspection?.events) ? inspection.events : []
        throughSeq = events.at(-1)?.seq ?? -1
      } else {
        throughSeq = beforeSeq - 1
      }
      if (!Number.isSafeInteger(throughSeq) || throughSeq < -1) {
        throw new Error('DSH returned an invalid session cursor')
      }

      const page = throughSeq === -1
        ? { records: [], hasMore: false }
        : await sessionController.page({
          address: { kind: 'session', sessionId },
          throughSeq,
          ...(beforeSeq === undefined ? {} : { beforeSeq }),
          maxMessages: limit,
        }, signal)
      const normalized = normalizeHistoryPage(
        page,
        config.maxEntryTextBytes,
        config.maxPageTextBytes,
        sessionId,
      )
      sendJson(response, 200, {
        sessionId,
        ...normalized,
      })
    })
    return
  }

  sendError(response, 404, 'not_found', 'The requested bridge endpoint does not exist.')
}

function supportsRecoverableArchive(workspaceController) {
  return typeof workspaceController?.archiveSession === 'function'
    && typeof workspaceController?.unarchiveSession === 'function'
    && typeof workspaceController?.follow === 'function'
}

async function readArchivedSessionIds(workspaceController, signal) {
  if (typeof workspaceController?.follow !== 'function') return new Set()
  let iterator
  try {
    iterator = workspaceController.follow(signal)[Symbol.asyncIterator]()
    const opening = await iterator.next()
    if (!opening.done && opening.value?.type === 'baseline'
      && Array.isArray(opening.value.value?.archivedSessionIds)) {
      return new Set(opening.value.value.archivedSessionIds
        .filter(id => typeof id === 'string' && id.length > 0 && id.length <= 256))
    }
    throw new Error('DSH Workspace follow did not return an opening baseline')
  } finally {
    await iterator?.return?.().catch(() => {})
  }
}

async function readMissingArchivedSessionSummaries(sessionController, archivedIds, visibleIds, signal) {
  const result = []
  for (const sessionId of archivedIds) {
    if (visibleIds.has(sessionId)) continue
    if (result.length >= 5000) break
    if (signal?.aborted) throw signal.reason ?? new Error('archived Session read cancelled')
    let projections
    if (typeof sessionController.projections === 'function') {
      try {
        projections = await sessionController.projections({ sessionId }, signal) ?? undefined
      } catch (error) {
        if (signal?.aborted) throw error
      }
    }
    result.push({
      sessionId,
      running: false,
      agentAvailable: false,
      ...(projections === undefined ? {} : { projections }),
    })
  }
  return result
}

async function rejectArchivedSessionCommand(workspaceController, sessionId, timeoutMs) {
  if (typeof workspaceController?.follow !== 'function') return
  const abort = new AbortController()
  const timeout = setTimeout(
    () => abort.abort(new Error('archived Session lookup timed out')),
    timeoutMs,
  )
  timeout.unref?.()
  try {
    const archivedIds = await readArchivedSessionIds(workspaceController, abort.signal)
    if (archivedIds.has(sessionId)) {
      throw new HttpError(409, 'session_archived', 'Restore the archived Session before sending a message.')
    }
  } finally {
    clearTimeout(timeout)
  }
}

async function findSessionSummary(sessionController, sessionId, signal = new AbortController().signal) {
  const listed = await sessionController.list({}, signal)
  return (Array.isArray(listed?.items) ? listed.items : [])
    .find(summary => sessionSummaryId(summary) === sessionId)
}

async function rejectDeviceSessionOperation(sessionController, pinnedDeviceSessionId, sessionId) {
  if (pinnedDeviceSessionId === sessionId) {
    throw new HttpError(409, 'session_is_device_control', 'Use the dedicated device-command endpoint for the pinned Halo device Session.')
  }
  const summary = await findSessionSummary(sessionController, sessionId)
  if (isCanonicalDeviceSession(summary)) {
    throw new HttpError(409, 'session_is_device_control', 'The canonical Halo device Session is protected from ordinary Session operations.')
  }
  // Archived Sessions may be absent from list(). When supported, projections
  // provide the same exact preset marker without activating an Agent. A
  // missing or unreadable projection remains the Controller operation's own
  // responsibility; never infer device identity from title or text.
  if (summary === undefined && typeof sessionController.projections === 'function') {
    try {
      const projections = await sessionController.projections({ sessionId })
      if (projections?.values?.agentPreset === DEVICE_AGENT_PRESET) {
        throw new HttpError(409, 'session_is_device_control', 'The canonical Halo device Session is protected from ordinary Session operations.')
      }
    } catch (error) {
      if (error instanceof HttpError) throw error
    }
  }
}

function requiredSessionId(value) {
  const sessionId = optionalBoundedString(value, 'sessionId', 256)
  if (sessionId === undefined) throw new HttpError(400, 'invalid_session_id', 'sessionId is required.')
  return sessionId
}

function requiredReturnedSessionId(value) {
  if (typeof value !== 'string' || value.length === 0 || value.length > 256) {
    throw new Error('DSH returned an invalid Session id')
  }
  return value
}

function requiredTitle(value) {
  if (typeof value !== 'string' || value.trim().length === 0 || value.trim() !== value
    || Buffer.byteLength(value, 'utf8') > 4096) {
    throw new HttpError(400, 'invalid_title', 'title is required, must be trimmed, and must not exceed 4096 UTF-8 bytes.')
  }
  return value
}

function requiredTaskBaseDirectory(value) {
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_TASK_BASE_DIRECTORY_LENGTH
    || value.trim() !== value || value.includes('\0') || !isAbsolute(value)) {
    throw new HttpError(400, 'invalid_task_base_directory', 'baseDirectory must be a trimmed absolute local path.')
  }
  if (process.platform === 'win32' && /^(?:\\\\|\/\/)/u.test(value)) {
    throw new HttpError(400, 'invalid_task_base_directory', 'baseDirectory must be local; UNC and device paths are not accepted.')
  }
  if (process.platform === 'win32' && !/^[A-Za-z]:[\\/]/u.test(value)) {
    throw new HttpError(400, 'invalid_task_base_directory', 'baseDirectory must include a local drive letter.')
  }
  return resolve(value)
}

function requiredTaskDirectoryName(value) {
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_TASK_DIRECTORY_NAME_LENGTH
    || value.trim() !== value || value === '.' || value === '..'
    || /[<>:"/\\|?*\u0000-\u001F]/u.test(value) || /[. ]$/u.test(value)) {
    throw new HttpError(400, 'invalid_task_directory_name', 'directoryName must be one safe local directory name without separators or traversal.')
  }
  const stem = value.split('.', 1)[0].toUpperCase()
  if (/^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/u.test(stem)) {
    throw new HttpError(400, 'invalid_task_directory_name', 'directoryName is reserved by Windows.')
  }
  return value
}

function requiredRequestId(value) {
  const requestId = optionalBoundedString(value, 'requestId', 128)
  if (requestId === undefined || !/^[A-Za-z0-9][A-Za-z0-9._:-]*$/u.test(requestId)) {
    throw new HttpError(400, 'invalid_request_id', 'requestId is required and must use only ASCII letters, digits, dot, underscore, colon, or hyphen.')
  }
  return requestId
}

function requiredTaskPrompt(value) {
  if (typeof value !== 'string' || value.trim().length === 0
    || Buffer.byteLength(value, 'utf8') > MAX_COMMAND_TEXT_BYTES) {
    throw new HttpError(400, 'invalid_task_prompt', `prompt must contain text and must not exceed ${MAX_COMMAND_TEXT_BYTES} UTF-8 bytes.`)
  }
  return value
}

function sendOperationError(response, error, fallbackCode) {
  if (response.headersSent || response.writableEnded) return
  if (error instanceof HttpError) {
    sendError(response, error.status, error.code, error.message)
    return
  }
  const code = typeof error?.code === 'string' ? error.code : ''
  const message = safeError(error)
  if (code === 'session/not-found' || error?.name === 'SessionPersistenceNotFoundError') {
    sendError(response, 404, 'session_not_found', message)
    return
  }
  if (code === 'gateway/bad-request' || code === 'session/title-invalid') {
    sendError(response, 400, 'dsh_rejected_request', message)
    return
  }
  if (code === 'session/writer-held' || code === 'agent-preset/conflict'
    || code === 'session/conflict' || code === 'session/agent-busy'
    || code === 'workspace/session-active' || /already owned|writer[- ]held|writer lock/iu.test(message)) {
    sendError(response, 409, 'session_unavailable', message)
    return
  }
  sendError(response, 502, fallbackCode, message)
}

function createSessionCommandCoordinator(sessionController, config) {
  const supported = ['prompt', 'follow'].every(name => typeof sessionController?.[name] === 'function')
  let queueTail = Promise.resolve()
  let queued = 0
  const dedupe = new Map()

  const prune = () => {
    const cutoff = Date.now() - COMMAND_DEDUPE_TTL_MS
    for (const [requestId, entry] of dedupe) {
      if (entry.createdAt < cutoff && entry.promise === undefined) dedupe.delete(requestId)
    }
    while (dedupe.size > COMMAND_DEDUPE_LIMIT) {
      const removable = [...dedupe].find(([, entry]) => entry.promise === undefined)
      if (removable === undefined) break
      dedupe.delete(removable[0])
    }
  }

  return {
    supported,
    execute(command) {
      if (!supported) throw new HttpError(501, 'prompt_not_supported', 'This DSH runtime does not expose prompt and follow.')
      const requestId = command.requestId ?? randomUUID()
      const prepared = { ...command, requestId }
      const fingerprint = JSON.stringify([prepared.command, prepared.sessionId])
      let entry = dedupe.get(requestId)
      if (entry !== undefined && entry.fingerprint !== fingerprint) {
        throw new HttpError(409, 'request_id_conflict', 'requestId was already used for different Session content.')
      }
      if (entry?.promise !== undefined) return entry.promise
      if (entry?.result !== undefined) return Promise.resolve(entry.result)
      if (queued >= config.maxQueuedDeviceCommands) {
        throw new HttpError(429, 'session_command_queue_full', 'Too many Session commands are waiting for DSH.')
      }
      entry ??= { fingerprint, createdAt: Date.now() }
      dedupe.set(requestId, entry)
      queued += 1
      const task = queueTail.then(() => runDeviceCommand(sessionController, prepared.sessionId, prepared))
      queueTail = task.catch(() => {})
      entry.promise = task
      void task.then(result => {
        if (result.status === 'completed') entry.result = result
      }, () => {}).finally(() => {
        entry.promise = undefined
        queued -= 1
        prune()
      })
      return task
    },
  }
}

/**
 * Serialize all device prompts through one dedicated DSH Session. The caller
 * should persist the returned sessionId and send it on the first call after a
 * bridge restart. requestId is the durable DSH idempotency key.
 */
export function createDeviceCommandCoordinator(sessionController, config, logger = console) {
  const supported = ['create', 'prompt', 'follow']
    .every(name => typeof sessionController?.[name] === 'function')
  let pinnedSessionId = config.deviceSessionId
  let sessionInitialized = false
  let queueTail = Promise.resolve()
  let queued = 0
  const dedupe = new Map()

  const pruneDedupe = () => {
    const cutoff = Date.now() - COMMAND_DEDUPE_TTL_MS
    for (const [requestId, entry] of dedupe) {
      if (entry.createdAt < cutoff && entry.promise === undefined) dedupe.delete(requestId)
    }
    while (dedupe.size > COMMAND_DEDUPE_LIMIT) {
      const removable = [...dedupe].find(([, entry]) => entry.promise === undefined)
      if (removable === undefined) break
      dedupe.delete(removable[0])
    }
  }

  const ensureSession = async requestedSessionId => {
    if (pinnedSessionId !== undefined && requestedSessionId !== undefined
      && requestedSessionId !== pinnedSessionId) {
      throw new HttpError(409, 'device_session_conflict', 'The bridge is already pinned to a different device session.')
    }
    const wanted = pinnedSessionId ?? requestedSessionId
    if (sessionInitialized) return pinnedSessionId

    const createRequest = await resolveDeviceSessionCreateRequest(sessionController, wanted, config)
    const created = await sessionController.create(createRequest)
    const actual = typeof created?.sessionId === 'string' ? created.sessionId : ''
    if (actual.length === 0 || actual.length > 256) {
      throw new Error('DSH returned an invalid device session id')
    }
    if (wanted !== undefined && actual !== wanted) {
      throw new Error('DSH returned a different device session id')
    }
    pinnedSessionId = actual
    sessionInitialized = true

    // rename() writes the title projection directly; it does not invoke an LLM
    // title generator. Older or reduced deployments may omit the title service,
    // so naming is best effort and never blocks device control.
    if (typeof sessionController.rename === 'function' && config.deviceSessionTitle !== undefined) {
      try {
        await sessionController.rename({ sessionId: actual, title: config.deviceSessionTitle })
      } catch (error) {
        logger?.warn?.(`halo-session-bridge: could not name device session: ${safeError(error)}`)
      }
    }
    return actual
  }

  const executeOne = async command => {
    const sessionId = await ensureSession(command.sessionId)
    return runDeviceCommand(sessionController, sessionId, command)
  }

  return {
    supported,
    get sessionId() {
      return pinnedSessionId ?? null
    },
    execute(command) {
      if (!supported) {
        throw new HttpError(501, 'prompt_not_supported', 'This DSH runtime does not expose create, prompt, and follow.')
      }
      pruneDedupe()
      const requestId = command.requestId ?? randomUUID()
      const prepared = { ...command, requestId }
      const fingerprint = JSON.stringify([prepared.command, prepared.sessionId ?? null])
      let entry = dedupe.get(requestId)
      if (entry !== undefined && entry.fingerprint !== fingerprint) {
        throw new HttpError(409, 'request_id_conflict', 'requestId was already used for different command content or a different session.')
      }
      if (entry?.promise !== undefined) return entry.promise
      if (entry?.result !== undefined) return Promise.resolve(entry.result)
      if (queued >= config.maxQueuedDeviceCommands) {
        throw new HttpError(429, 'device_command_queue_full', 'Too many device commands are waiting for DSH.')
      }

      entry ??= { fingerprint, createdAt: Date.now() }
      dedupe.set(requestId, entry)
      queued += 1
      const task = queueTail.then(() => executeOne(prepared))
      queueTail = task.catch(() => {})
      entry.promise = task
      void task.then(result => {
        // A timed-out accepted command may still be running. Retain only its
        // fingerprint, so a retry with the same requestId observes DSH again;
        // DSH itself prevents the prompt from being inserted twice.
        if (result.status === 'completed') entry.result = result
      }, () => {
        // The HTTP caller receives the original rejection. This branch only
        // settles the detached bookkeeping promise.
      }).finally(() => {
        entry.promise = undefined
        queued -= 1
        pruneDedupe()
      })
      return task
    },
  }
}

async function resolveDeviceSessionCreateRequest(sessionController, wanted, config) {
  const expectedPreset = config.deviceAgentPreset ?? DEVICE_AGENT_PRESET
  const fresh = {
    ...(wanted === undefined ? {} : { sessionId: wanted }),
    ...(config.deviceWorkingDirectory === undefined ? {} : { cwd: config.deviceWorkingDirectory }),
    agentPreset: expectedPreset,
  }
  if (wanted === undefined) return fresh

  const lookupAbort = new AbortController()
  const timeout = setTimeout(
    () => lookupAbort.abort(new Error('device Session metadata lookup timed out')),
    config.requestTimeoutMs,
  )
  timeout.unref?.()
  try {
    const summary = await findSessionSummary(sessionController, wanted, lookupAbort.signal)
    // The caller pre-mints the first Session id. An id absent from the complete
    // list is therefore a new Session and keeps the configured default cwd.
    if (summary === undefined) return fresh
    if (summary?.origin === 'subagent') {
      throw new HttpError(409, 'device_session_unavailable', 'The saved device Session is a subagent Session and cannot be adopted.')
    }

    let preset = summary?.projections?.values?.agentPreset ?? summary?.agentPreset
    let cwd = typeof summary?.cwd === 'string' && summary.cwd.length > 0 ? summary.cwd : undefined
    if (preset === undefined && typeof sessionController.projections === 'function') {
      const projections = await sessionController.projections({ sessionId: wanted }, lookupAbort.signal)
      preset = projections?.values?.agentPreset
    }
    if (preset === undefined || cwd === undefined) {
      const inspection = await sessionController.inspect(wanted, lookupAbort.signal)
      preset ??= inspection?.meta?.agentPreset
      const inspectedCwd = inspection?.meta?.cwd
      if (cwd === undefined && typeof inspectedCwd === 'string' && inspectedCwd.length > 0) cwd = inspectedCwd
    }
    if (preset !== expectedPreset) {
      throw new HttpError(409, 'device_session_unavailable', 'The saved Session does not use the Halo device Agent preset.')
    }
    if (cwd === undefined) {
      throw new HttpError(409, 'device_session_unavailable', 'The saved device Session has no verifiable working directory.')
    }
    return { sessionId: wanted, cwd, agentPreset: expectedPreset }
  } finally {
    clearTimeout(timeout)
  }
}

async function runDeviceCommand(sessionController, sessionId, command) {
  const abort = new AbortController()
  const timeoutMs = command.timeoutSeconds * 1000
  const timeout = setTimeout(() => abort.abort(new Error('device command timed out')), timeoutMs)
  timeout.unref?.()
  let iterator
  let promptAccepted = false
  const collector = new DeviceCommandCollector(command.requestId, command.command)

  try {
    iterator = sessionController.follow({
      address: { kind: 'session', sessionId },
      maxMessages: 100,
    }, abort.signal)[Symbol.asyncIterator]()
    const opening = await iterator.next()
    if (opening.done || opening.value?.type !== 'snapshot') {
      throw new Error('DSH follow did not return an opening snapshot')
    }
    collector.acceptFrame(opening.value)

    // A retry can already be durable or still running. In either case, do not
    // submit it again. When it is outside the opening window, DSH's own rpcId
    // check remains the final duplicate-execution fence.
    if (!collector.matched) {
      const accepted = await sessionController.prompt({
        requestId: command.requestId,
        sessionId,
        mode: 'queue',
        content: [{ type: 'text', text: command.command }],
      }, abort.signal)
      if (accepted?.accepted !== true) throw new Error('DSH did not acknowledge the device command')
      promptAccepted = true
    } else {
      promptAccepted = true
    }

    while (!collector.complete) {
      const next = await iterator.next()
      if (next.done) {
        if (abort.signal.aborted) break
        throw new Error('DSH follow ended before the command turn completed')
      }
      collector.acceptFrame(next.value)
    }
    if (collector.complete) return collector.result(sessionId, command.requestId)
  } catch (error) {
    // Once DSH acknowledges admission (or its durable user event proves it),
    // an observation-stream failure cannot be reported as a rejection. The
    // caller must inspect history instead of resending. A local semantic
    // conflict remains a definite rejection even if it surfaces late.
    if (!abort.signal.aborted
      && (error instanceof HttpError || (!promptAccepted && !collector.matched))) throw error
  } finally {
    clearTimeout(timeout)
    if (!abort.signal.aborted) abort.abort(new Error('device command observation completed'))
    await iterator?.return?.().catch(() => {})
  }

  return collector.pendingResult(sessionId, command.requestId, promptAccepted || collector.matched)
}

class DeviceCommandCollector {
  constructor(requestId, command) {
    this.requestId = requestId
    this.command = command
    this.currentTurn = undefined
    this.targetTurn = undefined
    this.matched = false
    this.complete = false
    this.reason = undefined
    this.finalText = null
    this.partialText = null
    this.toolCalls = new Map()
    this.toolResults = new Map()
  }

  acceptFrame(frame) {
    if (frame?.type === 'snapshot') {
      for (const record of Array.isArray(frame.records) ? frame.records : []) {
        if (record?.type === 'event') this.acceptEvent(record.event)
      }
      return
    }
    if (frame?.type === 'event') this.acceptEvent(frame.event)
  }

  acceptEvent(event) {
    if (event === null || typeof event !== 'object') return
    const data = event.data
    if (event.type === 'turn/start' && Number.isSafeInteger(data?.turn)) {
      this.currentTurn = data.turn
    }

    if (event.type === 'user/message') {
      const source = data?.source ?? data?.message?.source
      if (source?.kind === 'user' && source.rpcId === this.requestId) {
        const durableCommand = contentText(data?.content ?? data?.message?.content)
        if (durableCommand !== this.command) {
          throw new HttpError(409, 'request_id_conflict', 'requestId is already attached to different durable prompt content.')
        }
        this.matched = true
        if (Number.isSafeInteger(this.currentTurn)) this.targetTurn = this.currentTurn
      }
    }

    const eventTurn = Number.isSafeInteger(data?.turn) ? data.turn : undefined
    if (this.matched && this.targetTurn === undefined && eventTurn !== undefined) {
      this.targetTurn = eventTurn
    }
    if (this.targetTurn !== undefined && eventTurn === this.targetTurn) {
      if (event.type === 'assistant/message') {
        const text = contentText(data?.message?.content)
        if (text.trim().length > 0) {
          const bounded = truncateUtf8(text, DEFAULT_MAX_ENTRY_TEXT_BYTES).text
          this.partialText = bounded
          if (data?.interrupted !== true) this.finalText = bounded
        }
      } else if (event.type === 'tool/call') {
        const callId = typeof data?.callId === 'string' ? data.callId : ''
        if (callId.length > 0 && !this.toolCalls.has(callId)) {
          this.toolCalls.set(callId, {
            sequence: Number.isSafeInteger(event.seq) ? event.seq : null,
            callId,
            name: typeof data?.name === 'string' ? truncateUtf8(data.name, 256).text : 'unknown',
          })
        }
      } else if (event.type === 'tool/result') {
        const message = data?.message
        const callId = typeof message?.toolCallId === 'string' ? message.toolCallId : ''
        if (callId.length > 0) {
          const renderedResult = contentText(message?.content)
          const bounded = truncateUtf8(renderedResult, MAX_TOOL_RESULT_BYTES)
          const pixelBarFailure = parsePixelBarDomainFailure(
            this.toolCalls.get(callId)?.name,
            renderedResult,
          )
          this.toolResults.set(callId, {
            sequence: Number.isSafeInteger(event.seq) ? event.seq : null,
            callId,
            status: message?.isError === true || data?.error !== undefined || pixelBarFailure !== null
              ? 'error'
              : 'completed',
            result: bounded.text,
            truncated: bounded.truncated,
            ...(typeof data?.error?.code === 'string'
              ? { errorCode: truncateUtf8(data.error.code, 256).text }
              : pixelBarFailure !== null
                ? { errorCode: pixelBarFailure.code ?? 'pixelbar/result-failed' }
                : {}),
          })
        }
      } else if (event.type === 'turn/end') {
        this.complete = true
        this.reason = data?.reason
      }
    }

    if (event.type === 'turn/end' && eventTurn === this.currentTurn) this.currentTurn = undefined
  }

  toolOutcomes() {
    const outcomes = []
    for (const call of this.toolCalls.values()) {
      if (outcomes.length >= MAX_TOOL_OUTCOMES) break
      const result = this.toolResults.get(call.callId)
      outcomes.push({
        ...call,
        status: result?.status ?? 'unknown',
        result: result?.result ?? '',
        truncated: result?.truncated ?? false,
        ...(result?.errorCode === undefined ? {} : { errorCode: result.errorCode }),
      })
    }
    return outcomes
  }

  result(sessionId, requestId) {
    const toolOutcomes = this.toolOutcomes()
    const reason = typeof this.reason?.kind === 'string' ? this.reason.kind : 'unknown'
    const allResults = [...this.toolResults.values()]
    const successfulToolCount = allResults.filter(outcome => outcome.status === 'completed').length
    const calledTools = toolOutcomes.map(outcome => outcome.name)
    const successfulTools = toolOutcomes
      .filter(outcome => outcome.status === 'completed')
      .map(outcome => outcome.name)
    const success = reason === 'completed'
      && successfulToolCount === this.toolCalls.size
      && allResults.every(outcome => outcome.status === 'completed')
    const finalText = this.finalText ?? this.partialText
    const fallbackMessage = reason !== 'completed'
      ? `DSH turn ended with ${reason}.`
      : success
        ? this.toolCalls.size > 0
          ? '设备工具已执行，但 DSH 未返回文字回复。'
          : 'DSH 已完成本轮，但未返回文字回复。'
        : '设备工具执行失败，且 DSH 未返回文字回复。'
    return {
      status: 'completed',
      success,
      sessionId,
      requestId,
      finalText,
      message: finalText ?? fallbackMessage,
      calledTools,
      successfulTools,
      calledToolCount: this.toolCalls.size,
      successfulToolCount,
      toolOutcomes,
      turn: this.targetTurn ?? null,
      turnEndReason: reason,
      truncated: this.toolCalls.size > toolOutcomes.length || toolOutcomes.some(outcome => outcome.truncated),
    }
  }

  pendingResult(sessionId, requestId, accepted) {
    const toolOutcomes = this.toolOutcomes()
    const calledTools = toolOutcomes.map(outcome => outcome.name)
    const successfulTools = toolOutcomes
      .filter(outcome => outcome.status === 'completed')
      .map(outcome => outcome.name)
    return {
      status: accepted ? 'accepted' : 'unknown',
      success: false,
      sessionId,
      requestId,
      finalText: this.finalText ?? this.partialText,
      message: accepted
        ? 'The command was accepted and may still be running. Retry with the same requestId to observe completion without repeating it.'
        : 'Command admission timed out. Retry only with the same requestId so DSH can deduplicate it.',
      calledTools,
      successfulTools,
      calledToolCount: this.toolCalls.size,
      successfulToolCount: [...this.toolResults.values()]
        .filter(outcome => outcome.status === 'completed').length,
      toolOutcomes,
      turn: this.targetTurn ?? null,
      turnEndReason: null,
      truncated: this.toolCalls.size > toolOutcomes.length || toolOutcomes.some(outcome => outcome.truncated),
    }
  }
}

async function readJsonBody(request, maximumBytes) {
  const contentType = String(request.headers['content-type'] ?? '').split(';', 1)[0].trim().toLowerCase()
  if (contentType !== 'application/json') {
    throw new HttpError(415, 'unsupported_media_type', 'Content-Type must be application/json.')
  }
  const declared = Number(request.headers['content-length'])
  if (Number.isFinite(declared) && declared > maximumBytes) {
    throw new HttpError(413, 'request_too_large', `The JSON body must not exceed ${maximumBytes} bytes.`)
  }
  const chunks = []
  let bytes = 0
  for await (const chunk of request) {
    bytes += chunk.length
    if (bytes > maximumBytes) {
      throw new HttpError(413, 'request_too_large', `The JSON body must not exceed ${maximumBytes} bytes.`)
    }
    chunks.push(chunk)
  }
  if (bytes === 0) throw new HttpError(400, 'invalid_json', 'A JSON request body is required.')
  try {
    const parsed = JSON.parse(Buffer.concat(chunks).toString('utf8'))
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('not an object')
    }
    return parsed
  } catch {
    throw new HttpError(400, 'invalid_json', 'The request body must be one JSON object.')
  }
}

function parseDeviceCommand(body) {
  if (typeof body.command !== 'string' || body.command.trim().length === 0) {
    throw new HttpError(400, 'invalid_command', 'command is required and must contain non-whitespace text.')
  }
  const command = body.command.trim()
  if (Buffer.byteLength(command, 'utf8') > MAX_COMMAND_TEXT_BYTES) {
    throw new HttpError(400, 'invalid_command', `command must not exceed ${MAX_COMMAND_TEXT_BYTES} UTF-8 bytes.`)
  }
  const sessionId = optionalBoundedString(body.sessionId, 'sessionId', 256)
  const requestId = optionalBoundedString(body.requestId, 'requestId', 128)
  if (requestId !== undefined && !/^[A-Za-z0-9][A-Za-z0-9._:-]*$/u.test(requestId)) {
    throw new HttpError(400, 'invalid_request_id', 'requestId must use only ASCII letters, digits, dot, underscore, colon, or hyphen.')
  }
  const timeoutSeconds = body.timeoutSeconds === undefined
    ? DEFAULT_COMMAND_TIMEOUT_MS / 1000
    : Number(body.timeoutSeconds)
  if (!Number.isSafeInteger(timeoutSeconds)
    || timeoutSeconds < MIN_COMMAND_TIMEOUT_SECONDS
    || timeoutSeconds > MAX_COMMAND_TIMEOUT_SECONDS) {
    throw new HttpError(400, 'invalid_timeout', `timeoutSeconds must be an integer from ${MIN_COMMAND_TIMEOUT_SECONDS} to ${MAX_COMMAND_TIMEOUT_SECONDS}.`)
  }
  return { command, sessionId, requestId, timeoutSeconds }
}

function optionalBoundedString(value, name, maximumLength) {
  if (value === undefined || value === null || value === '') return undefined
  if (typeof value !== 'string' || value.length > maximumLength || value.trim() !== value || value.length === 0) {
    throw new HttpError(400, `invalid_${name.replace(/[A-Z]/gu, letter => `_${letter.toLowerCase()}`)}`, `${name} must be a non-empty trimmed string of at most ${maximumLength} characters.`)
  }
  return value
}

function dshCommandError(error) {
  const code = typeof error?.code === 'string' ? error.code : ''
  const message = safeError(error)
  if (code === 'session/writer-held' || code === 'agent-preset/conflict' || code === 'session/conflict'
    || /already owned|writer[- ]held|writer lock/iu.test(message)) {
    return new HttpError(409, 'device_session_unavailable', message)
  }
  if (code === 'session/not-found') return new HttpError(404, 'session_not_found', message)
  if (code === 'gateway/bad-request' || code === 'session/title-invalid') {
    return new HttpError(400, 'dsh_rejected_command', message)
  }
  return new HttpError(502, 'dsh_command_failed', message)
}

async function runWithRequestSignal(request, response, timeoutMs, operation) {
  const abort = new AbortController()
  const timeout = setTimeout(() => abort.abort(new Error('request timed out')), timeoutMs)
  timeout.unref?.()
  const onAborted = () => abort.abort(new Error('client disconnected'))
  const onResponseClose = () => {
    if (!response.writableEnded) abort.abort(new Error('client disconnected'))
  }
  request.once('aborted', onAborted)
  response.once('close', onResponseClose)
  try {
    return await operation(abort.signal)
  } catch (error) {
    if (abort.signal.aborted && !(error instanceof HttpError)) {
      throw new HttpError(504, 'request_cancelled', 'The DSH task request was cancelled or timed out.')
    }
    throw error
  } finally {
    clearTimeout(timeout)
    request.off('aborted', onAborted)
    response.off('close', onResponseClose)
  }
}

async function withRequestSignal(request, response, timeoutMs, operation) {
  const abort = new AbortController()
  const timeout = setTimeout(() => abort.abort(new Error('request timed out')), timeoutMs)
  timeout.unref?.()
  const onAborted = () => abort.abort(new Error('client disconnected'))
  const onResponseClose = () => {
    if (!response.writableEnded) abort.abort(new Error('client disconnected'))
  }
  request.once('aborted', onAborted)
  response.once('close', onResponseClose)
  try {
    await operation(abort.signal)
  } catch (error) {
    if (response.headersSent || response.writableEnded) return
    if (abort.signal.aborted) {
      sendError(response, 504, 'request_cancelled', 'The DSH session read was cancelled or timed out.')
      return
    }
    const code = typeof error?.code === 'string' ? error.code : ''
    if (code === 'session/not-found' || error?.name === 'SessionPersistenceNotFoundError') {
      sendError(response, 404, 'session_not_found', 'The requested DSH session was not found.')
      return
    }
    sendError(response, 502, 'dsh_read_failed', safeError(error))
  } finally {
    clearTimeout(timeout)
    request.off('aborted', onAborted)
    response.off('close', onResponseClose)
  }
}

function foldSurfaceRecords(records) {
  const surface = []
  for (const record of records) {
    const event = record?.type === 'event' ? record.event : undefined
    if (event === null || typeof event !== 'object' || !Number.isSafeInteger(event.seq)) continue
    const operation = event.surfaceOp
    if (operation && typeof operation === 'object' && operation.op === 'replace') {
      const start = operation.startSeq
      const end = operation.endSeq
      if (Number.isSafeInteger(start) && Number.isSafeInteger(end)) {
        for (let index = surface.length - 1; index >= 0; index -= 1) {
          if (surface[index].seq >= start && surface[index].seq <= end) surface.splice(index, 1)
        }
      }
    }
    surface.push(event)
  }
  return surface
}

function eventCursor(event) {
  let cursor = event.seq
  if (Array.isArray(event.sourceEventSeqs)) {
    for (const source of event.sourceEventSeqs) {
      if (Number.isSafeInteger(source) && source >= 0 && source < cursor) cursor = source
    }
  }
  if (event.surfaceOp && typeof event.surfaceOp === 'object') {
    const start = event.surfaceOp.startSeq
    if (Number.isSafeInteger(start) && start >= 0 && start < cursor) cursor = start
  }
  return cursor
}

function eventToEntry(event, maxTextBytes, sessionId) {
  const role = roleForEvent(event.type)
  if (role === null) return null
  const message = event.type === 'user/message'
    ? (event.data?.message ?? event.data)
    : (event.data?.message ?? event.data)
  const text = contentText(message?.content)
  if (text.trim().length === 0) return null
  const bounded = truncateUtf8(text, maxTextBytes)
  return {
    sequence: event.seq,
    sessionId,
    role,
    kind: historyEntryKind(role, text),
    text: bounded.text,
    createdAt: toIsoTime(event.time),
    truncated: bounded.truncated,
  }
}

function historyEntryKind(role, text) {
  if (role === 'tool') return 'tool'
  if (role === 'system' || role === 'developer') return 'context'
  if (role === 'user' && text.startsWith(RUNTIME_CONTEXT_PREFIX)) return 'context'
  return 'conversation'
}

function contentText(content) {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  const chunks = []
  const visit = (value) => {
    if (typeof value === 'string') {
      chunks.push(value)
      return
    }
    if (value === null || typeof value !== 'object') return
    if (value.type === 'text' && typeof value.text === 'string') {
      chunks.push(value.text)
      return
    }
    // Tool-result blocks contain ordinary content blocks. Reasoning and tool
    // arguments are intentionally not flattened into the conversation view.
    if (value.type === 'tool-result' && Array.isArray(value.content)) {
      for (const child of value.content) visit(child)
    }
  }
  for (const block of content) visit(block)
  return chunks.join('\n')
}

function isPixelBarToolName(toolName) {
  return typeof toolName === 'string' && toolName.toLowerCase().includes('pixelbar')
}

function parsePixelBarDomainFailure(toolName, renderedResult) {
  if (!isPixelBarToolName(toolName)) return null
  try {
    const value = JSON.parse(renderedResult)
    if (value === null || typeof value !== 'object' || Array.isArray(value)
      || value.success !== false) return null
    return {
      code: typeof value.code === 'string' && value.code.length > 0
        ? truncateUtf8(value.code, 256).text
        : undefined,
    }
  } catch {
    // Other tools and legacy PixelBar results may render ordinary text. Only a
    // structured, top-level business failure changes the durable tool status.
    return null
  }
}

function roleForEvent(type) {
  switch (type) {
    case 'user/message': return 'user'
    case 'assistant/message': return 'assistant'
    case 'system/message': return 'system'
    case 'developer/message': return 'developer'
    case 'tool/result': return 'tool'
    default: return null
  }
}

function runtimeStatus(summary) {
  if (summary?.running === true) return 'running'
  if (summary?.agentAvailable === true) return 'idle'
  if (summary?.agentAvailable === false) return 'detached'
  return 'unknown'
}

function normalizeConfig(config) {
  const home = resolve(typeof config.home === 'string' && config.home.length > 0
    ? config.home
    : process.env.DSH_HOME || join(homedir(), '.dsh'))
  const discoveryHomeKey = home.replace(/[\\/]+$/u, '').toUpperCase()
  const discoveryDirectory = resolve(
    typeof config.discoveryDirectory === 'string' && config.discoveryDirectory.length > 0
      ? config.discoveryDirectory
      : join(
        process.env.LOCALAPPDATA || homedir(),
        'HaloPixelToolBox',
        'DshSessionBridge',
        createHash('sha256').update(discoveryHomeKey, 'utf8').digest('hex'),
      ),
  )
  const profile = typeof config.profile === 'string' && config.profile.length > 0
    ? config.profile
    : 'unknown'
  const classificationCacheScope = createHash('sha256')
    .update(`${discoveryHomeKey}\0${profile}`, 'utf8')
    .digest('hex')
  const classificationRuleFileKey = createHash('sha256')
    .update(CLASSIFICATION_RULE_VERSION, 'utf8')
    .digest('hex')
    .slice(0, 12)
  return {
    home,
    discoveryDirectory,
    profile,
    classificationCacheScope,
    classificationCachePath: join(
      discoveryDirectory,
      `session-classifications.v${CLASSIFICATION_CACHE_SCHEMA_VERSION}.${classificationRuleFileKey}.${classificationCacheScope.slice(0, 24)}.cache`,
    ),
    webUrl: optionalHttpUrl(config.webUrl),
    ownedStartupId: optionalOwnedStartupId(config.ownedStartupId),
    deviceSessionId: optionalConfigString(config.deviceSessionId, 'deviceSessionId', 256),
    deviceAgentPreset: optionalConfigString(config.deviceAgentPreset, 'deviceAgentPreset', 128),
    deviceWorkingDirectory: typeof config.deviceWorkingDirectory === 'string' && config.deviceWorkingDirectory.length > 0
      ? resolve(config.deviceWorkingDirectory)
      : undefined,
    deviceSessionTitle: config.deviceSessionTitle === false
      ? undefined
      : optionalConfigString(config.deviceSessionTitle ?? '音箱控制', 'deviceSessionTitle', 256),
    taskAgentPreset: optionalConfigString(config.taskAgentPreset ?? TASK_AGENT_PRESET, 'taskAgentPreset', 128),
    maxQueuedDeviceCommands: boundedInteger(config.maxQueuedDeviceCommands, 1, 64, 16),
    maxLegacySessionProbesPerList: boundedInteger(
      config.maxLegacySessionProbesPerList,
      0,
      100,
      DEFAULT_LEGACY_SESSION_PROBES_PER_LIST,
    ),
    maxLegacySessionInspectsPerList: boundedInteger(
      config.maxLegacySessionInspectsPerList,
      0,
      10,
      DEFAULT_LEGACY_SESSION_INSPECTS_PER_LIST,
    ),
    port: boundedInteger(config.port, 0, 65_535, 0),
    requestTimeoutMs: boundedInteger(config.requestTimeoutMs, 1_000, 120_000, DEFAULT_REQUEST_TIMEOUT_MS),
    maxEntryTextBytes: boundedInteger(config.maxEntryTextBytes, 256, 64 * 1024, DEFAULT_MAX_ENTRY_TEXT_BYTES),
    maxPageTextBytes: boundedInteger(config.maxPageTextBytes, 1024, 1024 * 1024, DEFAULT_MAX_PAGE_TEXT_BYTES),
  }
}

function assertSessionController(controller) {
  const required = ['list', 'inspect', 'page']
  const missing = required.filter(name => typeof controller?.[name] !== 'function')
  if (missing.length > 0) {
    throw new Error(`halo-session-bridge: unsupported DSH sessionController; missing ${missing.join(', ')}`)
  }
}

function parseLimit(value) {
  if (value === null || value.length === 0) return DEFAULT_HISTORY_LIMIT
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < 1 || parsed > MAX_HISTORY_LIMIT) {
    throw new HttpInputError('limit must be an integer from 1 to 100')
  }
  return parsed
}

function parseOptionalNonNegativeInteger(value, name) {
  if (value === null || value.length === 0) return undefined
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < 0 || Object.is(parsed, -0)) {
    throw new HttpInputError(`${name} must be a non-negative integer`)
  }
  return parsed
}

class HttpInputError extends Error {}

class HttpError extends Error {
  constructor(status, code, message) {
    super(message)
    this.status = status
    this.code = code
  }
}

function boundedInteger(value, minimum, maximum, fallback) {
  if (value === undefined || value === null || value === '') return fallback
  const parsed = Number(value)
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw new Error(`halo-session-bridge: expected an integer from ${minimum} to ${maximum}`)
  }
  return parsed
}

function optionalHttpUrl(value) {
  if (typeof value !== 'string' || value.length === 0) return undefined
  const parsed = new URL(value)
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error('halo-session-bridge: webUrl must use http or https')
  }
  return parsed.href
}

function optionalOwnedStartupId(value) {
  if (value === undefined || value === null || value === '') return undefined
  if (typeof value !== 'string' || !/^[0-9a-fA-F]{32}$/u.test(value)) {
    throw new Error('halo-session-bridge: ownedStartupId must be a 32-character hexadecimal GUID')
  }
  return value
}

function optionalConfigString(value, name, maximumLength) {
  if (value === undefined || value === null || value === '') return undefined
  if (typeof value !== 'string' || value.length > maximumLength || value.trim() !== value) {
    throw new Error(`halo-session-bridge: ${name} must be a trimmed string of at most ${maximumLength} characters`)
  }
  return value
}

function authorized(header, expected) {
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) return false
  const supplied = Buffer.from(header.slice('Bearer '.length), 'utf8')
  const wanted = Buffer.from(expected, 'utf8')
  return supplied.length === wanted.length && timingSafeEqual(supplied, wanted)
}

function truncateUtf8(value, maximumBytes) {
  if (Buffer.byteLength(value, 'utf8') <= maximumBytes) return { text: value, truncated: false }
  let bytes = 0
  let output = ''
  for (const character of value) {
    const size = Buffer.byteLength(character, 'utf8')
    if (bytes + size > maximumBytes) break
    output += character
    bytes += size
  }
  return { text: output, truncated: true }
}

function toIsoTime(value) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

function applySecurityHeaders(response) {
  response.setHeader('Cache-Control', 'no-store')
  response.setHeader('Content-Security-Policy', "default-src 'none'; frame-ancestors 'none'")
  response.setHeader('Referrer-Policy', 'no-referrer')
  response.setHeader('X-Content-Type-Options', 'nosniff')
  response.setHeader('X-Frame-Options', 'DENY')
}

function sendJson(response, status, value) {
  if (response.writableEnded) return
  const body = JSON.stringify(value)
  response.statusCode = status
  response.setHeader('Content-Type', 'application/json; charset=utf-8')
  response.setHeader('Content-Length', Buffer.byteLength(body))
  response.end(body)
}

function sendError(response, status, code, message) {
  sendJson(response, status, { error: { code, message } })
}

function safeError(error) {
  if (error instanceof HttpInputError) return error.message
  if (error instanceof Error && typeof error.message === 'string' && error.message.length > 0) {
    return error.message.slice(0, 1000)
  }
  return 'Unknown DSH session read failure'
}

async function listen(server, port) {
  await new Promise((resolvePromise, reject) => {
    const onError = error => reject(error)
    server.once('error', onError)
    server.listen(port, LOOPBACK_HOST, () => {
      server.off('error', onError)
      resolvePromise()
    })
  })
}

async function closeServer(server, sockets) {
  if (!server.listening) return
  const closed = new Promise(resolvePromise => server.close(resolvePromise))
  if (typeof server.closeAllConnections === 'function') server.closeAllConnections()
  else for (const socket of sockets) socket.destroy()
  await closed
}

async function writeDescriptor(path, descriptor) {
  await mkdir(dirname(path), { recursive: true, mode: 0o700 })
  const temporary = `${path}.${randomBytes(8).toString('hex')}.tmp`
  try {
    await writeFile(temporary, `${JSON.stringify(descriptor, null, 2)}\n`, {
      encoding: 'utf8',
      mode: 0o600,
      flag: 'wx',
    })
    await rename(temporary, path)
    await chmod(path, 0o600).catch(() => {})
  } finally {
    await rm(temporary, { force: true }).catch(() => {})
  }
}

async function removeOwnedDescriptor(path, token) {
  try {
    const current = JSON.parse(await readFile(path, 'utf8'))
    if (current?.token !== token) return
    await rm(path, { force: true })
  } catch (error) {
    if (error?.code !== 'ENOENT') return
  }
}

function removeOwnedDescriptorSync(path, token) {
  try {
    const current = JSON.parse(readFileSync(path, 'utf8'))
    if (current?.token === token) rmSync(path, { force: true })
  } catch {
    // Process-exit cleanup is best effort; stale descriptors are rejected by PID
    // and authenticated status checks on the next client discovery pass.
  }
}

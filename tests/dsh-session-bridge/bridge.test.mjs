import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdir, mkdtemp, readFile, readdir, rm, stat, utimes, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import {
  createDeviceCommandCoordinator,
  createDeviceSessionClassifier,
  createTaskInteractionBroker,
  normalizeHistoryPage,
  normalizeSessionSummary,
  startSessionBridge,
} from '../../integrations/deepseek-harness/halo-session-bridge/bridge-core.js'

test('normalizes DSH summaries without guessing old-runtime attachment state', () => {
  assert.deepEqual(normalizeSessionSummary({
    sessionId: 'session-1',
    updatedAt: Date.UTC(2026, 8, 30, 12),
    running: false,
    cwd: 'C:/work',
    projections: { values: { title: 'Test session' } },
  }), {
    id: 'session-1',
    title: 'Test session',
    workingDirectory: 'C:/work',
    updatedAt: '2026-09-30T12:00:00.000Z',
    runtimeStatus: 'unknown',
    isDeviceControl: false,
    deviceControlKind: null,
    isArchived: false,
  })

  assert.equal(normalizeSessionSummary({ running: true }).runtimeStatus, 'running')
  assert.equal(normalizeSessionSummary({ running: false, agentAvailable: true }).runtimeStatus, 'idle')
  assert.equal(normalizeSessionSummary({ running: false, agentAvailable: false }).runtimeStatus, 'detached')
  const canonicalSummary = normalizeSessionSummary({
    projections: { values: { agentPreset: 'halo-device', title: '任意标题' } },
  })
  assert.equal(canonicalSummary.isDeviceControl, true)
  assert.equal(canonicalSummary.deviceControlKind, 'canonical')
  const titledSummary = normalizeSessionSummary({
    projections: { values: { title: '音箱控制' } },
  })
  assert.equal(titledSummary.isDeviceControl, false)
  assert.equal(titledSummary.deviceControlKind, null)
})

test('normalizes history text, replacements, cursors, and truncation', () => {
  const page = normalizeHistoryPage({
    records: [
      eventRecord(1, 'user/message', { role: 'user', content: [{ type: 'text', text: 'old' }] }),
      eventRecord(2, 'user/message', { role: 'user', content: [{ type: 'text', text: 'new value' }] }, {
        op: 'replace', startSeq: 1, endSeq: 1,
      }),
      eventRecord(3, 'assistant/message', {
        message: { role: 'assistant', content: [{ type: 'reasoning', text: 'hidden' }, { type: 'text', text: 'answer' }] },
      }),
    ],
    hasMore: true,
  }, 5, 100)

  assert.deepEqual(page, {
    entries: [
      { sequence: 2, sessionId: '', role: 'user', kind: 'conversation', text: 'new v', createdAt: '2026-09-30T12:00:00.000Z', truncated: true },
      { sequence: 3, sessionId: '', role: 'assistant', kind: 'conversation', text: 'answe', createdAt: '2026-09-30T12:00:00.000Z', truncated: true },
    ],
    beforeSeq: 1,
    hasMore: true,
    truncated: true,
  })
})

test('a page byte cut retains the newest suffix and advances from that suffix', () => {
  const page = normalizeHistoryPage({
    records: [
      eventRecord(10, 'user/message', { role: 'user', content: [{ type: 'text', text: 'older' }] }),
      eventRecord(11, 'assistant/message', { message: { role: 'assistant', content: [{ type: 'text', text: 'newer' }] } }),
    ],
    hasMore: false,
  }, 100, 5)

  assert.deepEqual(page, {
    entries: [{ sequence: 11, sessionId: '', role: 'assistant', kind: 'conversation', text: 'newer', createdAt: '2026-09-30T12:00:00.000Z', truncated: false }],
    beforeSeq: 11,
    hasMore: true,
    truncated: true,
  })
})

test('classifies history context, tool, and conversation entries with their source session', () => {
  const runtimeContext = 'Current runtime context. This snapshot supersedes earlier runtime-context snapshots. cwd=/work'
  const page = normalizeHistoryPage({
    records: [
      eventRecord(0, 'system/message', { content: [{ type: 'text', text: 'system' }] }),
      eventRecord(1, 'developer/message', { message: { content: [{ type: 'text', text: 'developer' }] } }),
      eventRecord(2, 'user/message', { content: [{ type: 'text', text: runtimeContext }] }),
      eventRecord(3, 'user/message', { content: [{ type: 'text', text: 'hello' }] }),
      eventRecord(4, 'tool/result', {
        message: { role: 'tool', toolCallId: 'call-1', content: [{ type: 'text', text: 'result' }] },
      }),
      eventRecord(5, 'assistant/message', {
        message: { content: [{ type: 'text', text: 'answer' }] },
      }),
    ],
    hasMore: false,
  }, 1024, 8192, 'source-session')

  assert.deepEqual(page.entries.map(entry => [entry.sessionId, entry.role, entry.kind]), [
    ['source-session', 'system', 'context'],
    ['source-session', 'developer', 'context'],
    ['source-session', 'user', 'context'],
    ['source-session', 'user', 'conversation'],
    ['source-session', 'tool', 'tool'],
    ['source-session', 'assistant', 'conversation'],
  ])
})

test('identifies canonical and exact legacy device sessions incrementally with a bounded cache', async () => {
  const legacyPrefix = '你是 Halo PixelBar 的语音助手。请严格执行下面的用户口令；优先调用可用的 PixelBar 工具完成操作；完成后只用一句简短中文说明结果，不使用 Markdown。不要改变、扩展或猜测用户原意。用户口令：'
  const pageCalls = []
  const inspectCalls = []
  const sessionController = {
    async page(request) {
      pageCalls.push(request.address.sessionId)
      const text = request.address.sessionId.startsWith('legacy')
        ? `${legacyPrefix}关闭氛围灯`
        : `${legacyPrefix.replace('请严格执行', '请执行')}普通会话`
      return {
        records: [eventRecord(0, 'user/message', { content: [{ type: 'text', text }] })],
        hasMore: false,
      }
    },
    async inspect(sessionId) {
      inspectCalls.push(sessionId)
      return {
        meta: { agentPreset: sessionId === 'canonical-fallback' ? 'halo-device' : 'standard' },
        events: [],
      }
    },
  }
  const summaries = [
    { sessionId: 'canonical', updatedAt: 1, projections: { asOfSeq: 1, values: { agentPreset: 'halo-device' } } },
    { sessionId: 'legacy-1', updatedAt: 2, projections: { asOfSeq: 5, values: { agentPreset: 'standard' } } },
    { sessionId: 'ordinary', updatedAt: 3, projections: { asOfSeq: 5, values: { agentPreset: 'standard' } } },
    { sessionId: 'legacy-2', updatedAt: 4, projections: { asOfSeq: 5, values: { agentPreset: null } } },
    { sessionId: 'canonical-fallback', updatedAt: 5 },
  ]
  const classifier = createDeviceSessionClassifier(sessionController, {
    maxLegacySessionProbesPerList: 2,
    maxLegacySessionInspectsPerList: 1,
  }, { warn() {} })

  const first = await classifier.classify(summaries)
  assert.equal(first.get('canonical'), 'canonical')
  assert.equal(first.get('legacy-1'), 'legacy')
  assert.equal(first.get('ordinary'), null)
  assert.equal(first.get('legacy-2'), undefined)
  assert.deepEqual(pageCalls, ['legacy-1', 'ordinary'])

  const second = await classifier.classify(summaries)
  assert.equal(second.get('legacy-2'), 'legacy')
  assert.equal(second.get('canonical-fallback'), 'canonical')
  assert.deepEqual(pageCalls, ['legacy-1', 'ordinary', 'legacy-2'])
  assert.deepEqual(inspectCalls, ['ordinary', 'canonical-fallback'])

  await classifier.classify(summaries)
  assert.deepEqual(pageCalls, ['legacy-1', 'ordinary', 'legacy-2'])
  assert.deepEqual(inspectCalls, ['ordinary', 'canonical-fallback'])
})

test('classifies only structurally pure manual PixelBar tool histories as legacy', async () => {
  const histories = new Map([
    ['manual', [
      rawEvent(0, 'user/message', { content: [{ type: 'text', text: 'device request' }] }),
      rawEvent(1, 'tool/call', { name: 'get_pixelbar_status', arguments: '{}' }),
      rawEvent(2, 'tool/result', { message: { role: 'tool', toolCallId: 'call-1', content: [] } }),
    ]],
    ['manual-light-effect', [
      rawEvent(0, 'user/message', { content: [{ type: 'text', text: 'change effect' }] }),
      rawEvent(1, 'tool/call', { name: 'set_pixelbar_light_effect', arguments: '{"mode":"next"}' }),
      rawEvent(2, 'tool/result', { message: { role: 'tool', toolCallId: 'call-effect', content: [] } }),
    ]],
    ['mixed-tools', [
      rawEvent(0, 'tool/call', { name: 'get_pixelbar_status', arguments: '{}' }),
      rawEvent(1, 'tool/call', { name: 'pwsh', arguments: '{}' }),
    ]],
    ['workflow', [
      rawEvent(0, 'tool/call', { name: 'configure_pixelbar', arguments: '{}' }),
      rawEvent(1, 'todo/write', { todos: [] }),
    ]],
    ['attachment', [
      rawEvent(0, 'user/message', { content: [{ type: 'image', data: 'opaque' }] }),
      rawEvent(1, 'tool/call', { name: 'set_pixelbar_volume', arguments: '{}' }),
    ]],
    ['lookalike-tool', [
      rawEvent(0, 'tool/call', { name: 'delete_pixelbar_everything', arguments: '{}' }),
    ]],
    ['title-only', [
      rawEvent(0, 'user/message', { content: [{ type: 'text', text: 'PixelBar' }] }),
    ]],
  ])
  const inspected = []
  const classifier = createDeviceSessionClassifier({
    async inspect(sessionId) {
      inspected.push(sessionId)
      return { meta: { agentPreset: 'standard' }, events: histories.get(sessionId) }
    },
  }, {
    maxLegacySessionProbesPerList: histories.size,
    maxLegacySessionInspectsPerList: histories.size,
  }, { warn() {} })
  const summaries = [...histories.keys()].map((sessionId, index) => ({
    sessionId,
    updatedAt: index + 1,
  }))

  const classified = await classifier.classify(summaries)
  assert.equal(classified.get('manual'), 'legacy')
  assert.equal(classified.get('manual-light-effect'), 'legacy')
  assert.equal(classified.get('mixed-tools'), null)
  assert.equal(classified.get('workflow'), null)
  assert.equal(classified.get('attachment'), null)
  assert.equal(classified.get('lookalike-tool'), null)
  assert.equal(classified.get('title-only'), null)
  assert.deepEqual(inspected, [...histories.keys()])
})

test('persists classification metadata without history text and invalidates changed sessions', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-cache-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  const config = {
    classificationCachePath: cachePath,
    classificationCacheScope: 'a'.repeat(64),
  }
  const calls = []
  const controller = {
    async page(request) {
      calls.push(['page', request.throughSeq])
      return { records: [], hasMore: false }
    },
    async inspect() {
      calls.push(['inspect'])
      return {
        meta: { agentPreset: 'standard' },
        events: [
          rawEvent(0, 'user/message', { content: [{ type: 'text', text: 'private device request' }] }),
          rawEvent(1, 'tool/call', { name: 'get_pixelbar_status', arguments: '{}' }),
        ],
      }
    },
  }
  const summary = {
    sessionId: 'persisted-session',
    updatedAt: 100,
    cwd: 'C:/private/workspace',
    projections: {
      asOfSeq: 5,
      values: { title: 'private title', agentPreset: 'standard' },
    },
  }

  const first = createDeviceSessionClassifier(controller, config, { warn() {} })
  assert.equal((await first.classify([summary])).get(summary.sessionId), 'legacy')
  assert.deepEqual(calls, [['page', 5], ['inspect']])
  const serialized = await readFile(cachePath, 'utf8')
  assert.equal(serialized.includes('private device request'), false)
  assert.equal(serialized.includes('private title'), false)
  assert.equal(serialized.includes('C:/private/workspace'), false)
  assert.equal(serialized.includes('Bearer'), false)
  const persisted = JSON.parse(serialized)
  assert.deepEqual(Object.keys(persisted.entries[0]).sort(), [
    'key', 'projectionCursor', 'sessionId', 'updatedAt', 'value',
  ])
  assert.match(persisted.entries[0].key, /^[a-f0-9]{64}$/u)

  calls.length = 0
  const cold = createDeviceSessionClassifier(controller, config, { warn() {} })
  assert.equal((await cold.classify([summary])).get(summary.sessionId), 'legacy')
  assert.deepEqual(calls, [])

  const changed = {
    ...summary,
    updatedAt: 101,
    projections: { ...summary.projections, asOfSeq: 6 },
  }
  assert.equal((await cold.classify([changed])).get(summary.sessionId), 'legacy')
  assert.deepEqual(calls, [['page', 6], ['inspect']])
})

test('does not reuse or persist a classification without a revision marker', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-no-revision-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  const config = {
    classificationCachePath: cachePath,
    classificationCacheScope: 'b'.repeat(64),
  }
  let inspections = 0
  const controller = {
    async inspect() {
      inspections += 1
      return {
        meta: { agentPreset: 'standard' },
        events: [rawEvent(1, 'tool/call', { name: 'get_pixelbar_status', arguments: '{}' })],
      }
    },
  }
  const summary = { sessionId: 'missing-revision' }
  const classifier = createDeviceSessionClassifier(controller, config, { warn() {} })

  assert.equal((await classifier.classify([summary])).get(summary.sessionId), 'legacy')
  assert.equal((await classifier.classify([summary])).get(summary.sessionId), 'legacy')
  assert.equal(inspections, 2)
  await assert.rejects(readFile(cachePath, 'utf8'), { code: 'ENOENT' })
})

test('merges concurrent classification cache writers without losing either session', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-merge-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  const config = {
    classificationCachePath: cachePath,
    classificationCacheScope: 'b'.repeat(64),
  }
  const controller = {
    async inspect(sessionId) {
      return {
        meta: { agentPreset: sessionId === 'canonical' ? 'halo-device' : 'standard' },
        events: sessionId === 'legacy'
          ? [rawEvent(0, 'tool/call', { name: 'set_pixelbar_volume', arguments: '{}' })]
          : [],
      }
    },
  }
  const first = createDeviceSessionClassifier(controller, config, { warn() {} })
  const second = createDeviceSessionClassifier(controller, config, { warn() {} })

  await Promise.all([
    first.classify([{ sessionId: 'canonical', updatedAt: 1 }]),
    second.classify([{ sessionId: 'legacy', updatedAt: 2 }]),
  ])
  const document = JSON.parse(await readFile(cachePath, 'utf8'))
  assert.deepEqual(document.entries.map(entry => entry.sessionId).sort(), ['canonical', 'legacy'])
})

test('recovers a stale classification cache lock left by a terminated writer', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-lock-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  const lockPath = `${cachePath}.lock`
  await writeFile(lockPath, '', 'utf8')
  const staleTime = new Date(Date.now() - 60_000)
  await utimes(lockPath, staleTime, staleTime)
  const classifier = createDeviceSessionClassifier({
    async inspect() { return { meta: { agentPreset: 'standard' }, events: [] } },
  }, {
    classificationCachePath: cachePath,
    classificationCacheScope: 'e'.repeat(64),
  }, { warn() {} })

  assert.equal((await classifier.classify([{ sessionId: 'ordinary', updatedAt: 1 }])).get('ordinary'), null)
  assert.equal(JSON.parse(await readFile(cachePath, 'utf8')).entries.length, 1)
  await assert.rejects(stat(lockPath), error => error.code === 'ENOENT')
})

test('retries a transient classification cache write failure on the next listing', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-retry-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  await mkdir(cachePath)
  let inspections = 0
  const warnings = []
  const classifier = createDeviceSessionClassifier({
    async inspect() {
      inspections += 1
      return { meta: { agentPreset: 'standard' }, events: [] }
    },
  }, {
    classificationCachePath: cachePath,
    classificationCacheScope: 'f'.repeat(64),
  }, {
    warn(message) { warnings.push(message) },
  })
  const summary = { sessionId: 'ordinary', updatedAt: 1 }

  assert.equal((await classifier.classify([summary])).get('ordinary'), null)
  assert.equal(warnings.length > 0, true)
  await rm(cachePath, { recursive: true, force: true })
  assert.equal((await classifier.classify([summary])).get('ordinary'), null)
  assert.equal(JSON.parse(await readFile(cachePath, 'utf8')).entries.length, 1)
  assert.equal(inspections, 1)
})

test('ignores stale-rule and corrupt classification caches without failing the list', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-classification-invalid-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const cachePath = join(directory, 'classifications.json')
  const scope = 'c'.repeat(64)
  const config = { classificationCachePath: cachePath, classificationCacheScope: scope }
  let inspections = 0
  const controller = {
    async inspect() {
      inspections += 1
      return { meta: { agentPreset: 'standard' }, events: [] }
    },
  }
  const summary = { sessionId: 'ordinary', updatedAt: 1 }
  const seed = createDeviceSessionClassifier(controller, config, { warn() {} })
  assert.equal((await seed.classify([summary])).get('ordinary'), null)

  const stale = JSON.parse(await readFile(cachePath, 'utf8'))
  stale.ruleVersion = 'obsolete-rule'
  stale.entries[0].value = 'legacy'
  await writeFile(cachePath, JSON.stringify(stale), 'utf8')
  const afterRuleChange = createDeviceSessionClassifier(controller, config, { warn() {} })
  assert.equal((await afterRuleChange.classify([summary])).get('ordinary'), null)

  const wrongScope = JSON.parse(await readFile(cachePath, 'utf8'))
  wrongScope.scope = 'd'.repeat(64)
  wrongScope.entries[0].value = 'legacy'
  await writeFile(cachePath, JSON.stringify(wrongScope), 'utf8')
  const afterScopeChange = createDeviceSessionClassifier(controller, config, { warn() {} })
  assert.equal((await afterScopeChange.classify([summary])).get('ordinary'), null)

  await writeFile(cachePath, '{broken', 'utf8')
  const warnings = []
  const afterCorruption = createDeviceSessionClassifier(controller, config, {
    warn(message) { warnings.push(message) },
  })
  assert.equal((await afterCorruption.classify([summary])).get('ordinary'), null)
  assert.equal(inspections, 4)
  assert.equal(warnings.length > 0, true)
})

test('bounds the default full-history classification budget at twelve per listing', async () => {
  const inspected = []
  const classifier = createDeviceSessionClassifier({
    async inspect(sessionId) {
      inspected.push(sessionId)
      return { meta: { agentPreset: 'standard' }, events: [] }
    },
  }, {}, { warn() {} })
  const summaries = Array.from({ length: 13 }, (_, index) => ({
    sessionId: `session-${index}`,
    updatedAt: index,
  }))

  await classifier.classify(summaries)
  assert.equal(inspected.length, 12)
  await classifier.classify(summaries)
  assert.equal(inspected.length, 13)
})

test('defers uncached classification after the six-second soft deadline', async () => {
  let clock = 0
  const inspected = []
  const classifier = createDeviceSessionClassifier({
    async inspect(sessionId) {
      inspected.push(sessionId)
      clock += 3_100
      return { meta: { agentPreset: 'standard' }, events: [] }
    },
  }, {
    now: () => clock,
  }, { warn() {} })
  const summaries = Array.from({ length: 4 }, (_, index) => ({
    sessionId: `timed-${index}`,
    updatedAt: index,
  }))

  const first = await classifier.classify(summaries)
  assert.deepEqual(inspected, ['timed-0', 'timed-1'])
  assert.equal(first.get('timed-0'), null)
  assert.equal(first.get('timed-1'), null)
  assert.equal(first.get('timed-2'), undefined)

  await classifier.classify(summaries)
  assert.deepEqual(inspected, ['timed-0', 'timed-1', 'timed-2', 'timed-3'])
})

test('serves authenticated list and paged history and removes its descriptor', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-bridge-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const calls = []
  const sessionController = {
    async list(_request, signal) {
      assert.equal(signal.aborted, false)
      calls.push('list')
      return {
        items: [
          {
            sessionId: 'main', running: true, updatedAt: 1,
            projections: { values: { title: 'Main' } },
          },
          { sessionId: 'child', origin: 'subagent', running: false, updatedAt: 1 },
        ],
      }
    },
    async inspect(sessionId, signal) {
      assert.equal(sessionId, 'main')
      assert.equal(signal.aborted, false)
      calls.push('inspect')
      return { events: [{ seq: 0 }, { seq: 1 }, { seq: 2 }] }
    },
    async page(request, signal) {
      assert.equal(signal.aborted, false)
      if (calls.filter(call => call === 'page').length === 0) {
        assert.deepEqual(request, {
          address: { kind: 'session', sessionId: 'main' },
          throughSeq: 2,
          maxMessages: 10,
        })
      } else {
        assert.deepEqual(request, {
          address: { kind: 'session', sessionId: 'main' },
          throughSeq: 1,
          beforeSeq: 2,
          maxMessages: 10,
        })
      }
      calls.push('page')
      return {
        records: [eventRecord(0, 'user/message', {
          role: 'user', content: [{ type: 'text', text: 'hello' }],
        })],
        hasMore: false,
      }
    },
  }

  const bridge = await startSessionBridge({
    sessionController,
    config: {
      discoveryDirectory: directory,
      home: join(directory, 'dsh-home'),
      profile: 'test',
      ownedStartupId: '0123456789abcdef0123456789abcdef',
      maxLegacySessionProbesPerList: 0,
    },
    logger: { info() {}, warn() {} },
  })

  const descriptor = JSON.parse(await readFile(bridge.descriptorPath, 'utf8'))
  assert.equal(descriptor.ownedStartupId, '0123456789abcdef0123456789abcdef')
  const authorization = { Authorization: `Bearer ${descriptor.token}` }
  assert.equal((await fetch(`${descriptor.baseUrl}v1/status`)).status, 401)
  const statusResponse = await fetch(`${descriptor.baseUrl}v1/status`, { headers: authorization })
  assert.equal(statusResponse.status, 200)
  const statusBody = await statusResponse.json()
  assert.equal(statusBody.ownedStartupId, '0123456789abcdef0123456789abcdef')
  assert.equal(statusBody.deviceSessionId, null)
  assert.equal(statusBody.capabilities.deviceHistoryGrouping, true)

  const sessionsResponse = await fetch(`${descriptor.baseUrl}v1/sessions`, { headers: authorization })
  assert.equal(sessionsResponse.status, 200)
  const sessions = await sessionsResponse.json()
  assert.equal(sessions.sessions.length, 1)
  assert.equal(sessions.sessions[0].id, 'main')
  assert.equal(sessions.sessions[0].isDeviceControl, false)
  assert.equal(sessions.sessions[0].deviceControlKind, null)

  const historyResponse = await fetch(
    `${descriptor.baseUrl}v1/history?sessionId=main&limit=10`,
    { headers: authorization },
  )
  assert.equal(historyResponse.status, 200)
  const history = await historyResponse.json()
  assert.equal(history.entries[0].text, 'hello')
  assert.equal(history.entries[0].sessionId, 'main')
  assert.equal(history.entries[0].kind, 'conversation')

  const olderResponse = await fetch(
    `${descriptor.baseUrl}v1/history?sessionId=main&beforeSeq=2&limit=10`,
    { headers: authorization },
  )
  assert.equal(olderResponse.status, 200)
  assert.deepEqual(calls, ['list', 'inspect', 'page', 'page'])

  await bridge.close()
  await assert.rejects(stat(bridge.descriptorPath), error => error.code === 'ENOENT')
})

test('serializes device commands in one named session and deduplicates requestId', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-command-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const stream = eventStream()
  const calls = []
  let sequence = 0
  let turn = 0
  const sessionController = {
    async list() { return { items: [] } },
    async inspect() { return { events: [] } },
    async page() { return { records: [], hasMore: false } },
    async create(request) {
      calls.push(['create', request])
      return { sessionId: request.sessionId ?? 'session-device' }
    },
    async rename(request) {
      calls.push(['rename', request])
      return { title: request.title, seq: sequence++ }
    },
    follow(request, signal) {
      calls.push(['follow', request])
      return stream.follow(signal)
    },
    async prompt(request, signal) {
      assert.equal(signal.aborted, false)
      calls.push(['prompt', request])
      turn += 1
      const current = turn
      const shouldFail = request.content.some(part => part?.text === '模拟设备拒绝')
      stream.push(commandEvent(sequence++, 'turn/start', { turn: current }))
      stream.push(commandEvent(sequence++, 'user/message', {
        role: 'user',
        content: request.content,
        source: { kind: 'user', rpcId: request.requestId },
      }))
      stream.push(commandEvent(sequence++, 'step/start', { turn: current, step: 1 }))
      stream.push(commandEvent(sequence++, 'tool/call', {
        turn: current, step: 1, callId: `call-${current}`, name: 'set_pixelbar_volume', arguments: '{"volume":13}',
      }))
      stream.push(commandEvent(sequence++, 'tool/result', {
        turn: current,
        step: 1,
        message: {
          role: 'tool', toolCallId: `call-${current}`, content: [{
            type: 'text',
            text: shouldFail
              ? JSON.stringify({
                success: false,
                status: 'deviceError',
                code: 'device/rejected',
                message: '设备拒绝了请求',
                retryable: false,
              })
              : 'volume set',
          }],
        },
      }))
      stream.push(commandEvent(sequence++, 'assistant/message', {
        turn: current,
        step: 1,
        message: { role: 'assistant', content: [{ type: 'text', text: '音量已调到 13。' }] },
      }))
      stream.push(commandEvent(sequence++, 'turn/end', { turn: current, reason: { kind: 'completed' } }))
      return { accepted: true }
    },
  }

  const bridge = await startSessionBridge({
    sessionController,
    config: {
      discoveryDirectory: directory,
      deviceAgentPreset: 'halo-device',
    },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }
  const body = JSON.stringify({
    command: '把音量调到 13',
    requestId: 'request-1',
    timeoutSeconds: 30,
  })

  const response = await fetch(`${bridge.descriptor.baseUrl}v1/device-command`, {
    method: 'POST', headers, body,
  })
  assert.equal(response.status, 200)
  const result = await response.json()
  assert.equal(result.success, true)
  assert.equal(result.sessionId, 'session-device')
  assert.equal(result.finalText, '音量已调到 13。')
  assert.deepEqual(result.calledTools, ['set_pixelbar_volume'])
  assert.deepEqual(result.successfulTools, ['set_pixelbar_volume'])
  assert.equal(result.calledToolCount, 1)
  assert.equal(result.successfulToolCount, 1)
  assert.equal(result.toolOutcomes[0].name, 'set_pixelbar_volume')

  const duplicate = await fetch(`${bridge.descriptor.baseUrl}v1/device-command`, {
    method: 'POST', headers, body,
  })
  assert.equal(duplicate.status, 200)
  assert.deepEqual(await duplicate.json(), result)

  const failedResponse = await fetch(`${bridge.descriptor.baseUrl}v1/device-command`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ command: '模拟设备拒绝', requestId: 'request-2', timeoutSeconds: 30 }),
  })
  assert.equal(failedResponse.status, 200)
  const failed = await failedResponse.json()
  assert.equal(failed.success, false)
  assert.deepEqual(failed.calledTools, ['set_pixelbar_volume'])
  assert.deepEqual(failed.successfulTools, [])
  assert.equal(failed.successfulToolCount, 0)
  assert.equal(failed.toolOutcomes[0].status, 'error')
  assert.equal(failed.toolOutcomes[0].errorCode, 'device/rejected')

  assert.equal(calls.filter(([name]) => name === 'create').length, 1)
  assert.equal(calls.filter(([name]) => name === 'rename').length, 1)
  assert.equal(calls.filter(([name]) => name === 'prompt').length, 2)
  assert.equal(calls.find(([name]) => name === 'create')[1].agentPreset, 'halo-device')

  const conflict = await fetch(`${bridge.descriptor.baseUrl}v1/device-command`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ command: '关闭灯光', requestId: 'request-1', timeoutSeconds: 30 }),
  })
  assert.equal(conflict.status, 409)

  const status = await fetch(`${bridge.descriptor.baseUrl}v1/status`, {
    headers: { Authorization: headers.Authorization },
  })
  const statusBody = await status.json()
  assert.equal(statusBody.capabilities.deviceCommand, true)
  assert.equal(statusBody.capabilities.deviceHistoryGrouping, true)
  assert.equal(statusBody.deviceAgentPreset, 'halo-device')
  assert.equal(statusBody.deviceSessionId, 'session-device')
})

test('resumes an existing canonical device session with its original working directory', async () => {
  const stream = eventStream()
  const calls = []
  let sequence = 0
  const sessionController = {
    async list() {
      return {
        items: [{
          sessionId: 'saved-device',
          cwd: 'D:/old-debug',
          projections: { values: { agentPreset: 'halo-device' } },
        }],
      }
    },
    async create(request) {
      calls.push(['create', request])
      if (request.cwd !== 'D:/old-debug') {
        const error = new Error('Session belongs to a different workspace')
        error.code = 'session/conflict'
        throw error
      }
      return { sessionId: request.sessionId }
    },
    follow(_request, signal) { return stream.follow(signal) },
    async prompt(request) {
      calls.push(['prompt', request])
      stream.push(commandEvent(sequence++, 'turn/start', { turn: 1 }))
      stream.push(commandEvent(sequence++, 'user/message', {
        role: 'user', content: request.content, source: { kind: 'user', rpcId: request.requestId },
      }))
      stream.push(commandEvent(sequence++, 'assistant/message', {
        turn: 1,
        step: 1,
        message: { role: 'assistant', content: [{ type: 'text', text: '设备状态正常。' }] },
      }))
      stream.push(commandEvent(sequence++, 'turn/end', { turn: 1, reason: { kind: 'completed' } }))
      return { accepted: true }
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'saved-device',
    deviceAgentPreset: 'halo-device',
    deviceWorkingDirectory: 'D:/installed',
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
    requestTimeoutMs: 1_000,
  }, { warn() {} })

  const result = await coordinator.execute({
    command: '查询设备状态',
    sessionId: 'saved-device',
    requestId: 'resume-original-cwd',
    timeoutSeconds: 1,
  })

  assert.equal(result.success, true)
  assert.deepEqual(calls[0], ['create', {
    sessionId: 'saved-device',
    cwd: 'D:/old-debug',
    agentPreset: 'halo-device',
  }])
  assert.equal(calls.filter(([name]) => name === 'prompt').length, 1)
})

test('refuses to adopt an existing ordinary session as the device session', async () => {
  let creates = 0
  let prompts = 0
  const sessionController = {
    async list() {
      return {
        items: [{
          sessionId: 'ordinary',
          cwd: 'D:/work',
          projections: { values: { agentPreset: 'standard' } },
        }],
      }
    },
    async create() { creates += 1; return { sessionId: 'ordinary' } },
    async prompt() { prompts += 1; return { accepted: true } },
    async *follow() { yield { type: 'snapshot', cursor: -1, records: [] } },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'ordinary',
    deviceAgentPreset: 'halo-device',
    deviceWorkingDirectory: 'D:/installed',
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
    requestTimeoutMs: 1_000,
  }, { warn() {} })

  await assert.rejects(coordinator.execute({
    command: '换一个自定义场景',
    sessionId: 'ordinary',
    requestId: 'do-not-adopt',
    timeoutSeconds: 1,
  }), error => error?.status === 409 && error?.code === 'device_session_unavailable')
  assert.equal(creates, 0)
  assert.equal(prompts, 0)
})

test('sends normal Session prompts and manages sessions without a device preset', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-management-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const stream = eventStream()
  const calls = []
  const archived = new Set()
  let sequence = 0
  const summaries = [{
    sessionId: 'ordinary-existing',
    cwd: 'D:/ordinary',
    updatedAt: 1,
    projections: { values: { title: 'Existing', agentPreset: 'standard' } },
  }]
  const sessionController = {
    async list() {
      // DSH 0.2 keeps archived rows in SessionController.list; the Workspace
      // baseline is the authoritative archive marker.
      return { items: summaries }
    },
    async inspect() { return { events: [] } },
    async page() { return { records: [], hasMore: false } },
    async projections({ sessionId }) {
      return summaries.find(summary => summary.sessionId === sessionId)?.projections
    },
    async create(request) {
      calls.push(['create', request])
      const summary = {
        sessionId: 'ordinary-new',
        cwd: 'D:/ordinary',
        updatedAt: 2,
        projections: { values: { agentPreset: 'standard' } },
      }
      summaries.push(summary)
      return { sessionId: summary.sessionId, agentPreset: 'standard' }
    },
    async rename(request) {
      calls.push(['rename', request])
      const summary = summaries.find(item => item.sessionId === request.sessionId)
      if (summary !== undefined) summary.projections.values.title = request.title
      return { title: request.title, seq: sequence++ }
    },
    follow(request, signal) {
      calls.push(['follow', request])
      return stream.follow(signal)
    },
    async prompt(request) {
      calls.push(['prompt', request])
      stream.push(commandEvent(sequence++, 'turn/start', { turn: 1 }))
      stream.push(commandEvent(sequence++, 'user/message', {
        role: 'user', content: request.content, source: { kind: 'user', rpcId: request.requestId },
      }))
      stream.push(commandEvent(sequence++, 'assistant/message', {
        turn: 1,
        step: 1,
        message: { role: 'assistant', content: [{ type: 'text', text: '普通会话回复。' }] },
      }))
      stream.push(commandEvent(sequence++, 'turn/end', { turn: 1, reason: { kind: 'completed' } }))
      return { accepted: true }
    },
  }
  const workspaceController = {
    async archiveSession({ sessionId }) {
      calls.push(['archive', sessionId])
      archived.add(sessionId)
      return { archivedSessionIds: [...archived] }
    },
    async unarchiveSession({ sessionId }) {
      calls.push(['restore', sessionId])
      archived.delete(sessionId)
      return { archivedSessionIds: [...archived] }
    },
    async *follow() {
      yield { type: 'baseline', value: { archivedSessionIds: [...archived] } }
    },
  }
  const bridge = await startSessionBridge({
    sessionController,
    workspaceController,
    config: {
      discoveryDirectory: directory,
      maxLegacySessionProbesPerList: 0,
    },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }

  const status = await (await fetch(`${bridge.descriptor.baseUrl}v1/status`, { headers })).json()
  assert.equal(status.capabilities.sessionPrompt, true)
  assert.equal(status.capabilities.sessionCreate, true)
  assert.equal(status.capabilities.sessionRename, true)
  assert.equal(status.capabilities.sessionArchive, true)
  assert.equal(status.capabilities.sessionRestore, true)

  const commandResponse = await fetch(`${bridge.descriptor.baseUrl}v1/session-command`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      sessionId: 'ordinary-existing',
      command: '继续分析这个问题',
      requestId: 'ordinary-request-1',
      timeoutSeconds: 30,
    }),
  })
  assert.equal(commandResponse.status, 200)
  assert.equal((await commandResponse.json()).finalText, '普通会话回复。')
  assert.equal(calls.filter(([name]) => name === 'create').length, 0)
  assert.deepEqual(calls.find(([name]) => name === 'prompt')[1].content, [
    { type: 'text', text: '继续分析这个问题' },
  ])

  const createResponse = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/create`, {
    method: 'POST', headers, body: JSON.stringify({ title: '新的普通会话' }),
  })
  assert.equal(createResponse.status, 201)
  const created = await createResponse.json()
  assert.equal(created.session.id, 'ordinary-new')
  assert.equal(created.session.title, '新的普通会话')
  assert.equal(created.session.isArchived, false)
  assert.deepEqual(calls.find(([name]) => name === 'create')[1], {})

  const renameResponse = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/rename`, {
    method: 'POST', headers, body: JSON.stringify({ sessionId: 'ordinary-new', title: '已重命名' }),
  })
  assert.equal(renameResponse.status, 200)
  assert.deepEqual(await renameResponse.json(), { sessionId: 'ordinary-new', title: '已重命名' })

  const archiveResponse = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/archive`, {
    method: 'POST', headers, body: JSON.stringify({ sessionId: 'ordinary-new', archived: true }),
  })
  assert.equal(archiveResponse.status, 200)
  assert.deepEqual(await archiveResponse.json(), { sessionId: 'ordinary-new', archived: true })
  const afterArchive = await (await fetch(`${bridge.descriptor.baseUrl}v1/sessions`, { headers })).json()
  const archivedRows = afterArchive.sessions.filter(session => session.id === 'ordinary-new')
  assert.equal(archivedRows.length, 1)
  assert.equal(archivedRows[0].isArchived, true)
  assert.equal(archivedRows[0].title, '已重命名')
  assert.equal(archivedRows[0].workingDirectory, 'D:/ordinary')
  assert.equal(archivedRows[0].updatedAt, '1970-01-01T00:00:00.002Z')

  const promptsBeforeArchivedSend = calls.filter(([name]) => name === 'prompt').length
  const archivedCommand = await fetch(`${bridge.descriptor.baseUrl}v1/session-command`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      sessionId: 'ordinary-new',
      command: '这条消息不能提交',
      requestId: 'archived-request',
      timeoutSeconds: 30,
    }),
  })
  assert.equal(archivedCommand.status, 409)
  assert.equal((await archivedCommand.json()).error.code, 'session_archived')
  assert.equal(calls.filter(([name]) => name === 'prompt').length, promptsBeforeArchivedSend)

  const restoreResponse = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/archive`, {
    method: 'POST', headers, body: JSON.stringify({ sessionId: 'ordinary-new', archived: false }),
  })
  assert.equal(restoreResponse.status, 200)
  assert.deepEqual(await restoreResponse.json(), { sessionId: 'ordinary-new', archived: false })
  const afterRestore = await (await fetch(`${bridge.descriptor.baseUrl}v1/sessions`, { headers })).json()
  assert.equal(afterRestore.sessions.find(session => session.id === 'ordinary-new').isArchived, false)
})

test('disables archive and restore when the DSH runtime cannot restore archived sessions', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-archive-legacy-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const bridge = await startSessionBridge({
    sessionController: {
      async list() { return { items: [] } },
      async inspect() { return { events: [] } },
      async page() { return { records: [], hasMore: false } },
    },
    workspaceController: {
      async archiveSession() {},
      async *follow() { yield { type: 'baseline', value: { archivedSessionIds: [] } } },
    },
    config: { discoveryDirectory: directory },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }

  const status = await (await fetch(`${bridge.descriptor.baseUrl}v1/status`, { headers })).json()
  assert.equal(status.capabilities.sessionArchive, false)
  assert.equal(status.capabilities.sessionRestore, false)
  const response = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/archive`, {
    method: 'POST', headers, body: JSON.stringify({ sessionId: 'old', archived: true }),
  })
  assert.equal(response.status, 501)
  assert.equal((await response.json()).error.code, 'session_archive_not_supported')
})

test('protects pinned and canonical device sessions from ordinary Session operations', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-protected-device-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  let prompts = 0
  let renames = 0
  let archives = 0
  const sessionController = {
    async list() {
      return {
        items: [{
          sessionId: 'canonical-device',
          cwd: 'D:/device',
          projections: { values: { agentPreset: 'halo-device' } },
        }],
      }
    },
    async inspect() { return { events: [] } },
    async page() { return { records: [], hasMore: false } },
    async create(request) { return { sessionId: request.sessionId ?? 'new' } },
    async rename() { renames += 1; return { title: 'unexpected' } },
    async prompt() { prompts += 1; return { accepted: true } },
    async *follow() { yield { type: 'snapshot', cursor: -1, records: [] } },
  }
  const workspaceController = {
    async archiveSession() { archives += 1 },
    async unarchiveSession() {},
    async *follow() { yield { type: 'baseline', value: { archivedSessionIds: [] } } },
  }
  const bridge = await startSessionBridge({
    sessionController,
    workspaceController,
    config: {
      discoveryDirectory: directory,
      deviceSessionId: 'pinned-device',
      deviceAgentPreset: 'halo-device',
    },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }

  const command = await fetch(`${bridge.descriptor.baseUrl}v1/session-command`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      sessionId: 'canonical-device', command: '继续', requestId: 'protected-command', timeoutSeconds: 30,
    }),
  })
  assert.equal(command.status, 409)
  assert.equal((await command.json()).error.code, 'session_is_device_control')

  const rename = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/rename`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ sessionId: 'canonical-device', title: '不要改名' }),
  })
  assert.equal(rename.status, 409)
  assert.equal((await rename.json()).error.code, 'session_is_device_control')

  const archive = await fetch(`${bridge.descriptor.baseUrl}v1/sessions/archive`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ sessionId: 'pinned-device', archived: true }),
  })
  assert.equal(archive.status, 409)
  assert.equal((await archive.json()).error.code, 'session_is_device_control')
  assert.equal(prompts, 0)
  assert.equal(renames, 0)
  assert.equal(archives, 0)
})

test('rejects oversized or malformed device command input before prompting', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-command-input-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  let prompts = 0
  const sessionController = {
    async list() { return { items: [] } },
    async inspect() { return { events: [] } },
    async page() { return { records: [], hasMore: false } },
    async create() { return { sessionId: 'device' } },
    async prompt() { prompts += 1; return { accepted: true } },
    async *follow() { yield { type: 'snapshot', cursor: -1, records: [] } },
  }
  const bridge = await startSessionBridge({
    sessionController,
    config: { discoveryDirectory: directory },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }
  const response = await fetch(`${bridge.descriptor.baseUrl}v1/device-command`, {
    method: 'POST', headers, body: JSON.stringify({ command: 'ok', timeoutSeconds: 1 }),
  })
  assert.equal(response.status, 400)
  assert.equal(prompts, 0)
})

test('a completion timeout never resubmits or cancels an accepted prompt', async () => {
  let prompts = 0
  let cancels = 0
  const sessionController = {
    async list() { return { items: [] } },
    async create(request) { return { sessionId: request.sessionId ?? 'device' } },
    async prompt() { prompts += 1; return { accepted: true } },
    cancel() { cancels += 1 },
    async *follow(_request, signal) {
      yield { type: 'snapshot', cursor: -1, records: [], hasMore: false, projections: { asOfSeq: -1, values: {} } }
      await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }))
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'device',
    deviceAgentPreset: undefined,
    deviceWorkingDirectory: undefined,
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
  }, { warn() {} })
  const result = await coordinator.execute({
    command: '开灯',
    sessionId: 'device',
    requestId: 'timeout-request',
    timeoutSeconds: 0.02,
  })
  assert.equal(result.status, 'accepted')
  assert.equal(result.success, false)
  assert.deepEqual(result.calledTools, [])
  assert.deepEqual(result.successfulTools, [])
  assert.equal(prompts, 1)
  assert.equal(cancels, 0)
})

test('a prompt admission timeout remains unknown until DSH acknowledges it', async () => {
  let prompts = 0
  const sessionController = {
    async list() { return { items: [] } },
    async create(request) { return { sessionId: request.sessionId ?? 'device' } },
    async prompt(_request, signal) {
      prompts += 1
      await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }))
      throw signal.reason
    },
    async *follow() {
      yield { type: 'snapshot', cursor: -1, records: [], hasMore: false, projections: { asOfSeq: -1, values: {} } }
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'device',
    deviceAgentPreset: undefined,
    deviceWorkingDirectory: undefined,
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
  }, { warn() {} })

  const result = await coordinator.execute({
    command: '开灯',
    sessionId: 'device',
    requestId: 'admission-timeout-request',
    timeoutSeconds: 0.02,
  })
  assert.equal(result.status, 'unknown')
  assert.equal(result.success, false)
  assert.equal(prompts, 1)
})

test('an observation failure after prompt acknowledgement remains accepted', async () => {
  let prompts = 0
  const observationFailure = Object.assign(new Error('follow stream failed'), { code: 'session/not-found' })
  const sessionController = {
    async list() { return { items: [] } },
    async create(request) { return { sessionId: request.sessionId ?? 'device' } },
    async prompt() { prompts += 1; return { accepted: true } },
    async *follow() {
      yield { type: 'snapshot', cursor: -1, records: [], hasMore: false, projections: { asOfSeq: -1, values: {} } }
      throw observationFailure
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'device',
    deviceAgentPreset: undefined,
    deviceWorkingDirectory: undefined,
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
  }, { warn() {} })

  const result = await coordinator.execute({
    command: '开灯',
    sessionId: 'device',
    requestId: 'accepted-follow-failure',
    timeoutSeconds: 1,
  })
  assert.equal(result.status, 'accepted')
  assert.equal(result.success, false)
  assert.equal(prompts, 1)
})

test('a completed clarification turn without a tool remains a successful DSH reply', async () => {
  const stream = eventStream()
  let sequence = 0
  const sessionController = {
    async list() { return { items: [] } },
    async create() { return { sessionId: 'device' } },
    follow(_request, signal) { return stream.follow(signal) },
    async prompt(request) {
      stream.push(commandEvent(sequence++, 'turn/start', { turn: 1 }))
      stream.push(commandEvent(sequence++, 'user/message', {
        role: 'user', content: request.content, source: { kind: 'user', rpcId: request.requestId },
      }))
      stream.push(commandEvent(sequence++, 'assistant/message', {
        turn: 1,
        step: 1,
        message: { role: 'assistant', content: [{ type: 'text', text: '没有执行设备操作。' }] },
      }))
      stream.push(commandEvent(sequence++, 'turn/end', { turn: 1, reason: { kind: 'completed' } }))
      return { accepted: true }
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'device',
    deviceAgentPreset: undefined,
    deviceWorkingDirectory: undefined,
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
  }, { warn() {} })
  const result = await coordinator.execute({
    command: '执行设备操作',
    sessionId: 'device',
    requestId: 'no-tool-request',
    timeoutSeconds: 1,
  })
  assert.equal(result.status, 'completed')
  assert.equal(result.success, true)
  assert.deepEqual(result.calledTools, [])
  assert.deepEqual(result.successfulTools, [])
})

test('a tool-only completed turn returns a clear fallback message', async () => {
  const stream = eventStream()
  let sequence = 0
  const sessionController = {
    async list() { return { items: [] } },
    async create() { return { sessionId: 'device' } },
    follow(_request, signal) { return stream.follow(signal) },
    async prompt(request) {
      stream.push(commandEvent(sequence++, 'turn/start', { turn: 1 }))
      stream.push(commandEvent(sequence++, 'user/message', {
        role: 'user', content: request.content, source: { kind: 'user', rpcId: request.requestId },
      }))
      stream.push(commandEvent(sequence++, 'tool/call', {
        turn: 1, step: 1, callId: 'call-1', name: 'get_pixelbar_status', arguments: '{}',
      }))
      stream.push(commandEvent(sequence++, 'tool/result', {
        turn: 1,
        step: 1,
        message: {
          role: 'tool', toolCallId: 'call-1', content: [{ type: 'text', text: '{"success":true}' }],
        },
      }))
      stream.push(commandEvent(sequence++, 'turn/end', { turn: 1, reason: { kind: 'completed' } }))
      return { accepted: true }
    },
  }
  const coordinator = createDeviceCommandCoordinator(sessionController, {
    deviceSessionId: 'device',
    deviceAgentPreset: undefined,
    deviceWorkingDirectory: undefined,
    deviceSessionTitle: undefined,
    maxQueuedDeviceCommands: 4,
  }, { warn() {} })
  const result = await coordinator.execute({
    command: '查询设备状态',
    sessionId: 'device',
    requestId: 'tool-only-request',
    timeoutSeconds: 1,
  })
  assert.equal(result.success, true)
  assert.equal(result.finalText, null)
  assert.equal(result.message, '设备工具已执行，但 DSH 未返回文字回复。')
  assert.deepEqual(result.successfulTools, ['get_pixelbar_status'])
})

test('task bridge creates an exclusive workspace, admits once, reports progress, cancels, and releases without cancelling', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-task-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const discoveryDirectory = join(directory, 'discovery')
  const baseDirectory = join(directory, 'tasks')
  const summaries = new Map()
  const records = new Map()
  const historyRecords = new Map()
  const createRequests = []
  const promptRequests = []
  let cancelCalls = 0
  let nextId = 1
  const sessionController = {
    async list() { return { items: [...summaries.values()] } },
    async inspect(sessionId) {
      return {
        meta: { cwd: summaries.get(sessionId)?.cwd, agentPreset: 'standard' },
        events: sessionId === 'legacy-device'
          ? [rawEvent(0, 'tool/call', { name: 'get_pixelbar_status', arguments: '{}' })]
          : [],
      }
    },
    async page(request) {
      const full = historyRecords.get(request.address.sessionId) ?? []
      const throughSeq = Number.isSafeInteger(request.throughSeq)
        ? request.throughSeq
        : Number.MAX_SAFE_INTEGER
      const beforeSeq = Number.isSafeInteger(request.beforeSeq)
        ? request.beforeSeq
        : Number.MAX_SAFE_INTEGER
      const eligible = full.filter(record => record.event.seq <= throughSeq && record.event.seq < beforeSeq)
      const maxMessages = Number.isSafeInteger(request.maxMessages) ? request.maxMessages : eligible.length
      const selected = eligible.slice(-maxMessages)
      return { records: selected, hasMore: eligible.length > selected.length }
    },
    async create(request) {
      createRequests.push(request)
      if (request.cwd?.endsWith('unknown-create')) throw new Error('simulated unknown create outcome')
      const sessionId = request.sessionId ?? `task-${nextId++}`
      if (!summaries.has(sessionId)) {
        summaries.set(sessionId, {
          sessionId,
          cwd: request.cwd,
          running: false,
          agentAvailable: true,
          blank: true,
          projections: { values: { title: '', agentPreset: request.agentPreset } },
        })
        const initialRecords = []
        records.set(sessionId, initialRecords)
        historyRecords.set(sessionId, initialRecords)
      }
      return { sessionId, agentPreset: request.agentPreset }
    },
    async rename({ sessionId, title }) {
      summaries.get(sessionId).projections.values.title = title
      return { title }
    },
    async prompt(request) {
      promptRequests.push(request)
      if (request.requestId === 'unknown-admission') throw new Error('simulated admission failure')
      const summary = summaries.get(request.sessionId)
      summary.running = true
      summary.blank = false
      const admittedRecords = [
        eventRecord(0, 'turn/start', { turn: 1 }),
        eventRecord(1, 'user/message', {
          turn: 1,
          source: { kind: 'user', rpcId: request.requestId },
          content: request.content,
        }),
        eventRecord(2, 'tool/call', {
          turn: 1, step: 1, callId: 'call-1', name: 'web_search', arguments: '{"query":"DSH"}',
        }),
      ]
      const current = records.get(request.sessionId)
      const history = historyRecords.get(request.sessionId)
      current.push(...admittedRecords)
      if (history !== current) history.push(...admittedRecords)
      return { accepted: true }
    },
    async *follow({ address }) {
      const current = records.get(address.sessionId) ?? []
      const history = historyRecords.get(address.sessionId) ?? current
      yield {
        type: 'snapshot',
        cursor: history.at(-1)?.event?.seq ?? -1,
        records: current,
        hasMore: history.length > current.length,
        projections: { values: { userQuestions: { active: [] } } },
      }
    },
    async projections() { return { values: { userQuestions: { active: [] } } } },
    async cancel({ sessionId }) {
      cancelCalls += 1
      summaries.get(sessionId).running = false
      return { accepted: true }
    },
  }
  const taskInteractions = createTaskInteractionBroker()
  const bridge = await startSessionBridge({
    sessionController,
    taskInteractions,
    config: { discoveryDirectory, requestTimeoutMs: 5_000 },
    logger: { info() {}, warn() {} },
  })
  context.after(() => bridge.close())
  const headers = {
    Authorization: `Bearer ${bridge.descriptor.token}`,
    'Content-Type': 'application/json',
  }
  const post = (path, body) => fetch(`${bridge.descriptor.baseUrl}${path}`, {
    method: 'POST', headers, body: JSON.stringify(body),
  })

  const createdResponse = await post('v1/tasks/create', {
    baseDirectory, directoryName: 'voice-task', title: '语音任务',
  })
  assert.equal(createdResponse.status, 201)
  const created = await createdResponse.json()
  assert.equal(created.session.id, 'task-1')
  assert.deepEqual(createRequests[0], { cwd: join(baseDirectory, 'voice-task'), agentPreset: 'standard' })
  assert.equal((await stat(join(baseDirectory, 'voice-task'))).isDirectory(), true)
  const adoptedResponse = await post('v1/tasks/adopt', { sessionId: 'task-1' })
  assert.equal(adoptedResponse.status, 200)
  const adopted = await adoptedResponse.json()
  assert.equal(adopted.sessionId, 'task-1')
  assert.equal(adopted.session.id, 'task-1')

  summaries.set('canonical-device', {
    sessionId: 'canonical-device', blank: false,
    projections: { values: { agentPreset: 'halo-device' } },
  })
  summaries.set('legacy-device', {
    sessionId: 'legacy-device', blank: false,
    projections: { values: { agentPreset: 'standard' } },
  })
  for (const sessionId of ['canonical-device', 'legacy-device']) {
    const rejected = await post('v1/tasks/adopt', { sessionId })
    assert.equal(rejected.status, 409)
    assert.equal((await rejected.json()).error.code, 'task_session_is_device_control')
  }

  const duplicateDirectory = await post('v1/tasks/create', {
    baseDirectory, directoryName: 'voice-task', title: '不应覆盖',
  })
  assert.equal(duplicateDirectory.status, 409)
  assert.equal((await duplicateDirectory.json()).error.code, 'task_directory_exists')
  assert.equal(createRequests.length, 1)
  const unknownCreate = await post('v1/tasks/create', {
    baseDirectory, directoryName: 'unknown-create', title: '创建结果不明',
  })
  assert.equal(unknownCreate.status, 502)
  assert.equal((await stat(join(baseDirectory, 'unknown-create'))).isDirectory(), true)

  const unknownAdmission = await post('v1/tasks/prompt', {
    sessionId: 'task-1', requestId: 'unknown-admission', prompt: '运输结果不明',
  })
  assert.equal(unknownAdmission.status, 502)
  const unknownRetry = await post('v1/tasks/prompt', {
    sessionId: 'task-1', requestId: 'unknown-admission', prompt: '运输结果不明',
  })
  assert.equal(unknownRetry.status, 409)
  assert.equal((await unknownRetry.json()).error.code, 'task_prompt_outcome_unknown')
  assert.equal(promptRequests.filter(request => request.requestId === 'unknown-admission').length, 1)

  const admitted = await post('v1/tasks/prompt', {
    sessionId: 'task-1', requestId: 'request-1', prompt: '请执行长任务',
  })
  assert.equal(admitted.status, 202)
  assert.equal((await admitted.json()).accepted, true)
  const duplicateAdmission = await post('v1/tasks/prompt', {
    sessionId: 'task-1', requestId: 'request-1', prompt: '请执行长任务',
  })
  assert.equal(duplicateAdmission.status, 202)
  assert.equal(promptRequests.filter(request => request.requestId === 'request-1').length, 1)
  const conflictingAdmission = await post('v1/tasks/prompt', {
    sessionId: 'task-1', requestId: 'request-1', prompt: '不同内容',
  })
  assert.equal(conflictingAdmission.status, 409)
  assert.equal((await conflictingAdmission.json()).error.code, 'request_id_conflict')

  // Simulate a very long current turn whose bounded follow tail no longer
  // contains its turn/start or durable user rpcId.
  records.set('task-1', records.get('task-1').slice(2))
  const runningStatus = await fetch(
    `${bridge.descriptor.baseUrl}v1/tasks/status?sessionId=task-1`,
    { headers },
  )
  assert.equal(runningStatus.status, 200)
  const running = await runningStatus.json()
  assert.equal(running.taskStatus, 'running')
  assert.equal(running.progressText, '正在执行：web_search')
  assert.equal(running.finalText, null)

  const approvalPromise = taskInteractions.handleApproval({
    agent: { id: 'task-1' },
    toolName: 'web_search',
    callId: 'call-1',
    reason: 'generic model justification',
    displayReason: { en: 'Allow network access', zh: '允许访问网络' },
  }, () => 'next')
  const approvalStatusResponse = await fetch(
    `${bridge.descriptor.baseUrl}v1/tasks/status?sessionId=task-1`,
    { headers },
  )
  const approvalStatus = await approvalStatusResponse.json()
  assert.equal(approvalStatus.taskStatus, 'waitingApproval')
  assert.equal(approvalStatus.pendingInteractions[0].reason, '允许访问网络\n工具参数（原始）：{"query":"DSH"}')
  await taskInteractions.respond({
    sessionId: 'task-1',
    interactionId: approvalStatus.pendingInteractions[0].id,
    type: 'approval',
    outcome: 'rejected',
  }, [], async () => {})
  assert.equal(await approvalPromise, 'rejected')

  const completionRecords = [
    eventRecord(3, 'tool/result', {
      turn: 1,
      message: { role: 'tool', toolCallId: 'call-1', content: [{ type: 'text', text: 'ok' }] },
    }),
    eventRecord(4, 'assistant/message', {
      turn: 1, message: { role: 'assistant', content: [{ type: 'text', text: '任务完成' }] },
    }),
    eventRecord(5, 'turn/end', { turn: 1, reason: { kind: 'completed' } }),
  ]
  records.get('task-1').push(...completionRecords)
  historyRecords.get('task-1').push(...completionRecords)
  summaries.get('task-1').running = false
  const completedResponse = await fetch(
    `${bridge.descriptor.baseUrl}v1/tasks/status?sessionId=task-1`,
    { headers },
  )
  const completed = await completedResponse.json()
  assert.equal(completed.taskStatus, 'completed')
  assert.equal(completed.finalText, '任务完成')
  assert.equal(completed.progressText, '任务已完成')
  assert.equal(completed.revision > running.revision, true)

  const unanchoredCreated = await post('v1/tasks/create', {
    baseDirectory, directoryName: 'unanchored-task', title: '排队任务',
  })
  assert.equal(unanchoredCreated.status, 201)
  const unanchoredSessionId = (await unanchoredCreated.json()).session.id
  const unanchoredAdmission = await post('v1/tasks/prompt', {
    sessionId: unanchoredSessionId, requestId: 'request-unanchored', prompt: '排队执行',
  })
  assert.equal(unanchoredAdmission.status, 202)
  const unrelatedTurn = [
    eventRecord(10, 'turn/start', { turn: 9 }),
    eventRecord(11, 'user/message', {
      turn: 9, source: { kind: 'user', rpcId: 'different-request' },
      content: [{ type: 'text', text: '其他任务' }],
    }),
    eventRecord(12, 'assistant/message', {
      turn: 9, message: { role: 'assistant', content: [{ type: 'text', text: '其他任务完成' }] },
    }),
    eventRecord(13, 'turn/end', { turn: 9, reason: { kind: 'completed' } }),
  ]
  records.set(unanchoredSessionId, unrelatedTurn)
  historyRecords.set(unanchoredSessionId, unrelatedTurn)
  summaries.get(unanchoredSessionId).running = false
  const unanchoredStatusResponse = await fetch(
    `${bridge.descriptor.baseUrl}v1/tasks/status?sessionId=${unanchoredSessionId}`,
    { headers },
  )
  const unanchoredStatus = await unanchoredStatusResponse.json()
  assert.equal(unanchoredStatus.taskStatus, 'running')
  assert.equal(unanchoredStatus.finalText, null)
  assert.equal(unanchoredStatus.turn, null)

  summaries.get('task-1').running = true
  const cancelled = await post('v1/tasks/cancel', { sessionId: 'task-1' })
  assert.equal(cancelled.status, 202)
  assert.equal(cancelCalls, 1)
  const released = await post('v1/tasks/release', { sessionId: 'task-1' })
  assert.equal(released.status, 200)
  assert.deepEqual(await released.json(), { sessionId: 'task-1', released: true })
  assert.equal(cancelCalls, 1)
  const afterRelease = await fetch(
    `${bridge.descriptor.baseUrl}v1/tasks/status?sessionId=task-1`,
    { headers },
  )
  assert.equal(afterRelease.status, 409)
  assert.equal((await afterRelease.json()).error.code, 'task_not_adopted')
})

test('task interaction broker claims only adopted sessions and validates exact string answer maps', async () => {
  const answered = []
  const broker = createTaskInteractionBroker({
    agents: { get(sessionId) { return { id: sessionId } } },
    userQuestions: {
      async answer(agent, callId, answer) {
        answered.push({ agent, callId, answer })
        return true
      },
    },
    idFactory: () => 'generated',
    now: () => Date.UTC(2026, 9, 1),
  })
  let waterfallCalls = 0
  const next = () => {
    waterfallCalls += 1
    return 'next-answerer'
  }
  assert.equal(broker.handleApproval({ agent: { id: 'ordinary' } }, next), 'next-answerer')
  assert.equal(waterfallCalls, 1)

  broker.adopt('task-1')
  const approvalPromise = broker.handleApproval({
    agent: { id: 'task-1' }, toolName: 'write_file', callId: 'call-a', reason: '写入文件',
  }, next)
  const approval = broker.list('task-1')[0]
  assert.equal(approval.type, 'approval')
  assert.deepEqual(approval.outcomes, ['allowed-once', 'rejected'])
  const approvalReply = await broker.respond({
    sessionId: 'task-1', interactionId: approval.id, type: 'approval', outcome: 'allowed-once',
  }, [], async () => {})
  assert.equal(approvalReply.accepted, true)
  assert.equal(await approvalPromise, 'allowed-once')

  const questions = [
    { id: 'single', question: '选一项', options: [{ label: 'A' }, { label: 'B' }] },
    { id: 'multiple', question: '选多项', multiSelect: true, options: [{ label: 'X' }, { label: 'Y' }] },
    { id: 'custom', question: '输入内容' },
    { id: 'skip', question: '可跳过' },
  ]
  const questionPromise = broker.handleQuestion({
    agent: { id: 'task-1' }, wait: { callId: 'call-q' }, questions,
  }, next)
  const question = broker.list('task-1')[0]
  await assert.rejects(
    broker.respond({
      sessionId: 'task-1', interactionId: question.id, type: 'question', answers: { single: 'A' },
    }, [], async () => {}),
    error => error.code === 'invalid_question_answers',
  )
  await broker.respond({
    sessionId: 'task-1',
    interactionId: question.id,
    type: 'question',
    answers: { single: 'A', multiple: 'X | Y | X', custom: '自由文本', skip: '' },
  }, [], async () => {})
  assert.deepEqual(await questionPromise, {
    answers: [
      { id: 'single', selected: ['A'] },
      { id: 'multiple', selected: ['X', 'Y'] },
      { id: 'custom', selected: [], custom: '自由文本' },
      { id: 'skip', selected: [] },
    ],
  })

  const projected = [{
    callId: 'continued-1',
    state: 'continued',
    questions: [{ id: 'continued', question: '继续？', options: [{ label: '是' }] }],
  }]
  let attachCalls = 0
  await broker.respond({
    sessionId: 'task-1',
    interactionId: 'question:continued-1',
    type: 'question',
    answers: { continued: '是' },
  }, projected, async () => { attachCalls += 1 })
  assert.equal(attachCalls, 0)
  assert.deepEqual(answered[0], {
    agent: { id: 'task-1' },
    callId: 'continued-1',
    answer: { answers: [{ id: 'continued', selected: ['是'] }] },
  })
  assert.deepEqual(broker.list('task-1', projected), [])

  const pendingApproval = broker.handleApproval({ agent: { id: 'task-1' }, toolName: 'shell' }, next)
  const pendingQuestion = broker.handleQuestion({
    agent: { id: 'task-1' }, questions: [{ id: 'q', question: '等待回答' }],
  }, next)
  broker.release('task-1')
  assert.equal(await pendingApproval, 'cancelled')
  await assert.rejects(pendingQuestion, /monitoring was released/u)
  assert.equal(broker.handleApproval({ agent: { id: 'task-1' } }, next), 'next-answerer')
  broker.close()
})

test('continued question answers reserve one writer before awaiting runtime acknowledgement', async (context) => {
  let releaseAnswer
  let answerEntered
  const acknowledgement = new Promise(resolve => { releaseAnswer = resolve })
  const entered = new Promise(resolve => { answerEntered = resolve })
  let answerCalls = 0
  const broker = createTaskInteractionBroker({
    agents: { get: sessionId => ({ id: sessionId }) },
    userQuestions: {
      async answer() {
        answerCalls += 1
        answerEntered()
        await acknowledgement
        return true
      },
    },
  })
  context.after(() => broker.close())
  broker.adopt('task-1')
  const projected = [{
    callId: 'continued-race', state: 'continued',
    questions: [{ id: 'q', question: '输入答案' }],
  }]
  const input = {
    sessionId: 'task-1', interactionId: 'question:continued-race',
    type: 'question', answers: { q: '确认' },
  }
  const first = broker.respond(input, projected, async () => {})
  await entered
  const concurrent = broker.respond(input, projected, async () => {})
    .then(value => ({ value }), error => ({ error }))
  const callsWhileWaiting = answerCalls
  releaseAnswer()
  const [accepted, second] = await Promise.all([first, concurrent])
  assert.equal(callsWhileWaiting, 1, 'a second caller must not enter the answerer while acknowledgement is pending')
  assert.equal(accepted.accepted, true)
  assert.equal(second.error?.code, 'interaction_reply_queued')
  assert.equal(answerCalls, 1)
  await assert.rejects(broker.respond(input, projected, async () => {}),
    error => error.code === 'interaction_reply_queued')
})

test('continued reply reservation covers agent attachment and releases after a failed attachment', async (context) => {
  let releaseAttach
  let attachedAgent
  const attachment = new Promise(resolve => { releaseAttach = resolve })
  let attachCalls = 0
  let answerCalls = 0
  const broker = createTaskInteractionBroker({
    agents: { get: () => attachedAgent },
    userQuestions: { async answer() { answerCalls += 1; return true } },
  })
  context.after(() => broker.close())
  broker.adopt('task-1')
  const projected = [{
    callId: 'continued-attach', state: 'continued',
    questions: [{ id: 'q', question: '输入答案' }],
  }]
  const input = {
    sessionId: 'task-1', interactionId: 'question:continued-attach',
    type: 'question', answers: { q: '继续' },
  }
  const ensureAgent = async () => { attachCalls += 1; await attachment }
  const first = broker.respond(input, projected, ensureAgent)
    .then(value => ({ value }), error => ({ error }))
  await assert.rejects(broker.respond(input, projected, ensureAgent),
    error => error.code === 'interaction_reply_queued')
  assert.equal(attachCalls, 1)
  releaseAttach()
  assert.equal((await first).error?.code, 'task_session_unavailable')
  assert.equal(answerCalls, 0)
  assert.equal(broker.list('task-1', projected).length, 1)
  const retry = await broker.respond(input, projected, async () => { attachedAgent = { id: 'task-1' } })
  assert.equal(retry.accepted, true)
  assert.equal(answerCalls, 1)
})

test('a declined continued answer releases its reservation without claiming success', async (context) => {
  let answerCalls = 0
  const broker = createTaskInteractionBroker({
    agents: { get: sessionId => ({ id: sessionId }) },
    userQuestions: { async answer() { answerCalls += 1; return answerCalls > 1 } },
  })
  context.after(() => broker.close())
  broker.adopt('task-1')
  const projected = [{
    callId: 'continued-declined', state: 'continued',
    questions: [{ id: 'q', question: '输入答案' }],
  }]
  const input = {
    sessionId: 'task-1', interactionId: 'question:continued-declined',
    type: 'question', answers: { q: '继续' },
  }
  await assert.rejects(broker.respond(input, projected, async () => {}),
    error => error.code === 'interaction_not_pending')
  assert.equal(broker.list('task-1', projected).length, 1)
  assert.equal((await broker.respond(input, projected, async () => {})).accepted, true)
  assert.equal(answerCalls, 2)
  assert.deepEqual(broker.list('task-1', projected), [])
})

test('normal process exit removes the owned descriptor synchronously', async (context) => {
  const directory = await mkdtemp(join(tmpdir(), 'halo-session-bridge-exit-'))
  context.after(() => rm(directory, { recursive: true, force: true }))
  const moduleUrl = new URL('../../integrations/deepseek-harness/halo-session-bridge/bridge-core.js', import.meta.url).href
  const program = `
    import { startSessionBridge } from ${JSON.stringify(moduleUrl)};
    const controller = { list(){}, inspect(){}, page(){} };
    await startSessionBridge({
      sessionController: controller,
      config: { discoveryDirectory: process.env.BRIDGE_TEST_DIRECTORY },
      logger: { info(){}, warn(){} },
    });
    process.exit(0);
  `
  const child = spawnSync(process.execPath, ['--input-type=module', '--eval', program], {
    env: { ...process.env, BRIDGE_TEST_DIRECTORY: directory },
    encoding: 'utf8',
    timeout: 10_000,
  })
  assert.equal(child.status, 0, child.stderr)
  assert.deepEqual((await readdir(directory)).filter(name => name.endsWith('.json')), [])
})

function eventRecord(seq, type, data, surfaceOp = 'append') {
  return {
    type: 'event',
    event: {
      seq,
      type,
      time: Date.UTC(2026, 8, 30, 12),
      data,
      surfaceOp,
    },
  }
}

function rawEvent(seq, type, data) {
  return eventRecord(seq, type, data).event
}

function commandEvent(seq, type, data) {
  return {
    type: 'event',
    event: { seq, type, time: Date.now(), data, surfaceOp: 'append' },
  }
}

function eventStream() {
  const values = []
  const waiters = []
  return {
    push(value) {
      const waiter = waiters.shift()
      if (waiter === undefined) values.push(value)
      else waiter(value)
    },
    async *follow(signal) {
      yield { type: 'snapshot', cursor: -1, records: [], hasMore: false, projections: { asOfSeq: -1, values: {} } }
      while (!signal.aborted) {
        if (values.length > 0) {
          yield values.shift()
          continue
        }
        const value = await new Promise(resolve => {
          const onAbort = () => {
            signal.removeEventListener('abort', onAbort)
            resolve(undefined)
          }
          signal.addEventListener('abort', onAbort, { once: true })
          waiters.push(item => {
            signal.removeEventListener('abort', onAbort)
            resolve(item)
          })
        })
        if (value === undefined) return
        yield value
      }
    },
  }
}

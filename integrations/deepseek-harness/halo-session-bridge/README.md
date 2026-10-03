# Halo session bridge for DeepSeek Harness

This DSH Host plugin exposes a small HTTP surface for the local
HaloPixelToolBox process. Session listing and history stay read-only. Mutating
endpoints use DSH's public Controllers to send a prompt, create or rename a
Session, and, on runtimes with reversible archive support, archive or restore
one. A separate device-command endpoint creates or resumes one dedicated DSH
Session and sends serialized prompts through `ctx.sessionController`; the
bridge never reads session files, SQLite indexes, or profile secrets directly.

The server binds only to `127.0.0.1` on an ephemeral port. At startup it writes
one current-process descriptor named `<pid>.json` to `discoveryDirectory`.
Every endpoint requires the random token from that descriptor as
`Authorization: Bearer <token>`. The token is never accepted in a URL. The
descriptor is removed when the plugin stops; clients must also reject stale
descriptors whose PID is gone or whose authenticated status endpoint fails.
An optional 32-character hexadecimal `ownedStartupId` is copied into the
descriptor and status response so a launcher can identify the Host it started;
it does not grant authority or replace the bearer token.

`/v1/status` also exposes the bridge's nullable pinned `deviceSessionId` and a
`capabilities.deviceHistoryGrouping` flag. A client can use these fields to
recover the one dedicated device conversation after its local preference store
is reset, while refusing an older bridge that cannot classify its history.

## Wire protocol v1

- `GET /v1/status`
- `GET /v1/sessions`
- `GET /v1/history?sessionId=<id>&beforeSeq=<exclusive-cursor>&limit=50`
- `POST /v1/session-command`
- `POST /v1/sessions/create`
- `POST /v1/sessions/rename`
- `POST /v1/sessions/archive`
- `POST /v1/device-command`
- `POST /v1/tasks/create`
- `POST /v1/tasks/adopt`
- `POST /v1/tasks/prompt`
- `GET /v1/tasks/status?sessionId=<id>`
- `POST /v1/tasks/interactions/respond`
- `POST /v1/tasks/cancel`
- `POST /v1/tasks/release`

The command body is at most 16 KiB:

```json
{
  "command": "把音量调到 13",
  "sessionId": "session-...",
  "requestId": "0a6de29b-72d0-4c7d-90c1-98fe8f6b9e16",
  "timeoutSeconds": 120
}
```

`command` is required. `sessionId` is optional to the protocol, but a launcher
should mint and persist one dedicated device Session and send it on every call;
that prevents a lost first response from splitting later commands into a new
history. `requestId` is also optional, but callers should mint one UUID for
each logical command and reuse exactly that UUID for a transport retry. DSH
persists it on `user/message.source.rpcId` and accepts a retry without inserting
the prompt twice. `timeoutSeconds` is an integer from 30 through 300.

Completed responses include `success`, the actual `sessionId`, `requestId`,
`finalText`, `message`, `calledTools` and `successfulTools` string arrays,
their numeric `calledToolCount` and `successfulToolCount`, bounded
`toolOutcomes`, `turnEndReason`, and `truncated`. A completed turn returns HTTP
200. HTTP 202 with `status: "accepted"` means the prompt may still be running;
retry with the same `requestId` to observe it. The bridge never automatically
resends an admitted prompt and never calls `cancel` on timeout, because cancel
acts on the Session's current turn rather than on one request identity.
PixelBar tools render structured device results; a top-level
`{"success":false}` is reported as a failed tool even when DSH itself completed
the turn normally.

`POST /v1/session-command` accepts the same bounded command shape, but requires
`sessionId`. It sends the raw command to that existing Session and never calls
`create`, changes its Agent preset, or wraps the text in the device persona.
It shares the durable `requestId` correlation and no-blind-retry behavior of the
device endpoint. DSH 0.1.5 and 0.2 both resolve and resume a cold Session inside
`sessionController.prompt`, using the persisted header's working directory and
Agent preset, so the bridge does not perform a conflicting explicit adoption
first. A command addressed to the pinned or exact `halo-device` Session is
rejected with `session_is_device_control` and must use the dedicated device
endpoint.

The management request bodies and successful responses are:

```text
POST /v1/sessions/create  { "title": "New chat" }
  -> 201 { "session": <projected-summary> }
POST /v1/sessions/rename  { "sessionId": "...", "title": "New title" }
  -> 200 { "sessionId": "...", "title": "New title" }
POST /v1/sessions/archive { "sessionId": "...", "archived": true|false }
  -> 200 { "sessionId": "...", "archived": true|false }
```

Creating an ordinary Session calls `sessionController.create({})`, so it
inherits the selected DSH profile's default Agent preset. The requested title
is then written directly with `rename`; no LLM title generator is invoked.
Titles must be non-empty, already trimmed, and at most 4096 UTF-8 bytes.
`/v1/status.capabilities` advertises `sessionPrompt`, `sessionCreate`,
`sessionRename`, `sessionArchive`, and `sessionRestore`. Clients must use these
flags rather than infer support from a DSH version string.
Rename and archive operations also reject the bridge's pinned device Session
and any Session carrying the exact `halo-device` preset. That protection uses
only the stable ID or preset projection and never guesses from a title.

Commands are serialized in one bridge queue and the bridge pins itself to the
first device `sessionId`. A different later ID is rejected. `follow` is opened
before `prompt`, the durable user event is correlated by `rpcId`, and completion
is reported only after that prompt's `turn/end`. Tool calls and results are
collected from that same turn. An optional `deviceAgentPreset` config is passed
to `sessionController.create`; `/v1/status` echoes it so the launcher can reject
an older Host that lacks the intended short device persona.

When a persisted device Session already exists, the bridge adopts it only when
DSH metadata proves that its Agent preset is exactly `halo-device`. It passes
that Session's recorded original working directory back to `create`, which is
required when a toolbox upgrade moves the launcher from a Debug directory to
an installed directory. An existing ordinary, subagent, missing-metadata, or
otherwise unverifiable Session fails closed before any prompt is sent. If the
client-preminted ID is absent from the complete Session list, the bridge may
create that same first-use ID with the configured default working directory;
it never substitutes another ID or replays the command after a failed create.

`/v1/sessions` returns top-level visible Sessions and recoverably archived
Sessions discoverable through the Workspace Controller. Each summary has an
`isArchived` Boolean. `runtimeStatus` is one of
`running`, `idle`, `detached`, or `unknown`. DSH 0.1.5 exposes `running` but not
`agentAvailable`, so a non-running 0.1.5 session is reported as `unknown` rather
than guessed. Each summary also includes `isDeviceControl` and a nullable
`deviceControlKind`: `canonical` identifies the exact `halo-device` preset and
`legacy` identifies the former voice wrapper. The bridge marks an exact preset
immediately. It recognizes sessions created by
the former Headless voice path only when a user message begins with that
version's complete fixed voice-wrapper prefix; titles and loose PixelBar
keywords are never used as evidence. Four still-earlier voice probe sessions
used one identical 104-character whole-message prompt without a command
delimiter; the bridge recognizes only its audited byte length and complete
SHA-256 fingerprint, not the shared assistant-identity sentence. A former
manual DSH session can also be
marked `legacy` after one complete cold inspection proves that it invoked at
least one exact tool from the bundled Halo PixelBar plugin, invoked no other
tool, contains no attachment, and contains no durable coding/workflow event
such as a command, todo, goal, subagent, team task, or compaction. Ambiguous
sessions remain ordinary. Legacy detection is cached by
`sessionId`, `updatedAt`, and projection cursor and probes at most 20 previously
unknown sessions per listing. Complete inspections are further capped at twelve
per listing. A six-second soft wall-clock guard stops starting new cold probes;
it does not cancel an in-flight read. Completed classifications are cached and
deferred sessions remain pending for the next refresh, so a large archive is
classified incrementally without holding one list request for the whole scan.

Confirmed classifications also survive bridge restarts in an atomic local
`.cache` document beside the endpoint descriptors; its extension keeps it out
of endpoint discovery. The filename carries hashes of the normalized DSH
home/profile scope and classifier rule version, while the document repeats the
scope hash and rule identifier. Each entry
contains only a Session ID, a SHA-256 validation key derived from `updatedAt`,
projection cursor and agent preset, the numeric timestamps/cursor, and the
classification. Titles, working directories, message text, tool results and
Bearer tokens are never persisted. A changed Session or rule misses the cache.
If both `updatedAt` and the projection cursor are unavailable, that Session's
classification is returned for the current listing but is not cached.
Writes use a bounded inter-process lock, merge concurrent writers, and replace
the document atomically; missing permissions, stale formats, and corrupt files
fail soft and fall back to cold classification.

History entries contain `sequence`, source `sessionId`, `role`, `kind`, `text`,
nullable ISO-8601 `createdAt`, and `truncated`. `kind` is `conversation`,
`context`, or `tool`; system and developer messages plus DSH's known runtime
context snapshot are context. The response's nullable `beforeSeq` is the cursor
for the next older page. Entry text defaults to 16 KiB and a page to 256 KiB;
both limits are explicit through `truncated`.

The two GET data endpoints call only the Controller's cold-safe `list`,
`projections` when available, `inspect`, and `page` methods. Session
classification prefers projection cursors and reads only a small tail page;
full inspection is a bounded compatibility fallback. Only the newest history
page uses `inspect` to obtain DSH's exact tail cursor; older pages derive their
upper cut from the returned dense-log `beforeSeq` cursor and do not rescan the
full log. Workspace archive discovery reads only the opening `follow` baseline.
Only POST endpoints may activate a writer or change metadata through `create`,
`follow`, `prompt`, `rename`, `archiveSession`, and `unarchiveSession`.

DSH 0.2 exposes both `workspaceController.archiveSession` and
`unarchiveSession`, so the bridge enables reversible archive and restore there.
DSH 0.1.5 exposes archive without an official restore method; both capability
flags remain false and the archive route returns HTTP 501, avoiding an
irreversible UI action. The bridge does not pass `stopActivity` to archive, so
DSH itself rejects attempts to archive a currently active Session.

## Long-task control

The task routes operate only on an ordinary Session created by the bridge or
explicitly adopted by ID. They reject archived Sessions, subagent Sessions,
the pinned device Session, and any Session whose exact Agent preset is
`halo-device`. Creating a task takes a trusted absolute local `baseDirectory`,
one safe direct-child `directoryName`, and a title:

```text
POST /v1/tasks/create
  { "baseDirectory": "C:/Users/me/Documents/DSH tasks",
    "directoryName": "research-20261001", "title": "Research task" }
  -> 201 { "session": <projected-summary> }
POST /v1/tasks/adopt { "sessionId": "..." }
  -> 200 { "sessionId": "...", "session": <projected-summary> }
POST /v1/tasks/prompt
  { "sessionId": "...", "requestId": "...", "prompt": "..." }
  -> 202 { "accepted": true, "sessionId": "...", "requestId": "..." }
```

The base directory may be created recursively, but the task directory itself
is created with an exclusive single-directory operation. An existing path is
never opened, reused, cleared, or overwritten. New task Sessions use the
configured `taskAgentPreset` (default `standard`). A task prompt is admitted
with DSH `mode: queue`; the bridge records its request ID before calling DSH.
An acknowledged duplicate returns the prior acknowledgement without prompting
again. If transport or DSH fails before acknowledgement, a later duplicate is
reported as `task_prompt_outcome_unknown` and is not resent.
If Session creation throws after the Controller call begins, the newly reserved
directory is intentionally retained: a transport failure cannot prove that DSH
did not accept a Session whose persisted working directory points there.

`GET /v1/tasks/status` returns `revision`, `runtimeStatus`, `taskStatus`,
`hasSubmittedPrompt`, `requestId`, `lastEventSeq`, nullable `turn`,
`lastTurnReason`, nullable `finalText`, short `progressText`, and
`pendingInteractions`. `taskStatus` is one of `running`, `waitingApproval`,
`waitingInput`, `completed`, `failed`, `cancelled`, or `idle`. Prompt admission
is never mistaken for a completed task during the brief interval before the
first durable turn event. Progress text is derived from the latest real tool
call/result or pending interaction and is bounded for subtitle display.
Before prompt admission, the bridge captures the exact cold follow cursor and
keeps that cursor plus the correlated request ID in process. Status first looks
for the exact `user/message.source.rpcId` in the bounded follow tail, then
incrementally pages backward from a fixed upper cursor with a bounded amount of
work per poll. Once found, its exact event sequence and turn are cached. A long
current turn therefore remains identifiable after its initial user message and
turn-start rows fall outside the follow tail. If the prompt is still queued, or
its durable request ID has not yet been found, the bridge reports it as running
and does not attribute an older or unrelated `turn/end` merely because the
Session is currently idle or has later sequence numbers.

The plugin installs prepend waterfall handlers for DSH's official
`approval/request` and `user-questions/request` events. They call `next()` for
every Session the bridge has not explicitly adopted. A claimed approval
exposes its opaque interaction `id`, exact `sessionId`, tool name, optional
call ID/reason, and only `allowed-once` or `rejected` outcomes. Durable approval
history is never treated as a grant. When the matching live approval has a
`callId` and its real `tool/call` remains in the bounded task tail, the bridge
appends the exact bounded raw tool arguments to DSH's localized approval reason
so the caller can show the concrete operation. If that correlation is not
available, it preserves only DSH's own reason and never invents an operation
description. A question exposes this minimal shape:

```json
{
  "id": "question:...",
  "type": "question",
  "sessionId": "session-...",
  "questions": [{
    "id": "destination",
    "header": "Location",
    "question": "Where should the report be saved?",
    "options": [{ "label": "Documents", "description": "Use Documents" }],
    "multiSelect": false
  }]
}
```

Responses must repeat the current interaction identity and type. Approval uses
`{"outcome":"allowed-once"}` or `{"outcome":"rejected"}`. Question
answers are a complete object of strings keyed by question ID. An empty string
explicitly skips that question. An exact option label selects it. For a
multi-select question, exact labels can be separated by `|`, for example
`"Windows | Linux"`; whitespace is trimmed and duplicates are removed. Any
other non-empty string is sent as the question's custom answer. Missing,
extra, non-string, stale, or mismatched answers are rejected before DSH is
called.

DSH 0.2 can keep timed questions in a `continued` state and exposes the
official `userQuestions.answer` API; the bridge advertises
`continuedQuestionResponse` only when that API and the target Agent registry
are present. DSH 0.1.5 does not expose that capability. Live blocking
questions and approvals are answerable only while their waterfall request is
held by this bridge.

Concurrent replies to the same continued question are reserved by Session and
interaction ID before agent attachment or runtime acknowledgement is awaited.
Only one writer enters DSH; another receives HTTP 409
`interaction_reply_queued`. Accepted replies remain deduplicated while the
question is projected as continued. A failed attachment or an explicit declined
answer releases the in-flight reservation without reporting success.

`POST /v1/tasks/cancel {"sessionId":"..."}` invokes DSH's official
whole-current-turn cancel and keeps the Session plus its inbox intact. It does
not claim to cancel one tool or request ID. `POST /v1/tasks/release` returns
`{"sessionId":"...","released":true}` and only relinquishes bridge
monitoring and answerer ownership. It never cancels the real task. Any live
request already held by the bridge settles fail closed; later waterfall events
for that Session pass to the next answerer. Bridge shutdown applies the same
answerer cleanup without cancelling Sessions. `/v1/status.capabilities`
advertises `taskCreate`, `taskPrompt`, `taskMonitor`, `taskRespond`,
`taskCancel`, `taskAdopt`, `taskRelease`, and
`continuedQuestionResponse` independently.

## One-shot startup patch

DSH accepts relative or absolute plugin paths in an invocation patch. A caller
can create a private patch beside this file, supply the runtime metadata, and
pass it only to the bridge-owned DSH process:

The declarative `@deepseek-ai/dsh-agent-preset` row below is the DSH 0.2 form.

```yaml
- insert:
    - id: halo-session-bridge
      name: ./index.js
      config:
        discoveryDirectory: C:/Users/me/AppData/Local/HaloPixelToolBox/DshSessionBridge/<home-hash>
        home: C:/Users/me/.dsh
        profile: halo-pixelbar
        deviceAgentPreset: halo-device
    - id: preset-halo-device
      name: '@deepseek-ai/dsh-agent-preset'
      config:
        id: halo-device
        order: 100
        plugins:
          - id: persona
            name: '@deepseek-ai/dsh-persona'
            config:
              prefix: >-
                你是 Halo PixelBar 设备助手。准确理解命令，优先调用一个最合适的
                Halo PixelBar 工具；多项设置优先 configure_pixelbar。默认场景指界面恢复默认场景，
                调用 restore_pixelbar_default_scene，组合设置用 restoreDefaultScene=true；
                恢复最近的个性场景，无记录时回退默认时钟，不是目录场景名称或氛围呼吸。
                发送字幕前按55个UTF-8字节组织文字，约18个常见汉字，包含标点、空格与标签；滚动不会扩大限额。
                自动状态只保留阶段、真实结果或下一步，保留失败、未确认、待授权含义；详情留会话和语音。
                用户指定字幕原文超长时说明限制并请其选择缩短内容，不得静默截断或改写。
                调用后用一句中文报告结果。
              complete: true
              includeRuntimeContext: false
```

This preset shape follows DSH's shipped Web presets. `complete: true` replaces
the coding persona with the short device prompt, while tool schemas are still
assembled. In the Halo profile, the PixelBar bundle registers its device tools
globally and the preset inherits them. Keep this declaration in the launcher's
temporary patch rather than editing the user's profile. The launcher should
verify that `/v1/status.deviceAgentPreset` is `halo-device` before enabling
commands.

DSH 0.1.5 uses the older `@deepseek-ai/dsh-agent-presets` catalog instead of
the singular declarative package. A compatible launcher writes the same
`persona` plugin to a private `halo-device/preset.yml` plus
`halo-device/agent.cordis.yml` directory, then adds that private directory as a
trusted catalog root in its one-shot patch. The HTTP bridge and command
contract are otherwise shared by 0.1.5 and 0.2. A launcher must select one of
these two preset forms explicitly and must not fall back to the coding preset.

```powershell
dsh --profile halo-pixelbar --patch <path-to-patch> --host 127.0.0.1 --port 0 --no-open
```

The invocation patch itself does not edit the profile's `package.json` or
`cordis.patch.yml`; ordinary DSH Host startup still owns any runtime state it
normally maintains.

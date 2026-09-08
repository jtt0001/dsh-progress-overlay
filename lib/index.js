/**
 * lib/index.js — dsh-progress-overlay 宿主插件。
 *
 * 数据链路（只读观察者，绝不反向控制任务执行）：
 *
 *   dsh 核心会话总线 (session/event, 与内置 dsh-tauri-pet 同源)
 *      │  ctx.on(...)  （结构化事件，无日志解析）
 *      ▼
 *   lib/state.js 归约器 → 浮窗快照 {primary, running, ...}
 *      │  指纹去重 + 100ms 合帧
 *      ▼
 *   SSE  /api/dsh-overlay/stream   （本机 127.0.0.1）
 *      │
 *      ▼
 *   resources/dsh-overlay.exe —— 独立 Windows Topmost 窗口（可缺席）
 *
 * 稳定性契约：
 *   - Overlay 进程只是 SSE 订阅者；它崩溃/被杀 → 重启或告警，任务不受影响；
 *   - 本插件在非 Windows / overlay.enabled=false 时安全空转；
 *   - 配置命名空间 `overlay`（settings.yaml），watch 变更实时下发并重建窗口策略。
 *
 * 装载契约（与内置 dsh-tauri-pet 完全一致；实测该写法能成功注册路由）：
 *   inject = ['webServer', 'sessions']；服务缺失时 cordis 让插件保持未激活。
 */

import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { existsSync, mkdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { homedir } from 'node:os'
import {
  createSessionState,
  reduceSessionEvent,
  foldOverlaySnapshot,
  extractArgs,
} from './state.js'
import { SETTINGS_NAMESPACE, Config, DEFAULTS, normalizeConfig } from './config.js'

export const name = 'dsh-progress-overlay'

/** 需要的宿主服务：webServer（SSE 路由）+ sessions（会话总线事件发布方）。 */
export const inject = ['webServer', 'sessions']

export { Config }

/** SSE 路径（浮窗与调试客户端订阅）。 */
export const STREAM_PATH = '/api/dsh-overlay/stream'
export const DECISION_PATH = '/api/dsh-overlay/decision'
export const STREAM_NAME = 'dsh-overlay'

const MAX_KEPT_SESSIONS = 6
const IDLE_PRUNE_MS = 30 * 60 * 1000
const BROADCAST_DEBOUNCE_MS = 100
const HEARTBEAT_MS = 15000
const CLIENT_MAX = 16

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

const isObject = (value) => value !== null && typeof value === 'object'

/** 展示指纹：只包含会影响画面的字段（lastActivityAt 等排序字段除外）。 */
function displayFingerprint(snapshot) {
  const pick = (view) => view && {
    id: view.id,
    title: view.title,
    status: view.status,
    phase: view.phase,
    phaseLabel: view.phaseLabel,
    opText: view.opText,
    detail: view.detail,
    todo: view.todo,
    stepsDone: view.stepsDone,
    stepsTotal: view.stepsTotal,
    percent: view.percent,
    startedAt: view.startedAt,
    endedAt: view.endedAt,
    error: view.error,
    waiting: view.waiting,
  }
  return JSON.stringify({
    n: snapshot.runningCount,
    idle: snapshot.idle,
    primary: pick(snapshot.primary),
    running: (snapshot.running ?? []).map(pick),
    prompt: snapshot.prompt ?? null,
  })
}

function sessionIdOf(session, event) {
  const s = session
  return typeof s?.id === 'string' ? s.id
    : typeof s?.sessionId === 'string' ? s.sessionId
      : typeof event?.data?.sessionId === 'string' ? event.data.sessionId
        : null
}

function defaultConfig() {
  return normalizeConfig(DEFAULTS)
}

function overlayStateFile() {
  const home = process.env.DSH_HOME || join(homedir(), '.dsh')
  return join(home, 'dsh-progress-overlay.json')
}

// ---------------------------------------------------------------------------
// 插件主体
// ---------------------------------------------------------------------------

/**
 * @param {any} ctx dsh 宿主上下文（结构匹配，不 import cordis）
 */
export function apply(ctx) {
  // 日志：ctx.logger 可调用时取子 logger；否则退回 console，保证诊断信息
  // 一定落到 dsh 日志里（生产环境不静默）。
  const logger = typeof ctx.logger === 'function'
    ? ctx.logger(name)
    : {
        info: (...args) => console.log(`[${name}]`, ...args),
        warn: (...args) => console.warn(`[${name}]`, ...args),
        error: (...args) => console.error(`[${name}]`, ...args),
      }

  const sessions = new Map() // id -> SessionState
  const clients = new Set()
  const recentTools = new Map() // callId -> { name, args, at }
  const pendingPrompts = new Map() // promptId -> { resolve, timer }
  let currentPrompt = null // 浮窗当前展示的待确认项（审批 / 提问）
  let promptSeq = 0
  let config = defaultConfig()
  let lastFingerprint = null
  let flushTimer = null
  let heartbeatTimer = null
  let stopping = false

  // ---- 子进程（Overlay 窗口）管理 --------------------------------------
  const here = dirname(fileURLToPath(import.meta.url))
  const overlayExe = process.platform === 'win32'
    ? join(here, '..', 'resources', 'dsh-overlay.exe')
    : null
  const overlayHostPs1 = process.platform === 'win32'
    ? join(here, '..', 'resources', 'run-overlay.ps1')
    : null
  const powershellExe = process.platform === 'win32'
    ? join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')
    : null
  let child = null
  let spawnAttempts = 0
  let childSpawnedAt = 0
  let respawnTimer = null

  function broadcastFrame(kind, payload) {
    const body = `data: ${JSON.stringify({ kind, payload })}\n\n`
    for (const res of clients) {
      try { res.write(body) } catch { /* 断开由 close 事件清理 */ }
    }
  }

  function flushSnapshot() {
    flushTimer = null
    const snapshot = foldOverlaySnapshot(sessions, Date.now(), currentPrompt)
    const fingerprint = displayFingerprint(snapshot)
    if (fingerprint === lastFingerprint) return
    lastFingerprint = fingerprint
    broadcastFrame('state', snapshot)
  }

  function scheduleFlush() {
    if (flushTimer !== null) return
    flushTimer = setTimeout(() => {
      try { flushSnapshot() } catch (error) { logger.error('flush failed:', error) }
    }, BROADCAST_DEBOUNCE_MS)
    flushTimer.unref?.()
  }

  function handleSessionEvent(session, rawEvent) {
    const id = sessionIdOf(session, rawEvent)
    if (!id) return
    const type = typeof rawEvent?.type === 'string' ? rawEvent.type : ''
    if (!type) return
    if (type === 'session/disposed') {
      sessions.delete(id)
      scheduleFlush()
      return
    }
    const event = {
      type,
      seq: typeof rawEvent?.seq === 'number' ? rawEvent.seq : 0,
      time: typeof rawEvent?.time === 'number' ? rawEvent.time : Date.now(),
      data: isObject(rawEvent?.data) ? rawEvent.data : {},
    }
    let state = sessions.get(id)
    if (!state) {
      state = createSessionState(id)
      sessions.set(id, state)
    }
    if (type === 'tool/call' && typeof event.data?.name === 'string') {
      const callId = String(event.data.callId ?? event.data.name)
      recentTools.set(callId, { name: event.data.name, args: event.data.arguments, at: Date.now() })
      if (recentTools.size > 64) {
        const oldest = [...recentTools.entries()].sort((a, b) => a[1].at - b[1].at)[0]
        if (oldest) recentTools.delete(oldest[0])
      }
    }
    // 审批/提问一旦有结论，浮窗上的确认卡片要立刻消失
    if (type === 'approval/decided' && currentPrompt && currentPrompt.type === 'approval') {
      clearPrompt('decided')
    }
    if (type === 'tool/result' && currentPrompt && currentPrompt.callId) {
      const callId = String(event.data?.message?.source?.callId ?? event.data?.callId ?? '')
      if (callId && callId === currentPrompt.callId) clearPrompt('done')
    }
    try {
      if (reduceSessionEvent(state, event)) scheduleFlush()
    } catch (error) {
      logger.error(`reduce failed (${type}):`, error)
    }
    pruneSessions()
  }

  function pruneSessions() {
    if (sessions.size <= MAX_KEPT_SESSIONS + 8) return
    const now = Date.now()
    const stale = [...sessions.values()]
      .filter((session) => !session.running && now - session.lastActivityAt > IDLE_PRUNE_MS)
      .sort((a, b) => a.lastActivityAt - b.lastActivityAt)
    for (const session of stale) {
      if (sessions.size <= MAX_KEPT_SESSIONS) break
      sessions.delete(session.id)
    }
  }

  // ---- 待确认项（审批 / 提问）桥接 --------------------------------------
  const PROMPT_TTL_MS = 10 * 60 * 1000

  function pushPrompt(prompt) {
    currentPrompt = prompt
    scheduleFlush()
  }

  function clearPrompt(reason) {
    if (!currentPrompt) return
    const id = currentPrompt.id
    currentPrompt = null
    scheduleFlush()
    for (const res of clients) writeFrame(res, 'prompt-clear', { id, reason })
  }

  /** 把一次审批/提问包装成"浮窗可点击的确认项"，并等待用户选择。 */
  function awaitOverlayDecision(prompt, next) {
    const id = `p${++promptSeq}`
    prompt.id = id
    const promise = new Promise((resolve) => {
      const timer = setTimeout(() => {
        pendingPrompts.delete(id)
        resolve(undefined) // 超时不替用户决定：交给后续回答者（浏览器 UI）
      }, PROMPT_TTL_MS)
      pendingPrompts.set(id, { resolve, timer, prompt })
    })
    pushPrompt(prompt)
    const fallback = Promise.resolve().then(() => next())
    return Promise.race([promise, fallback]).then((value) => {
      const entry = pendingPrompts.get(id)
      if (entry) {
        clearTimeout(entry.timer)
        pendingPrompts.delete(id)
      }
      if (currentPrompt && currentPrompt.id === id) clearPrompt('resolved')
      return value
    })
  }

  function toolDetailFor(callId) {
    if (!callId) return null
    const entry = recentTools.get(String(callId))
    if (!entry) return null
    const { path, command, snippet } = extractArgs(entry.args)
    return command ?? path ?? snippet ?? null
  }

  /** approval/request 回答者：与浏览器 UI 并行，谁先答谁生效。 */
  function handleApprovalRequest(request, next) {
    if (!clients.size) return next()
    const toolName = String(request?.toolName ?? 'tool')
    const callId = request?.callId != null ? String(request.callId) : null
    const detail = toolDetailFor(callId)
    return awaitOverlayDecision({
      type: 'approval',
      callId,
      title: '需要确认',
      toolName,
      detail,
      reason: typeof request?.reason === 'string' ? request.reason : null,
      options: [
        { id: 'allow', label: '允许', value: 'allowed-once' },
        { id: 'deny', label: '拒绝', value: 'rejected' },
      ],
    }, next)
  }

  /** user-questions/request 回答者：有选项的单选题可直接在浮窗点选。 */
  function handleUserQuestion(request, next) {
    if (!clients.size) return next()
    const questions = Array.isArray(request?.questions) ? request.questions : []
    const first = questions[0]
    if (!first || typeof first.question !== 'string') return next()
    const options = Array.isArray(first.options) ? first.options : []
    const selectable = options.length > 0 && first.multiSelect !== true
    const promptOptions = selectable
      ? options.slice(0, 4).map((option, index) => ({
          id: `opt${index}`,
          label: String(option.label ?? `选项 ${index + 1}`).slice(0, 24),
          value: { answers: [{ id: first.id, selected: [String(option.label ?? '')] }] },
        }))
      : []
    return awaitOverlayDecision({
      type: 'question',
      callId: null,
      title: selectable ? '需要选择' : '需要回答',
      toolName: 'ask_user_question',
      detail: first.question.slice(0, 120),
      reason: typeof first.header === 'string' ? first.header : null,
      options: promptOptions,
    }, next)
  }

  // ---- SSE 路由 ---------------------------------------------------------
  function writeFrame(response, kind, payload) {
    try {
      response.write(`data: ${JSON.stringify({ kind, payload })}\n\n`)
    } catch { /* ignore */ }
  }

  const sseHandler = (request, response) => {
    if (clients.size >= CLIENT_MAX) {
      response.writeHead(503)
      response.end('too many clients')
      return
    }
    response.writeHead(200, {
      'content-type': 'text/event-stream; charset=utf-8',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
      'access-control-allow-origin': '*',
    })
    response.write('retry: 2000\n\n')
    clients.add(response)
    // 连接即下发：身份 + 配置 + 当前完整快照（幂等恢复，无需重放历史）
    writeFrame(response, 'hello', { stream: STREAM_NAME, version: 1 })
    writeFrame(response, 'config', { config })
    writeFrame(response, 'state', foldOverlaySnapshot(sessions, Date.now(), currentPrompt))
    const onClose = () => {
      clients.delete(response)
      try { response.end() } catch { /* 忽略 */ }
    }
    request.on('close', onClose)
    request.on('error', onClose)
  }

  /** POST /api/dsh-overlay/decision —— 浮窗点「允许/拒绝/选项」后回传决定。 */
  function decisionHandler(request, response) {
    if (request.method !== 'POST') {
      response.writeHead(405)
      response.end('method not allowed')
      return
    }
    let body = ''
    request.setEncoding('utf8')
    request.on('data', (chunk) => {
      body += chunk
      if (body.length > 8192) request.destroy()
    })
    request.on('end', () => {
      let payload = null
      try { payload = JSON.parse(body) } catch { payload = null }
      const id = payload && typeof payload.id === 'string' ? payload.id : null
      const optionId = payload && typeof payload.optionId === 'string' ? payload.optionId : null
      const entry = id ? pendingPrompts.get(id) : null
      if (!entry || !optionId) {
        response.writeHead(404, { 'content-type': 'application/json' })
        response.end(JSON.stringify({ ok: false, error: 'unknown prompt or option' }))
        return
      }
      const option = (entry.prompt.options ?? []).find((item) => item.id === optionId)
      if (!option) {
        response.writeHead(400, { 'content-type': 'application/json' })
        response.end(JSON.stringify({ ok: false, error: 'unknown option' }))
        return
      }
      clearTimeout(entry.timer)
      pendingPrompts.delete(id)
      entry.resolve(option.value)
      logger.info(`overlay decision: ${entry.prompt.type} -> ${optionId}`)
      response.writeHead(200, { 'content-type': 'application/json' })
      response.end(JSON.stringify({ ok: true }))
    })
    request.on('error', () => { try { response.end() } catch { /* ignore */ } })
  }

  // ---- 设置命名空间（overlay.*）-----------------------------------------
  // Config 为 null 表示 schemastery 不可用（降级模式）：跳过设置注册，
  // 插件仍按 DEFAULTS 工作，绝不因此阻断宿主启动。
  if (Config) {
    ctx.inject?.(['settings'], (sctx) => {
      const sharedKey = Symbol.for('dsh-progress-overlay.settings-scope')
      const shared = (globalThis)[sharedKey] ?? (globalThis[sharedKey] = { scope: null, refs: 0 })
      if (!shared.scope) {
        try {
          shared.scope = sctx.settings.register(SETTINGS_NAMESPACE, Config, { base: DEFAULTS })
        } catch (error) {
          if (!(error instanceof Error) || !error.message.includes('already registered')) throw error
        }
      }
      if (!shared.scope) return
      shared.refs += 1
      applyConfig(normalizeConfig(shared.scope.get()))
      const offWatch = shared.scope.watch(() => {
        try { applyConfig(normalizeConfig(shared.scope.get())) } catch (error) { logger.error('config watch failed:', error) }
      })
      sctx.effect?.(() => () => {
        offWatch?.()
        shared.refs = Math.max(0, shared.refs - 1)
        if (shared.refs === 0) shared.scope = null
      })
    })
  } else {
    logger.warn('settings namespace skipped (schemastery unavailable) — using defaults')
  }

  function applyConfig(next) {
    const previous = config
    config = next
    if (JSON.stringify(next) !== JSON.stringify(previous)) {
      for (const res of clients) writeFrame(res, 'config', { config })
    }
    reconcileOverlay()
  }

  // ---- overlay.exe 生命周期 --------------------------------------------
  function launcherAvailable() {
    if (process.platform !== 'win32') return false
    if (process.env.DSH_OVERLAY_NO_SPAWN === '1') return false // 测试/无界面场景不拉起窗口
    if (overlayExe !== null && existsSync(overlayExe)) return true
    return overlayHostPs1 !== null && powershellExe !== null && existsSync(overlayHostPs1)
  }

  function reconcileOverlay() {
    if (stopping) return
    const want = config.enabled && launcherAvailable()
    if (want && !child && !respawnTimer) spawnOverlay()
    else if (!want && child) killOverlay()
    else if (!want && respawnTimer) { clearTimeout(respawnTimer); respawnTimer = null; spawnAttempts = 0 }
  }

  /** webServer 服务（inject 保证存在；ctx.get 兜底以兼容不同宿主）。 */
  function webServer() {
    return ctx.webServer ?? ctx.get?.('webServer')
  }

  function streamBaseUrl() {
    const server = webServer()
    const port = server && typeof server.port === 'number' ? server.port : 3080
    return `http://127.0.0.1:${port}${STREAM_PATH}`
  }

  function spawnOverlay() {
    const stateFile = overlayStateFile()
    try { mkdirSync(dirname(stateFile), { recursive: true }) } catch { /* ignore */ }
    const commonArgs = ['--url', streamBaseUrl(), '--state', stateFile]
    let file = null
    let args = null
    if (overlayExe !== null && existsSync(overlayExe)) {
      // 首选：独立编译的 WPF 可执行文件
      file = overlayExe
      args = commonArgs
    } else if (overlayHostPs1 !== null && powershellExe !== null && existsSync(overlayHostPs1)) {
      // 兜底：powershell 内存宿主（同一份 C# 源码；被安全软件清理 exe 时可用）
      file = powershellExe
      args = ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', overlayHostPs1, ...commonArgs]
    } else {
      logger.warn(`no overlay launcher available (exe=${overlayExe})`)
      scheduleRespawn()
      return
    }
    logger.info(`starting overlay: ${file} ${args.join(' ')}`)
    let spawned = null
    try {
      spawned = spawn(file, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
    } catch (error) {
      logger.error('spawn failed:', error)
      scheduleRespawn()
      return
    }
    child = spawned
    spawnAttempts += 1
    childSpawnedAt = Date.now()
    const sinceStart = (prefix, data) => {
      const line = String(data ?? '').trim()
      if (line) logger.info(`[overlay:${prefix}] ${line.slice(0, 400)}`)
    }
    spawned.stdout?.on('data', (data) => sinceStart('out', data))
    spawned.stderr?.on('data', (data) => sinceStart('err', data))
    spawned.on('error', (error) => {
      logger.error('overlay error:', error)
      child = null
      scheduleRespawn()
    })
    spawned.on('exit', (code) => {
      const was = child
      child = null
      if (stopping) return
      const uptime = Date.now() - childSpawnedAt
      logger.warn(`overlay exited code=${code} uptime=${uptime}ms (attempt #${spawnAttempts})`)
      if (was && uptime > 60_000) spawnAttempts = 0
      scheduleRespawn()
    })
  }

  function scheduleRespawn() {
    if (stopping || !config.enabled) return
    if (respawnTimer !== null) return
    const delay = Math.min(30_000, 2 ** Math.min(spawnAttempts, 6) * 1000)
    respawnTimer = setTimeout(() => {
      respawnTimer = null
      if (!stopping && config.enabled && !child) spawnOverlay()
    }, delay)
    respawnTimer.unref?.()
  }

  function killOverlay() {
    if (respawnTimer !== null) { clearTimeout(respawnTimer); respawnTimer = null }
    spawnAttempts = 0
    const target = child
    child = null
    if (target) {
      try { target.kill() } catch { /* ignore */ }
    }
  }

  // ---- 装载与拆卸 -------------------------------------------------------
  ctx.effect?.(() => {
    stopping = false

    const offApproval = ctx.on?.('approval/request', handleApprovalRequest)
    const offQuestion = ctx.on?.('user-questions/request', handleUserQuestion)
    const offSessionEvent = ctx.on?.('session/event', handleSessionEvent)
    const offSessionDisposed = ctx.on?.('session/disposed', (session) =>
      handleSessionEvent(session, { type: 'session/disposed', data: {} }))

    // 路由注册：与内置 pet 插件同一写法（ctx.webServer.register）。
    const disposers = []
    const server = webServer()
    if (server && typeof server.register === 'function') {
      try {
        disposers.push(server.register({ kind: 'exact', path: STREAM_PATH, handler: sseHandler }) ?? (() => {}))
        disposers.push(server.register({ kind: 'exact', path: DECISION_PATH, handler: decisionHandler }) ?? (() => {}))
        logger.info(`routes ready: ${STREAM_PATH}, ${DECISION_PATH} (port=${server.port ?? '?'})`)
      } catch (error) {
        logger.error('route register failed:', error)
      }
    } else {
      logger.error('webServer service has no register() — overlay routes not installed')
    }
    const routeDispose = () => { for (const dispose of disposers) { try { dispose() } catch { /* ignore */ } } }

    heartbeatTimer = setInterval(() => {
      for (const res of clients) {
        try { res.write(': keepalive\n\n') } catch { /* ignore */ }
      }
    }, HEARTBEAT_MS)
    heartbeatTimer.unref?.()

    reconcileOverlay()

    return () => {
      stopping = true
      try { routeDispose() } catch { /* ignore */ }
      if (heartbeatTimer !== null) { clearInterval(heartbeatTimer); heartbeatTimer = null }
      if (flushTimer !== null) { clearTimeout(flushTimer); flushTimer = null }
      offApproval?.()
      offQuestion?.()
      offSessionEvent?.()
      offSessionDisposed?.()
      for (const res of clients) { try { res.end() } catch { /* ignore */ } }
      clients.clear()
      killOverlay()
    }
  }, `${name}: lifecycle`)
}

// 注意：**不要**再添加 `export default apply`。
// cordis 的 ModuleLoader.unwrapExports 取 `exports.default ?? exports`：
// 一旦存在 default 导出，插件就被当成裸函数、具名 inject/name 全部丢失，
// 触发 `cannot get property "webServer" without inject` 并让整棵插件树
// 加载失败（已实际发生）。具名导出（name/inject/Config/apply）才是唯一契约。

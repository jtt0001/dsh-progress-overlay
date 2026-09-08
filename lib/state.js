/**
 * lib/state.js — dsh-progress-overlay 归约器（纯逻辑，无依赖）。
 *
 * 把 dsh 会话事件总线（`session/event`，与内置 dsh-tauri-pet 相同的事件源）
 * 折叠成浮窗展示所需的「任务进度视图」。这里只保留确定性纯函数：
 *   会话累计态  --reduceSessionEvent-->  SessionView（发生展示性变化才返回）
 *   会话集合    --foldOverlaySnapshot-->  浮窗快照（primary + 并行任务列表）
 *
 * 原则（见需求）：
 *   - 不伪造任何百分比：percent 只有存在真实 todo 列表（todo/write 事件携带
 *     DSH 自身的 todos 数组）时才由 completed/total 计算，否则恒为 null；
 *   - 状态词一律来自真实事件：turn/start、assistant/chunk（reasoning-delta）、
 *     tool/call、tool/result、todo/write、approval/asked、turn/end(reason)；
 *   - 不解析任何日志文本。
 */

// ---------------------------------------------------------------------------
// 词表 / 分类
// ---------------------------------------------------------------------------

/** 工具名 → 阶段 token（按真实工具名前缀分类；未知工具如实显示为 running-tool）。 */
export function phaseOfTool(name) {
  const value = String(name ?? '').trim()
  if (!value) return 'tool'
  const first = value.split(/[/_\-\s:]/u)[0].toLowerCase()
  // 命令执行类工具：展示其 command 参数
  if (/^(pwsh|powershell|bash|sh|shell|zsh|cmd|terminal|command|exec|run|wscript|cmdline)$/u.test(first) ||
      /^(run_|terminal_|shell_)/u.test(value) ||
      /command$/u.test(value) && !/compact|title/u.test(value)) return 'command'
  // 明确的问题/等待类工具（近似：名称整体 token 匹配问句名词）
  if (/^(ask|asking|request|prompt|confirm|clarif|wait|approve|seek|require|input)/u.test(value) ||
      /user_question|exit_plan/u.test(value)) return 'waiting'
  if (/^(todo|plan)/u.test(value)) return 'planning'
  if (/^(read|read_file|read_url|read_multi|glob|grep|rg|find|search_file|peek|stat|ls|list_file)/u.test(value) ||
      /^fs_/u.test(value)) return 'reading'
  if (/^(web_search|web_fetch|fetch|browse|read_page|search)/u.test(value) || /^(ego_)/u.test(value)) return 'searching'
  if (/^(write|edit|patch|apply_patch|str_replace|replace|insert|create|mkdir|move|rename|delete|remove|copy|duplicate|touch)/u.test(value)) return 'editing'
  if (/^(build|compile|bundle|tsc|esbuild|rollup|transpile)/u.test(value)) return 'building'
  if (/^(test|run_test|lint|check|verify|typecheck|coverage|prettier|format|audit)/u.test(value)) return 'testing'
  return 'tool'
}

/** 阶段 token → 展示词（英文规范词，浮窗再做本地化文案）。 */
export const PHASE_LABEL = {
  starting: 'Starting',
  thinking: 'Thinking',
  planning: 'Planning',
  reading: 'Reading files',
  searching: 'Searching',
  editing: 'Editing',
  writing: 'Writing',
  command: 'Running command',
  tool: 'Running tool',
  testing: 'Running tests',
  building: 'Building',
  waiting: 'Waiting',
  idle: 'Idle',
}

/** 结束原因 kind → 终态（completed / failed / cancelled / waiting）。 */
export function terminalOf(reasonKind) {
  const kind = String(reasonKind ?? '').toLowerCase()
  if (kind === 'completed' || kind === 'success' || kind === 'finished' || kind === '') return 'completed'
  if (kind === 'blocked' || kind === 'waiting') return 'waiting'
  if (/cancel|abort|stop|interrupt|reject/u.test(kind)) return 'cancelled'
  return 'failed'
}

/** 是否为「等用户回答」型工具（approval/澄清/确认），近似判定。 */
export function isUserQuestionTool(name) {
  return phaseOfTool(name) === 'waiting'
}

// ---------------------------------------------------------------------------
// 会话累计态
// ---------------------------------------------------------------------------

const MAX_OPEN_TOOLS = 8

export function createSessionState(id) {
  return {
    id,
    title: null,
    running: false,
    phase: 'idle',
    opText: null,       // 主操作行：路径 / 命令 / 工具名
    detail: null,       // 次操作行：参数摘要
    toolName: null,
    command: null,
    path: null,
    todo: null,         // todo/write 中 in_progress 的当前任务文本
    todoItems: null,    // 最近一次 todo/write 的完整列表快照 [{content,status}]
    stepsDone: 0,
    stepsTotal: 0,
    openTools: new Map(), // callId -> {name, args}
    awaiting: null,     // {kind:'approval'|'user-question'|'blocked', id, label}
    error: null,
    startedAt: null,
    endedAt: null,
    lastActivityAt: 0,
  }
}

/** 从任意参数对象里尽力提取路径 / 命令 / 参数摘要（只读启发式，不猜状态）。 */
export function extractArgs(args) {
  const a = args && typeof args === 'object' ? args : {}
  let path = null
  let command = null
  let snippet = null
  for (const key of ['file_path', 'path', 'file', 'target', 'filePath', 'location']) {
    const value = a[key]
    if (typeof value === 'string' && value) { path = value; break }
  }
  for (const key of ['command', 'cmd', 'cmdline', 'line', 'script']) {
    const value = a[key]
    if (typeof value === 'string' && value) { command = value; break }
  }
  if (command == null && Array.isArray(a.argv)) command = a.argv.join(' ')
  if (command == null && Array.isArray(a.arguments) && typeof a.arguments[0] === 'string') {
    command = a.arguments.join(' ')
  }
  if (snippet == null) {
    // 通用参数摘要：取前两个标量字段
    const parts = []
    for (const [key, value] of Object.entries(a)) {
      if (path != null && key === path) continue
      if (command != null && key === command) continue
      if (typeof value === 'string' && value) parts.push(value)
      if (Array.isArray(value)) parts.push(String(value[0] ?? ''))
      if (parts.length >= 2) break
    }
    snippet = parts.filter(Boolean).join(' ').slice(0, 160) || null
  }
  return { path, command, snippet }
}

const isObject = (value) => value !== null && typeof value === 'object'

/** 从 todo/write 的 data.todos 折叠步骤统计。 */
export function foldTodos(todos) {
  if (!Array.isArray(todos)) return null
  const list = todos.map((item) => {
    const content = isObject(item) && typeof item.content === 'string' ? item.content : ''
    const status = isObject(item) && typeof item.status === 'string' ? item.status : 'pending'
    return { content: content.slice(0, 240), status }
  })
  if (!list.length) return null
  const done = list.filter((item) => item.status === 'completed').length
  const active = list.find((item) => item.status === 'in_progress') ?? list.find((item) => item.status === 'pending')
  return {
    items: list,
    done,
    total: list.length,
    percent: list.length ? Math.round((done / list.length) * 100) : null,
    current: (active && active.content) || null,
  }
}

function toolCallIdOf(event, fallback = '') {
  const message = event.data?.message
  const content = Array.isArray(message?.content)
    ? message.content.find((item) => isObject(item) && item.toolCallId)
    : undefined
  const callId = message?.source?.callId ?? content?.toolCallId ?? message?.toolCallId ?? message?.callId ?? event.data?.callId
  return String(callId ?? fallback)
}

/** 工具调用阶段信息落盘（phase / op / path / command 更新）。 */
function applyToolState(state, name, args) {
  const phase = phaseOfTool(name)
  state.toolName = name
  const { path, command, snippet } = extractArgs(args)
  state.path = path
  state.command = command
  if (phase === 'command') {
    state.phase = 'command'
    state.opText = command
    state.detail = snippet
  } else if (phase === 'waiting') {
    state.phase = 'waiting'
    state.opText = name
    state.detail = snippet
  } else {
    state.phase = phase
    state.opText = path || name
    state.detail = snippet ?? (path ? name : null)
  }
}

function pickOpenTool(state) {
  if (!state.openTools.size) return null
  // Map 保持插入序 → 取最早仍在运行的调用
  const first = state.openTools.values().next().value
  return first
}

/**
 * 单个事件 → 会话累计态。返回 {changed:boolean}；phase/状态等展示字段已同步。
 * 该归约器对齐 dsh-tauri-pet 的事件词汇（turn/start 等），并为未知类型保持无操作。
 */
export function reduceSessionEvent(state, event) {
  const type = typeof event?.type === 'string' ? event.type : ''
  const data = isObject(event?.data) ? event.data : {}
  const time = typeof event?.time === 'number' ? event.time : Date.now()
  let changed = false

  const touch = (running) => {
    if (running) {
      if (!state.running) { state.startedAt = state.startedAt ?? time; state.endedAt = null }
      state.running = true
    }
    state.lastActivityAt = Math.max(state.lastActivityAt, time)
    changed = true
  }

  switch (type) {
    case 'turn/start': {
      // 新一轮（用户发起）任务开始
      state.running = true
      state.endedAt = null
      state.error = null
      state.awaiting = null
      state.openTools.clear()
      state.toolName = null
      state.command = null
      state.path = null
      state.opText = null
      state.detail = null
      state.stepsDone = 0
      state.stepsTotal = 0
      state.todo = null
      state.phase = 'starting'
      state.startedAt = state.startedAt == null ? time : state.startedAt
      state.lastActivityAt = time
      changed = true
      break
    }
    case 'step/start':
      touch(true)
      if (state.phase === 'idle' || state.phase === 'starting' || state.phase === 'completed') state.phase = 'thinking'
      break
    case 'assistant/chunk': {
      touch(true)
      const chunk = data.chunk
      if (isObject(chunk)) {
        if (chunk.type === 'reasoning-delta' && !state.openTools.size && !state.awaiting) {
          state.phase = 'thinking'
          state.opText = null
          state.detail = null
        } else if (chunk.type === 'text-delta' && !state.openTools.size && !state.awaiting) {
          state.phase = 'writing'
          state.opText = null
          state.detail = null
        }
      }
      break
    }
    case 'assistant/message': {
      touch(true)
      const message = data.message
      if (isObject(message)) {
        const hasText = Array.isArray(message.content) && message.content.some((block) =>
          isObject(block) && block.type === 'text' && typeof block.text === 'string' && block.text)
        if (hasText && !state.openTools.size && !state.awaiting) {
          state.phase = 'writing'
          state.opText = null
          state.detail = null
        }
      }
      break
    }
    case 'tool/call': {
      touch(true)
      const name = typeof data.name === 'string' ? data.name : ''
      const callId = typeof data.callId === 'string' ? data.callId : name
      if (name) {
        state.openTools.set(callId || name, { name, args: data.arguments })
        while (state.openTools.size > MAX_OPEN_TOOLS) {
          state.openTools.delete(state.openTools.keys().next().value)
        }
      }
      if (isUserQuestionTool(name)) {
        state.awaiting = { kind: 'user-question', id: callId, label: name }
        state.phase = 'waiting'
        state.opText = name
      } else {
        state.awaiting = null
        applyToolState(state, name, data.arguments)
        if (name && /^todo_write$/u.test(name)) {
          const folded = foldTodos(data.arguments?.todos ?? data.todos)
          if (folded) {
            state.todoItems = folded.items
            state.stepsDone = folded.done
            state.stepsTotal = folded.total
            state.todo = folded.current
          }
          // todo 写入后若无在跑工具，阶段回到 planning
          if (state.openTools.size <= 1) { state.phase = 'planning'; state.opText = null }
        }
      }
      break
    }
    case 'tool/result': {
      touch(true)
      const callId = toolCallIdOf(event)
      if (callId) {
        state.openTools.delete(callId)
        if (state.awaiting?.kind === 'user-question' && state.awaiting.id === callId) state.awaiting = null
      } else {
        state.openTools.clear()
      }
      const open = pickOpenTool(state)
      if (open) {
        applyToolState(state, open.name, open.args)
      } else if (state.awaiting) {
        state.phase = 'waiting'
      } else {
        state.phase = 'thinking'
        state.opText = null
        state.detail = null
      }
      break
    }
    case 'todo/write': {
      touch(true)
      const folded = foldTodos(data.todos)
      if (folded) {
        state.todoItems = folded.items
        state.stepsDone = folded.done
        state.stepsTotal = folded.total
        state.todo = folded.current
      }
      if (['idle', 'starting', 'thinking', 'writing', 'completed'].includes(state.phase)) {
        state.phase = 'planning'
        state.opText = null
        state.detail = null
      }
      break
    }
    case 'approval/asked': {
      touch(true)
      const id = String(data.id ?? '')
      state.awaiting = { kind: 'approval', id, label: data.label ?? '审批请求' }
      state.phase = 'waiting'
      state.opText = state.awaiting.label
      state.detail = null
      break
    }
    case 'approval/decided': {
      const id = String(data.id ?? '')
      if (state.awaiting?.kind === 'approval' && id && id === state.awaiting.id) {
        state.awaiting = null
        state.phase = state.openTools.size ? 'thinking' : 'thinking'
        state.opText = null
        changed = true
      }
      break
    }
    case 'user/message':
      touch(true)
      break
    case 'session/title': {
      if (typeof data.title === 'string' && data.title) {
        state.title = data.title.slice(0, 160)
        changed = true
      }
      break
    }
    case 'turn/end': {
      const reason = isObject(data.reason) ? data.reason : {}
      const kind = typeof reason.kind === 'string' ? reason.kind : 'completed'
      const terminal = terminalOf(kind)
      state.running = false
      state.endedAt = time
      state.awaiting = null
      state.openTools.clear()
      if (terminal === 'failed') {
        state.phase = 'failed'
        state.error = (reason.error && (typeof reason.error === 'string' ? reason.error : reason.error.message)) || `turn ended: ${kind}`
        state.opText = null
      } else if (terminal === 'cancelled') {
        state.phase = 'cancelled'
        state.error = null
        state.opText = null
      } else if (terminal === 'waiting') {
        state.phase = 'waiting'
        state.awaiting = { kind: 'blocked', id: '', label: 'blocked' }
        state.error = null
      } else {
        state.phase = 'completed'
        state.error = null
        state.opText = null
      }
      changed = true
      break
    }
    case 'agent/error': {
      touch(false)
      const message = typeof data.error === 'string' ? data.error
        : isObject(data.error) && typeof data.error.message === 'string' ? data.error.message : null
      state.running = false
      state.endedAt = time
      state.phase = 'failed'
      state.error = message ?? 'agent error'
      changed = true
      break
    }
    default:
      // 未知事件类型：保持无操作（不臆造状态）
      break
  }
  return changed
}

// ---------------------------------------------------------------------------
// 会话视图（浮窗消费的扁平 JSON）
// ---------------------------------------------------------------------------

export function sessionToView(state) {
  const status = !state.running
    ? state.phase === 'completed' ? 'completed'
      : state.phase === 'failed' ? 'failed'
        : state.phase === 'cancelled' ? 'cancelled'
          : state.phase === 'waiting' ? 'waiting'
            : 'idle'
    : state.phase === 'waiting' ? 'waiting'
      : 'running'
  const phase = state.phase
  // 终态优先展示结果短语
  let phaseLabel = PHASE_LABEL[phase] ?? 'Working'
  if (phase === 'completed') phaseLabel = 'Completed'
  if (phase === 'failed') phaseLabel = 'Failed'
  if (phase === 'cancelled') phaseLabel = 'Cancelled'
  return {
    id: state.id,
    title: state.title,
    status,
    phase,
    phaseLabel,
    opText: state.opText,
    detail: state.detail,
    todo: state.todo,
    stepsDone: state.stepsDone,
    stepsTotal: state.stepsTotal,
    percent: state.stepsTotal > 0 ? Math.round((state.stepsDone / state.stepsTotal) * 100) : null,
    startedAt: state.startedAt,
    endedAt: state.endedAt,
    lastActivityAt: state.lastActivityAt,
    error: state.error,
    waiting: state.awaiting ? state.awaiting.label : null,
  }
}

/**
 * 折叠全部会话为浮窗快照。
 * 选择「主任务」：优先仍在运行的会话（最近活动者），否则最近结束的会话；
 * 并行列表包含全部运行中的会话（含 primary，按最近活动排序，最多 5 个）。
 * @param {Map<string, object>} sessions 会话累计态
 * @param {number} now 服务器时间
 * @param {object|null} prompt 当前待用户处理的确认/提问（由宿主插件维护）
 */
export function foldOverlaySnapshot(sessions, now = Date.now(), prompt = null) {
  const list = [...sessions.values()]
    .filter((session) => session.lastActivityAt > 0)
    .sort((a, b) => b.lastActivityAt - a.lastActivityAt)
  const running = list.filter((session) => session.running)
  const ended = list.filter((session) => !session.running)
  const pool = [...running, ...ended]
  const primary = pool.length ? sessionToView(pool[0]) : null
  const runningViews = running.slice(0, 5).map(sessionToView)
  const runningCount = running.length
  return {
    now,
    runningCount,
    idle: runningCount === 0,
    primary,
    running: runningViews,
    prompt: prompt ?? null,
    updatedAt: Date.now(),
  }
}

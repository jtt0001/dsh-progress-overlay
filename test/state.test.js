/**
 * test/state.test.js — 归约器单元测试。
 *
 * 双模式运行：
 *   node --test test/            （标准 runner；受限环境若因 subprocess 隔离
 *                                 报 EPERM，请用下面这条进程内模式）
 *   node test/state.test.js      （进程内直跑，无子进程）
 *
 * 事件样本按 dsh 会话事件总线真实词汇构造（turn/start、assistant/chunk
 * reasoning-delta、tool/call、tool/result、todo/write、approval/asked、
 * turn/end …），与内置 dsh-tauri-pet 的消费词汇一致。
 */

import assert from 'node:assert/strict'

import {
  createSessionState,
  reduceSessionEvent,
  sessionToView,
  foldOverlaySnapshot,
  phaseOfTool,
  terminalOf,
  foldTodos,
} from '../lib/state.js'

// ---------------------------------------------------------------------------
// 极简进程内测试装置（node:test 的 runner 需要 subprocess 隔离，某些受限环境
// 不可用；这里直接收集断言并退出码报告。）
// ---------------------------------------------------------------------------
const tests = []
function test(name, fn) {
  tests.push({ name, fn })
}

function runAll() {
  let failed = 0
  for (const { name, fn } of tests) {
    try {
      fn()
      console.log(`ok - ${name}`)
    } catch (error) {
      failed += 1
      console.error(`not ok - ${name}`)
      console.error(error && error.stack ? error.stack : String(error))
    }
  }
  if (failed > 0) {
    console.error(`\n${failed} of ${tests.length} tests failed`)
    process.exitCode = 1
  } else {
    console.log(`\nall ${tests.length} tests passed`)
  }
}

// ---------------------------------------------------------------------------
// 工具
// ---------------------------------------------------------------------------
function feed(state, type, data, overrides = {}) {
  reduceSessionEvent(state, {
    type,
    time: overrides.time ?? Date.now(),
    seq: 0,
    data,
  })
  return state
}

// ---------------------------------------------------------------------------
// 用例
// ---------------------------------------------------------------------------

test('phaseOfTool classifies real tool names without guessing percentages', () => {
  assert.equal(phaseOfTool('read_file'), 'reading')
  assert.equal(phaseOfTool('grep'), 'reading')
  assert.equal(phaseOfTool('glob'), 'reading')
  assert.equal(phaseOfTool('write'), 'editing')
  assert.equal(phaseOfTool('edit'), 'editing')
  assert.equal(phaseOfTool('pwsh'), 'command')
  assert.equal(phaseOfTool('terminal_command'), 'command')
  assert.equal(phaseOfTool('web_search'), 'searching')
  assert.equal(phaseOfTool('web_fetch'), 'searching')
  assert.equal(phaseOfTool('todo_write'), 'planning')
  assert.equal(phaseOfTool('ask_user'), 'waiting')
  assert.equal(phaseOfTool('some_unknown_tool'), 'tool')
})

test('terminalOf maps turn/end reasons', () => {
  assert.equal(terminalOf('completed'), 'completed')
  assert.equal(terminalOf('error'), 'failed')
  assert.equal(terminalOf('max-tokens'), 'failed')
  assert.equal(terminalOf('timeout'), 'failed')
  assert.equal(terminalOf('cancelled'), 'cancelled')
  assert.equal(terminalOf('aborted'), 'cancelled')
  assert.equal(terminalOf('blocked'), 'waiting')
})

test('foldTodos only yields percent from a real finite todo list', () => {
  assert.equal(foldTodos(undefined), null)
  assert.equal(foldTodos([]), null)
  const folded = foldTodos([
    { content: '第一步', status: 'completed' },
    { content: '第二步', status: 'in_progress' },
    { content: '第三步', status: 'pending' },
  ])
  assert.equal(folded.done, 1)
  assert.equal(folded.total, 3)
  assert.equal(folded.percent, 33)
  assert.equal(folded.current, '第二步')
})

test('full realistic flow: thinking -> reading -> editing -> command -> tests -> completed', () => {
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }
  const state = createSessionState('session-1')

  feed(state, 'turn/start', {}, { time: at() })
  assert.equal(state.phase, 'starting')
  assert.equal(state.running, true)

  feed(state, 'assistant/chunk', { chunk: { type: 'reasoning-delta', text: '让我想想' } }, { time: at() })
  assert.equal(state.phase, 'thinking')

  feed(state, 'todo/write', {
    todos: [
      { content: '修改任务调度逻辑', status: 'in_progress' },
      { content: '跑一遍测试', status: 'pending' },
    ],
  }, { time: at() })
  assert.equal(state.phase, 'planning')
  assert.equal(state.todo, '修改任务调度逻辑')
  assert.equal(state.stepsTotal, 2)
  assert.equal(state.stepsDone, 0)

  feed(state, 'tool/call', { callId: 'c1', name: 'read_file', arguments: { file_path: 'src/task/runner.ts' } }, { time: at() })
  assert.equal(state.phase, 'reading')
  assert.equal(state.path, 'src/task/runner.ts')
  assert.equal(state.opText, 'src/task/runner.ts')

  feed(state, 'tool/result', { message: { source: { callId: 'c1' } } }, { time: at() })
  assert.equal(state.phase, 'thinking')

  feed(state, 'tool/call', { callId: 'c2', name: 'edit', arguments: { file_path: 'src/task/runner.ts' } }, { time: at() })
  assert.equal(state.phase, 'editing')
  assert.equal(state.opText, 'src/task/runner.ts')

  feed(state, 'tool/result', { message: { source: { callId: 'c2' } } }, { time: at() })

  feed(state, 'todo/write', {
    todos: [
      { content: '修改任务调度逻辑', status: 'completed' },
      { content: '跑一遍测试', status: 'in_progress' },
    ],
  }, { time: at() })
  assert.equal(state.stepsDone, 1)
  assert.equal(state.todo, '跑一遍测试')

  feed(state, 'tool/call', { callId: 'c3', name: 'pwsh', arguments: { command: 'npm run test' } }, { time: at() })
  assert.equal(state.phase, 'command')
  assert.equal(state.command, 'npm run test')
  assert.equal(state.opText, 'npm run test')

  const viewRunning = sessionToView(state)
  assert.equal(viewRunning.percent, 50) // 真实 todo 驱动
  assert.equal(viewRunning.status, 'running')

  feed(state, 'tool/result', { message: { source: { callId: 'c3' } } }, { time: at() })

  feed(state, 'turn/end', { reason: { kind: 'completed' } }, { time: at() })
  const view = sessionToView(state)
  assert.equal(view.status, 'completed')
  assert.equal(view.phase, 'completed')
  assert.equal(view.phaseLabel, 'Completed')
  assert.equal(state.running, false)
  assert.ok(state.endedAt > 0)
})

test('tool/result of an approval-style question returns to waiting -> then cancelled by turn/end', () => {
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }
  const state = createSessionState('s2')

  feed(state, 'turn/start', {}, { time: at() })
  feed(state, 'tool/call', { callId: 'q1', name: 'ask_user', arguments: { question: '允许吗?' } }, { time: at() })
  assert.equal(state.phase, 'waiting')
  assert.equal(state.awaiting?.kind, 'user-question')

  // 用户回答后（user/message）随后 turn/end cancelled
  feed(state, 'user/message', { content: 'no' }, { time: at() })
  feed(state, 'turn/end', { reason: { kind: 'cancelled' } }, { time: at() })
  const view = sessionToView(state)
  assert.equal(view.status, 'cancelled')
  assert.equal(view.phaseLabel, 'Cancelled')
})

test('approval asked -> decided -> resumes thinking', () => {
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }
  const state = createSessionState('s3')

  feed(state, 'turn/start', {}, { time: at() })
  feed(state, 'approval/asked', { id: 'a1', label: '运行高危命令?' }, { time: at() })
  assert.equal(state.phase, 'waiting')
  assert.equal(state.awaiting?.kind, 'approval')

  feed(state, 'approval/decided', { id: 'a1', granted: true }, { time: at() })
  assert.equal(state.awaiting, null)
  assert.notEqual(state.phase, 'waiting')
})

test('failure carries a short human summary and is not auto-hidden', () => {
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }
  const state = createSessionState('s4')
  feed(state, 'turn/start', {}, { time: at() })
  feed(state, 'tool/call', { callId: 'c1', name: 'pwsh', arguments: { command: 'npm test' } }, { time: at() })
  feed(state, 'tool/result', { message: { source: { callId: 'c1' } } }, { time: at() })
  feed(state, 'turn/end', { reason: { kind: 'error', error: { message: 'Command exited with code 1' } } }, { time: at() })
  const view = sessionToView(state)
  assert.equal(view.status, 'failed')
  assert.equal(view.error, 'Command exited with code 1')
})

test('overlay snapshot: multi-session aggregation picks newest running as primary', () => {
  const sessions = new Map()
  for (const id of ['a', 'b', 'c']) {
    const state = createSessionState(id)
    sessions.set(id, state)
  }
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }

  feed(sessions.get('a'), 'turn/start', {}, { time: at() })
  feed(sessions.get('b'), 'turn/start', {}, { time: at() })
  feed(sessions.get('b'), 'tool/call', { callId: 'x', name: 'pwsh', arguments: { command: 'npm test' } }, { time: at() })
  // c 已结束（最新结束但不在运行）
  feed(sessions.get('c'), 'turn/start', {}, { time: at() })
  feed(sessions.get('c'), 'turn/end', { reason: { kind: 'completed' } }, { time: at() })

  const snap = foldOverlaySnapshot(sessions)
  assert.equal(snap.runningCount, 2)
  assert.equal(snap.primary.id, 'b') // 仍在运行且活动最新
  assert.ok(snap.running.some((view) => view.id === 'a'))
})

test('snapshot percent stays null without todos (no fabricated progress)', () => {
  let t = 1_700_000_000_000
  const at = () => { t += 1000; return t }
  const state = createSessionState('s5')
  feed(state, 'turn/start', {}, { time: at() })
  feed(state, 'tool/call', { callId: 'c1', name: 'read_file', arguments: { file_path: 'x.ts' } }, { time: at() })
  const view = sessionToView(state)
  assert.equal(view.percent, null)
  assert.equal(view.stepsTotal, 0)
})

// ---------------------------------------------------------------------------
// 运行
// ---------------------------------------------------------------------------
runAll()

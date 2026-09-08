/**
 * test/mock-stream.mjs — 独立冒烟测试用的模拟 DSH 流服务器。
 *
 * 用法：node test/mock-stream.mjs [--port 3901] [--scenario one|cycle]
 *   one    单轮状态推进（thinking → reading → command → completed）
 *   cycle  循环播放（供长时间观察/截图）
 * 默认端口 3901；URL = http://127.0.0.1:<port>/api/dsh-overlay/stream
 */
import http from 'node:http'

const args = process.argv.slice(2)
const port = Number((args[args.indexOf('--port') + 1] ?? 3901))
const scenario = args[args.indexOf('--scenario') + 1] ?? 'one'
const stay = args.includes('--stay') // keep visible even when a fullscreen app is foreground

const config = {
  enabled: true,
  opacity: args.includes('--dim') ? 0.6 : 0.92,
  position: 'remember',
  compactMode: false,
  autoHideOnComplete: true,
  autoHideDelayMs: 5000,
  hideOnFullscreen: !stay,
  clickThrough: false,
  showElapsedTime: true,
  showPercentage: true,
  showTitle: true,
}

const view = (patch) => ({
  id: 's1',
  title: '修复任务调度逻辑',
  status: 'running',
  phase: 'thinking',
  phaseLabel: 'Thinking',
  opText: null,
  detail: null,
  todo: '调查 runner 卡顿',
  stepsDone: 1,
  stepsTotal: 4,
  percent: 25,
  startedAt: Date.now() - 5_000,
  endedAt: null,
  error: null,
  waiting: null,
  ...patch,
})

const sse = (res, msg) => res.write(`data: ${JSON.stringify(msg)}\n\n`)

const server = http.createServer((req, res) => {
  if (req.url !== '/api/dsh-overlay/stream') {
    res.writeHead(404)
    res.end('nf')
    return
  }
  res.writeHead(200, {
    'content-type': 'text/event-stream; charset=utf-8',
    'cache-control': 'no-cache',
    connection: 'keep-alive',
  })
  sse(res, { kind: 'hello', payload: { stream: 'dsh-overlay', version: 1 } })
  sse(res, { kind: 'config', payload: { config } })
  const send = (patch, idle = false) =>
    sse(res, {
      kind: 'state',
      payload: {
        now: Date.now(),
        runningCount: idle ? 0 : 1,
        idle,
        // 完成帧要保留 primary（浮窗靠它显示 ✓ 并停留数秒）；只有显式 idle 帧才置空
        primary: view(patch),
        running: [],
      },
    })

  const schedule = (fn, ms) => setTimeout(fn, ms)

  // 多任务并行场景：3 个运行中任务（不同阶段/进度），用于验证浮窗逐条显示
  const longTitle = args.includes('--long')
  const pad = (text) => (longTitle ? text + ' · 这一条故意写得很长用于验证宽度自适应' : text)
  const runMulti = () => {
    const a = view({ id: 's1', title: pad('修复任务调度逻辑'), todo: pad('跑测试'), phase: 'command', phaseLabel: 'Running command', opText: 'npm test', stepsDone: 2, stepsTotal: 4 })
    const b = view({ id: 's2', title: pad('重构设置面板'), todo: pad('改配置文件'), phase: 'editing', phaseLabel: 'Editing', opText: 'src/settings.ts', stepsDone: 1, stepsTotal: 3, startedAt: Date.now() - 32_000 })
    const c = view({ id: 's3', title: pad('整理文档'), todo: pad('读 README'), phase: 'reading', phaseLabel: 'Reading files', opText: 'docs/README.md', stepsDone: 0, stepsTotal: 5, startedAt: Date.now() - 9_000 })
    const push = (running) => sse(res, {
      kind: 'state',
      payload: { now: Date.now(), runningCount: running.length, idle: running.length === 0, primary: running[0] ?? null, running },
    })
    push([a, b, c])
    schedule(() => push([a, { ...b, phase: 'testing', phaseLabel: 'Running tests', opText: 'vitest', stepsDone: 2 }, { ...c, stepsDone: 1 }]), 4_000)
    schedule(() => push([{ ...a, stepsDone: 3 }, b, { ...c, phase: 'editing', phaseLabel: 'Editing', opText: 'docs/README.md' }]), 8_000)
    schedule(() => push([]), 14_000)
    if (scenario === 'cycle') schedule(runMulti, 20_000)
  }

  // 确认项场景：运行中任务 + 弹出审批（允许/拒绝），10 秒后自动消失
  const runPrompt = () => {
    const task = view({ phase: 'command', phaseLabel: 'Running command', opText: 'rm -rf build && npm run build', todo: '清理并重建' })
    const base = (prompt) => ({
      kind: 'state',
      payload: { now: Date.now(), runningCount: 1, idle: false, primary: task, running: [task], prompt: prompt ?? null },
    })
    sse(res, base(null))
    schedule(() => sse(res, base({
      id: 'p1', type: 'approval', title: '需要确认',
      toolName: 'pwsh', detail: 'rm -rf build && npm run build',
      reason: '该命令会删除 build 目录', options: [
        { id: 'allow', label: '允许' },
        { id: 'deny', label: '拒绝' },
      ],
    })), 3_000)
    schedule(() => sse(res, base(null)), 12_000)
    if (scenario === 'cycle') schedule(runPrompt, 18_000)
  }

  const runOnce = () => {
    send({ phase: 'starting', phaseLabel: 'Starting', todo: '启动任务' })
    schedule(() => send({ phase: 'thinking', phaseLabel: 'Thinking' }), 2_000)
    schedule(() => send({ phase: 'reading', phaseLabel: 'Reading files', opText: 'src/task/runner.ts', detail: 'read_file' }), 5_000)
    schedule(() => send({ phase: 'editing', phaseLabel: 'Editing', opText: 'src/task/runner.ts', detail: 'edit' }), 9_000)
    schedule(() => send({
      phase: 'command', phaseLabel: 'Running command',
      opText: 'npm run test', detail: 'pwsh',
      todo: '运行测试', stepsDone: 2, stepsTotal: 4, percent: 50,
    }), 13_000)
    schedule(() => send({
      status: 'completed', phase: 'completed', phaseLabel: 'Completed',
      opText: null, detail: null, todo: '全部完成', stepsDone: 4, stepsTotal: 4,
      percent: 100, endedAt: Date.now(),
    }, true), 17_000)
    schedule(() => sse(res, { kind: 'state', payload: { now: Date.now(), runningCount: 0, idle: true, primary: null, running: [] } }), 24_000)
    if (scenario === 'cycle') schedule(runOnce, 28_000)
  }
  if (scenario === 'multi') runMulti()
  else if (scenario === 'prompt') runPrompt()
  else runOnce()
  const ping = setInterval(() => res.write(': keepalive\n\n'), 15_000)
  req.on('close', () => clearInterval(ping))
})

server.listen(port, '127.0.0.1', () => {
  console.log(`mock stream: http://127.0.0.1:${port}/api/dsh-overlay/stream`)
})

// test/smoke-apply.mjs — apply() 冒烟（进程内，无真实宿主、无网络）。
// 覆盖：
//   1) 注入 webServer 时注册 SSE 路由与决策路由；
//   2) 事件总线回放不抛错；
//   3) 审批桥接：approval/request 回答者挂起 → 浮窗侧 POST 决策 → 返回 allowed-once；
//   4) 卸载后路由注销、无 webServer 时不崩溃。
process.env.DSH_OVERLAY_NO_SPAWN = '1'
import { EventEmitter } from 'node:events'
import { apply, STREAM_PATH, DECISION_PATH } from '../lib/index.js'

function makeCtx({ withServer }) {
  const handlers = new Map()
  const effects = []
  const routes = new Map()
  const ctx = {
    get: () => undefined,
    inject: () => undefined,
    on: (name, fn) => {
      handlers.set(name, fn)
      return () => handlers.delete(name)
    },
    effect: (fn) => {
      const disposer = fn()
      effects.push(disposer)
      return disposer
    },
  }
  if (withServer) {
    ctx.webServer = {
      port: 39999,
      register: (route) => {
        routes.set(route.path, route)
        return () => routes.delete(route.path)
      },
    }
  }
  return { ctx, handlers, effects, routes }
}

let failed = 0
const check = (ok, label) => {
  console.log((ok ? 'ok - ' : 'not ok - ') + label)
  if (!ok) failed += 1
}

function fakeSseClient() {
  const frames = []
  const request = new EventEmitter()
  request.setEncoding = () => {}
  const response = {
    writeHead: () => {},
    write: (chunk) => { frames.push(chunk); return true },
    end: () => {},
  }
  return { request, response, frames }
}

function fakePost(body) {
  const request = new EventEmitter()
  request.method = 'POST'
  request.setEncoding = () => {}
  request.destroy = () => {}
  const captured = { status: 0, body: '' }
  const response = {
    writeHead: (status) => { captured.status = status },
    end: (chunk) => { if (chunk) captured.body += chunk },
  }
  return { request, response, captured, body }
}

// 1) 有 webServer：路由 + 事件回放 + 审批桥接
{
  const { ctx, handlers, effects, routes } = makeCtx({ withServer: true })
  apply(ctx)
  check(routes.has(STREAM_PATH) && routes.get(STREAM_PATH).kind === 'exact', 'SSE 路由已注册（kind=exact）')
  check(routes.has(DECISION_PATH) && routes.get(DECISION_PATH).kind === 'exact', '决策路由已注册')

  const session = { id: 's-test' }
  let threw = null
  for (const [type, data] of [
    ['turn/start', {}],
    ['tool/call', { callId: 'c1', name: 'pwsh', arguments: { command: 'rm -rf build && npm run build' } }],
    ['assistant/chunk', { chunk: { type: 'reasoning-delta', text: 'x' } }],
    ['todo/write', { todos: [{ content: 'a', status: 'in_progress' }, { content: 'b', status: 'pending' }] }],
    ['turn/end', { reason: { kind: 'completed' } }],
  ]) {
    try {
      handlers.get('session/event')(session, { type, seq: 1, time: Date.now(), data })
    } catch (error) { threw = error; break }
  }
  check(threw === null, '事件总线回放无异常')

  // 审批桥接：连接一个假 SSE 客户端 → 触发 approval/request → POST 决策
  const sse = fakeSseClient()
  routes.get(STREAM_PATH).handler(sse.request, sse.response)
  const answerer = handlers.get('approval/request')
  check(typeof answerer === 'function', 'approval/request 回答者已注册')

  let resolved
  // next() 永不 resolve：模拟真实环境里"浏览器 UI 也在等用户回答"
  const pending = answerer({ toolName: 'pwsh', callId: 'c1', reason: '该命令会删除 build 目录' },
    () => new Promise(() => {}))
  pending.then((value) => { resolved = value })

  setTimeout(() => {
    // 等首帧（含 prompt）下发
    const hasPrompt = sse.frames.some((frame) => frame.includes('"prompt"') && frame.includes('"allow"'))
    check(hasPrompt, '浮窗收到待确认项（含允许/拒绝选项）')

    const post = fakePost('')
    routes.get(DECISION_PATH).handler(post.request, post.response)
    post.request.emit('data', JSON.stringify({ id: 'p1', optionId: 'allow' }))
    post.request.emit('end')

    setTimeout(() => {
      check(post.captured.status === 200, '决策接口返回 200')
      check(resolved === 'allowed-once', '点击「允许」后审批结果为 allowed-once（实际拿到：' + resolved + '）')

      for (const dispose of effects) dispose()
      check(routes.size === 0, '卸载后路由已注销')
      finish()
    }, 60)
  }, 200)
}

// 2) 无 webServer：不抛异常
{
  const { ctx, effects } = makeCtx({ withServer: false })
  let threw = null
  try { apply(ctx) } catch (error) { threw = error }
  check(threw === null, '缺少 webServer 时 apply 不崩溃')
  for (const dispose of effects) dispose()
}

function finish() {
  console.log(failed === 0 ? '\nsmoke apply passed' : `\n${failed} smoke checks failed`)
  if (failed > 0) process.exitCode = 1
}

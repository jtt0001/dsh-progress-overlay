# dsh-progress-overlay

**A real Windows always-on-top progress overlay for DeepSeek Harness (DSH) tasks.**
While DSH is working, a small standalone window in the screen corner shows what it is
actually doing — phase (thinking / reading / editing / running a command / testing),
the file or command being handled, real todo-based step progress, elapsed time, and the
final result. It also lets you answer DSH approval requests right from the overlay.

- **Separate OS window**, independent of the DSH main window / web UI: it stays visible
  while DSH is minimized, covered, or in the background.
- **Never steals focus**: `WS_EX_NOACTIVATE`, no `SetForegroundWindow` anywhere (the only
  exception is the explicit “open DSH main window” menu item); state refreshes never
  interrupt typing.
- **No fabricated progress**: a percentage is shown only when DSH's own todo events carry
  a real `completed/total`; otherwise the overlay shows phase and activity only.
- **Read-only by default**: state comes from the DSH session event bus
  (`session/event`), the same source the built-in `dsh-tauri-pet` uses. Overlay crash or
  close never affects the task. The single write path is an approval decision you click.
- Per-monitor-v2 DPI, multi-monitor work-area placement, drag position persistence with
  on-screen validation, fullscreen auto-hide, orange/red/blue attention flashes.

> **Windows only.** The overlay window is a .NET Framework 4.8 WPF executable
> (built into Windows 10/11 — no extra runtime needed). On non-Windows hosts the plugin
> stays dormant.

## Install

```sh
dsh plugin --profile desktop add github:<owner>/dsh-progress-overlay
# then restart DeepSeek Harness Desktop
```

The repository ships the prebuilt `resources/dsh-overlay.exe`; if a security suite
quarantines that unsigned binary, the plugin automatically falls back to a PowerShell
in-memory host that compiles the same C# source (`resources/run-overlay.ps1`) — same
feature set, slower startup. Whitelisting the plugin folder keeps the native path.

---

# 中文说明

一个真正的 **Windows 全局置顶 Overlay 窗口**：当 DeepSeek Harness（DSH）执行任务时，
在屏幕角落的独立小窗里实时显示**它当前在做什么**——阶段（思考/读文件/改代码/跑测试/跑命令）、
正在操作的文件或命令、真实步骤进度（有 todo 数据时）、已运行时长、完成/失败结果。

- 独立 OS 窗口，与 DSH 主窗口/Web UI 完全无关：主窗口最小化、被遮挡、切后台都照常显示；
- **从不抢焦点**：`WS_EX_NOACTIVATE` + 全程无 `SetForegroundWindow`（唯一例外：用户主动点
  「打开 DSH 主窗口」菜单）；任何状态刷新都不会打断你正在输入的内容；
- **不伪造进度**：只有 DSH 自带 todo 事件提供真实 `completed/total` 时才显示 `4 / 7 · 50%`，
  否则只显示阶段与操作；`percent` 为 null 时 UI 自动隐藏百分比；
- 数据来自 **DSH 会话事件总线**（`session/event`，与内置 dsh-tauri-pet 同源），纯只读观察者：
  浮窗崩溃/被杀不影响任务；关浮窗≠取消任务；
- PerMonitorV2 DPI、多显示器工作区定位、拖动后位置持久化并校验可见性、全屏自动隐藏等。

## 组成

```
lib/index.js      宿主插件（dsh 侧）：订阅 session/event → 归约 → SSE /api/dsh-overlay/stream；
                  管理 overlay 进程生命周期（exe 优先，powershell 内存宿主兜底）
lib/state.js      纯归约器：事件 → 会话视图/浮窗快照（percent 真实性规则在此）
lib/config.js     `overlay` 设置命名空间 schema（settings.yaml 持久化）
resources/dsh-overlay.cs   浮窗本体（C#/.NET Framework 4.8 WPF + Win32，单文件）
resources/build.ps1        用系统自带 csc 编译出 resources/dsh-overlay.exe
resources/run-overlay.ps1  powershell 内存宿主：把同一份 .cs 经 Add-Type 在进程内编译运行
                          （被 360 等安全软件清理无签名 exe 时的兜底路径）
test/             归约器单测（node test/state.test.js）+ mock SSE 流（联调用）
```

## 安装（桌面版）

```powershell
# 1) 构建 exe（可选；无 exe 时插件会自动使用 powershell 兜底宿主）
powershell -NoProfile -File D:\DeepSeek\dsh-progress-overlay\resources\build.ps1

# 2) 装进 desktop profile（推荐 link: 规格，源码改动实时生效）
#    在 ~/.dsh/profiles/desktop/package.json 里：
#      dependencies: { "dsh-progress-overlay": "link:D:/DeepSeek/dsh-progress-overlay" }
#      dsh.profile.bundles: [..., "dsh-progress-overlay"]
#    然后在该目录执行 pnpm install
dsh plugin --profile desktop add link:D:/DeepSeek/dsh-progress-overlay

# 3) 重启 DeepSeek Harness Desktop
```

> **file: 规格的坑**：pnpm 会把 `file:` 依赖按内容快照进 store，源码后续修改不会同步；
> 调试期请用 `link:`（桌面端内部插件也是 link: 规格）。生产分发可用 `file:` + 版本号递增。
>
> 360 安全卫士会把“本地刚编译的无签名 exe”静默清理：若发现 overlay 一直走不了 exe，
> 请把插件目录加入 360 信任区（或直接依赖兜底宿主，功能等价）。

## 设置（DSH 设置 → overlay）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | true | 总开关 |
| `opacity` | 0.92 | 卡片不透明度 |
| `position` | remember | top-right / top-left / bottom-right / bottom-left / remember |
| `compactMode` | false | 启动即折叠单行 |
| `autoHideOnComplete` | true | 完成后显示 ✓ 数秒自动隐藏 |
| `autoHideDelayMs` | 5000 | 完成后停留时长 |
| `hideOnFullscreen` | true | 全屏应用（浏览器/播放器/PPT/游戏）时隐藏，退出自动恢复 |
| `clickThrough` | false | 鼠标穿透（开启后不可拖动/点按钮） |
| `showElapsedTime` | true | 已运行时长 |
| `showPercentage` | true | 有真实进度时显示百分比 |
| `showTitle` | true | 任务/会话标题行 |

## 交互

- 拖卡片空白处 → 移动；松手自动收进当前显示器工作区并保存位置；
- `–` 折叠 / `✕` 隐藏（任务继续）；折叠条点击或双击展开；
- **右键菜单（本地即时生效并记忆，无需改 DSH 配置）**：
  - 打开 DSH 主窗口 / 展开折叠 / 隐藏浮窗
  - **透明度**：60% / 75% / 85% / 92% / 100%
  - 鼠标穿透（开/关）
  - 完成后自动隐藏（开/关）
- **多任务并行**：同时有多个任务运行时，浮窗逐条列出每个任务
  （阶段 · 标题/当前待办 · 步骤 · 耗时，最多 5 条），窗口高度自动增长；
  列表首条为主任务（加粗）。
- **状态闪烁（描边由外向内收拢再散开）**：
  - 任务完成 → **浅蓝**；
  - 任务失败 / 出错 → **红**（并且浮窗自动弹出来，保持显示）；
  - 需要你确认 / 回答 → **橙**（浮窗自动弹出）。
- **直接在浮窗审批**：DSH 请求审批时，浮窗显示"需要确认 + 工具名 + 命令/路径/原因"，
  并给出 **允许 / 拒绝** 按钮——点击即等同在 DSH 界面里作答（走官方 `approval/request`
  回答者链路，结论为 `allowed-once` / `rejected`）。若你在 DSH 界面里先答了，
  浮窗上的卡片会自动消失。
  - 提问类（`ask_user_question`）带选项时同样显示选项按钮；纯文本提问只展示问题，
    仍需在 DSH 里输入回答。
  - 说明：审批只在策略为"询问"时出现；`auto-approve` 预设或你自己的
    `dsh-approval-gate` 自动放行的命令不会弹确认。
- 完成：`✓ Completed`（停留后自动隐藏，可关闭）；失败：`✕` + 简短原因，保持显示到
  新任务或手动关闭；取消同理短暂展示。

## 验收

1. `node test/state.test.js` —— 归约器单测（9 项）；
2. `node test/smoke-apply.mjs` —— 路由注册 / 事件回放 / **审批桥接**（浮窗收到确认项 →
   POST 决策 → 返回 `allowed-once`）/ 卸载，共 9 项；
3. `node test/mock-stream.mjs --port 3901 --scenario prompt|multi|cycle` +
   运行 `resources/run-overlay.ps1` 或 exe → 观察确认项面板、多任务列表、闪烁与自动隐藏；
4. 实际任务验证：在 DSH 里跑一次长任务，把焦点切到别的程序——浮窗实时跟随任务状态。

## 局限

- 全屏**独占**模式（DX 全屏游戏）由系统决定层级，无法也无意强行覆盖；
- 状态语义依赖会话事件总线；某些自定义/无会话的调用（如部分后台 job）可能不产生事件。

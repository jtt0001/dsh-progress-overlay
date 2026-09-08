/**
 * lib/config.js — `overlay` 设置命名空间的 schema（schemastery）。
 *
 * 由 dsh 设置系统（settings.yaml 的 `overlay:` 块）持久化；宿主装载时以
 * `base: DEFAULTS` 作为首启种子。浮窗每次连接与配置变更时都会收到 config。
 *
 * 说明：
 *  - alwaysOnTop 不设开关：本浮窗是“真正的 Windows Topmost 窗口”，置顶是其
 *    存在意义（见需求 §2）。隐藏请用 enabled/hide。
 *  - showPercentage=true 只表示“存在真实百分比时显示”；服务器端 percent 在无
 *    todo 数据时恒为 null（见 lib/state.js）。
 *
 * 健壮性（重要）：schemastery 用**动态导入**解析。link: 安装的插件目录可能没有
 * 自己的 node_modules；静态 import 一旦失败会让整个 loader entry 导入失败，从而
 * 阻断 dsh 启动（已实际发生）。动态导入失败时这里降级为 `Config = null`，
 * 插件照常工作（用 DEFAULTS），只是设置页里不出现 overlay 命名空间。
 */

export const SETTINGS_NAMESPACE = 'overlay'

let z = null
try {
  const mod = await import('schemastery')
  z = mod?.default ?? mod
} catch (error) {
  // 只在控制台提示一次；不影响插件主体（浮窗仍按 DEFAULTS 工作）
  console.warn('[dsh-progress-overlay] schemastery unavailable, settings namespace disabled:', error?.message ?? error)
}

/** schemastery schema；依赖缺失时为 null（调用方需判空）。 */
export const Config = z
  ? z.object({
      enabled: z.boolean().default(true).description('启用系统级置顶进度浮窗（Windows）。'),
      opacity: z.number().min(0.5).max(1).step(0.01).default(0.92).description('浮窗不透明度。'),
      position: z.union(['top-right', 'top-left', 'bottom-right', 'bottom-left', 'remember'])
        .default('remember')
        .description('默认位置；remember = 恢复上次拖动位置（自动校验仍在可见屏幕内）。'),
      compactMode: z.boolean().default(false).description('启动即进入折叠模式（单行）。'),
      autoHideOnComplete: z.boolean().default(true).description('任务完成后显示 ✓ 数秒，然后自动隐藏。'),
      autoHideDelayMs: z.number().min(1000).max(30000).step(500).default(5000).description('完成后停留时长（毫秒）。'),
      hideOnFullscreen: z.boolean().default(true).description('检测到全屏应用（浏览器/播放器/PPT/游戏）时暂时隐藏，退出全屏自动恢复。'),
      clickThrough: z.boolean().default(false).description('鼠标穿透模式：浮窗不拦截下方应用的点击（拖动/按钮同时失效，需在设置里关闭）。'),
      showElapsedTime: z.boolean().default(true).description('显示已运行时长。'),
      showPercentage: z.boolean().default(true).description('存在真实进度（todo 步骤）时显示百分比与步骤数。'),
      showTitle: z.boolean().default(true).description('显示当前任务/会话标题行。'),
    })
  : null

/** 编译期默认（与 schema default 一致，供 base 与测试使用）。 */
export const DEFAULTS = {
  enabled: true,
  opacity: 0.92,
  position: 'remember',
  compactMode: false,
  autoHideOnComplete: true,
  autoHideDelayMs: 5000,
  hideOnFullscreen: true,
  clickThrough: false,
  showElapsedTime: true,
  showPercentage: true,
  showTitle: true,
}

/** 归一化（越界收敛），供设置读取兜底。 */
export function normalizeConfig(raw) {
  const value = raw && typeof raw === 'object' ? raw : {}
  const config = { ...DEFAULTS, ...value }
  config.opacity = Math.min(1, Math.max(0.5, Number(config.opacity) || DEFAULTS.opacity))
  config.autoHideDelayMs = Math.min(30000, Math.max(1000, Number(config.autoHideDelayMs) || DEFAULTS.autoHideDelayMs))
  config.enabled = Boolean(config.enabled)
  config.compactMode = Boolean(config.compactMode)
  config.autoHideOnComplete = Boolean(config.autoHideOnComplete)
  config.hideOnFullscreen = Boolean(config.hideOnFullscreen)
  config.clickThrough = Boolean(config.clickThrough)
  config.showElapsedTime = Boolean(config.showElapsedTime)
  config.showPercentage = Boolean(config.showPercentage)
  config.showTitle = Boolean(config.showTitle)
  if (!['top-right', 'top-left', 'bottom-right', 'bottom-left', 'remember'].includes(config.position)) {
    config.position = DEFAULTS.position
  }
  return config
}

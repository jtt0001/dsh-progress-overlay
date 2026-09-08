// test/measure-overlay.mjs — 用 UI Automation 读取浮窗内各文字元素的矩形并检测重叠。
// 只读操作：不注入键鼠、不抓屏。
import { execFileSync } from 'node:child_process'

const targetPid = Number(process.argv[2])
if (!Number.isInteger(targetPid)) {
  console.error('usage: node measure-overlay.mjs <overlay-pid>')
  process.exit(2)
}

const ps = String.raw`
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$targetPid = ${targetPid}
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $targetPid)
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $root) { Write-Output 'NO_WINDOW'; exit 0 }
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$rows = @()
foreach ($el in $all) {
  $r = $el.Current.BoundingRectangle
  if ($r.Width -le 0 -or $r.Height -le 0) { continue }
  $name = ($el.Current.Name -replace '\s+', ' ')
  if ($name.Length -gt 60) { $name = $name.Substring(0, 60) }
  $rows += [pscustomobject]@{ Type = $el.Current.ControlType.ProgrammaticName; Name = $name; X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height }
}
$rows | ConvertTo-Json -Depth 3
`

const out = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', ps], { encoding: 'utf8' })
if (out.trim() === 'NO_WINDOW') {
  console.log('NO_WINDOW')
  process.exit(0)
}
const items = JSON.parse(out)
const texts = items.filter((item) => item.W > 0 && item.H > 0)
console.log('元素数:', texts.length)
for (const item of texts) {
  console.log(`  ${item.Type.replace('ControlType.', '')} "${item.Name}" @${item.X},${item.Y} ${item.W}x${item.H}`)
}

let overlaps = 0
for (let i = 0; i < texts.length; i++) {
  for (let j = i + 1; j < texts.length; j++) {
    const a = texts[i]
    const b = texts[j]
    const ix = Math.max(0, Math.min(a.X + a.W, b.X + b.W) - Math.max(a.X, b.X))
    const iy = Math.max(0, Math.min(a.Y + a.H, b.Y + b.H) - Math.max(a.Y, b.Y))
    const inter = ix * iy
    const smaller = Math.min(a.W * a.H, b.W * b.H)
    if (inter > smaller * 0.4) {
      overlaps += 1
      console.log(`重叠: "${a.Name}" (${a.X},${a.Y} ${a.W}x${a.H}) 与 "${b.Name}" (${b.X},${b.Y} ${b.W}x${b.H})`)
    }
  }
}
console.log('重叠对数:', overlaps)

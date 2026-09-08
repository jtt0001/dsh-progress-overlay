// dsh-overlay.cs — DSH 系统级全局置顶任务进度浮窗（Windows）。
//
// 独立进程（非 Electron/非 dsh web 内部组件）：
//   编译目标  .NET Framework 4.8 WPF（系统自带），csc 4.8（Roslyn C# 7.3）
//   窗口     独立 OS 窗口：Topmost + WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW，
//             从不激活/夺取前台焦点（SetForegroundWindow 仅用于“用户主动
//             点击打开 DSH 主窗口”菜单）。
//   数据     只读 SSE 客户端订阅 /api/dsh-overlay/stream（结构化事件）；
//             崩溃/断连只影响本窗口，不影响 DSH 任务。
//   显示器   PerMonitorV2 DPI；所有定位使用物理像素 + 工作区（GetMonitorInfo），
//             位置持久化到 --state 文件并在启动时校验仍在可见屏幕内。
//
// 分区（同文件内按职责，便于系统 csc 单文件编译）：
//   Native         Win32 P/Invoke
//   OverlayConfig  服务器配置快照
//   OverlayState   服务器状态快照（SessionView）
//   Texts          文案/颜色/格式化
//   OverlayWindow  视觉与交互（纯代码构建 UI，无 XAML 编译依赖）
//   SseClient      流订阅 + 心跳/重连/超时退出
//   Json           极简 JSON 解析
//   Program        入口

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DshOverlay
{
    // =====================================================================
    // Native — Win32
    // =====================================================================
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOZORDER = 0x0004;
        public const int SWP_NOACTIVATE = 0x0010;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_RESTORE = 9;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const int WM_HOTKEY = 0x0312;
        public const uint VK_O = 0x4F;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public static MONITORINFO MonitorInfo(IntPtr hMonitor)
        {
            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(hMonitor, ref info);
            return info;
        }

        public static MONITORINFO MonitorInfoFromPoint(int x, int y)
        {
            var pt = new POINT { X = x, Y = y };
            return MonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
        }

        public static MONITORINFO MonitorInfoFromWindow(IntPtr hwnd)
        {
            return MonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        }

        public static RECT WindowRect(IntPtr hwnd)
        {
            RECT rect;
            GetWindowRect(hwnd, out rect);
            return rect;
        }
    }

    // =====================================================================
    // OverlayConfig — 服务器下发的配置
    // =====================================================================
    internal sealed class OverlayConfig
    {
        public bool Enabled = true;
        public double Opacity = 0.92;
        public string Position = "remember";
        public bool CompactMode = false;
        public bool AutoHideOnComplete = true;
        public double AutoHideDelayMs = 5000;
        public bool HideOnFullscreen = true;
        public bool ClickThrough = false;
        public bool ShowElapsedTime = true;
        public bool ShowPercentage = true;
        public bool ShowTitle = true;

        public static OverlayConfig FromJson(Dictionary<string, object> map)
        {
            var cfg = new OverlayConfig();
            if (map == null) return cfg;
            cfg.Enabled = GetBool(map, "enabled", cfg.Enabled);
            cfg.Opacity = Math.Min(1.0, Math.Max(0.5, GetDouble(map, "opacity", cfg.Opacity)));
            cfg.Position = GetString(map, "position", cfg.Position);
            cfg.CompactMode = GetBool(map, "compactMode", cfg.CompactMode);
            cfg.AutoHideOnComplete = GetBool(map, "autoHideOnComplete", cfg.AutoHideOnComplete);
            cfg.AutoHideDelayMs = Math.Min(30000, Math.Max(1000, GetDouble(map, "autoHideDelayMs", cfg.AutoHideDelayMs)));
            cfg.HideOnFullscreen = GetBool(map, "hideOnFullscreen", cfg.HideOnFullscreen);
            cfg.ClickThrough = GetBool(map, "clickThrough", cfg.ClickThrough);
            cfg.ShowElapsedTime = GetBool(map, "showElapsedTime", cfg.ShowElapsedTime);
            cfg.ShowPercentage = GetBool(map, "showPercentage", cfg.ShowPercentage);
            cfg.ShowTitle = GetBool(map, "showTitle", cfg.ShowTitle);
            return cfg;
        }

        private static bool GetBool(IDictionary<string, object> map, string key, bool fallback)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }

        private static double GetDouble(IDictionary<string, object> map, string key, double fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string GetString(IDictionary<string, object> map, string key, string fallback)
        {
            object value;
            return map.TryGetValue(key, out value) && value is string ? (string)value : fallback;
        }
    }

    // =====================================================================
    // OverlayState / SessionView — 服务器状态快照
    // =====================================================================
    internal sealed class SessionView
    {
        public string Id = "";
        public string Title;
        public string Status = "idle";  // idle|running|completed|failed|cancelled|waiting
        public string Phase = "idle";
        public string PhaseLabel = "Idle";
        public string OpText;
        public string Detail;
        public string Todo;
        public int StepsDone;
        public int StepsTotal;
        public double? Percent;
        public long StartedAt;
        public long EndedAt;
        public string Error;
        public string Waiting;

        public static SessionView FromJson(IDictionary<string, object> map)
        {
            var view = new SessionView();
            if (map == null) return view;
            view.Id = Str(map, "id", "");
            view.Title = Str(map, "title", null);
            view.Status = Str(map, "status", "idle");
            view.Phase = Str(map, "phase", "idle");
            view.PhaseLabel = Str(map, "phaseLabel", null);
            view.OpText = Str(map, "opText", null);
            view.Detail = Str(map, "detail", null);
            view.Todo = Str(map, "todo", null);
            view.StepsDone = Int(map, "stepsDone", 0);
            view.StepsTotal = Int(map, "stepsTotal", 0);
            object percent;
            if (map.TryGetValue("percent", out percent))
            {
                double parsed;
                if (double.TryParse(Convert.ToString(percent, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    view.Percent = parsed;
            }
            view.StartedAt = Long(map, "startedAt", 0);
            view.EndedAt = Long(map, "endedAt", 0);
            view.Error = Str(map, "error", null);
            view.Waiting = Str(map, "waiting", null);
            return view;
        }

        private static string Str(IDictionary<string, object> map, string key, string fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            string text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private static int Int(IDictionary<string, object> map, string key, int fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static long Long(IDictionary<string, object> map, string key, long fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }

    /// <summary>浮窗可点击的确认项（审批 / 提问）。</summary>
    internal sealed class PromptView
    {
        public string Id = "";
        public string Type = "approval";   // approval | question
        public string Title = "";
        public string ToolName;
        public string Detail;
        public string Reason;
        public List<PromptOption> Options = new List<PromptOption>();

        public static PromptView FromJson(IDictionary<string, object> map)
        {
            if (map == null) return null;
            var prompt = new PromptView();
            prompt.Id = Read(map, "id") ?? "";
            if (prompt.Id.Length == 0) return null;
            prompt.Type = Read(map, "type") ?? "approval";
            prompt.Title = Read(map, "title") ?? "需要确认";
            prompt.ToolName = Read(map, "toolName");
            prompt.Detail = Read(map, "detail");
            prompt.Reason = Read(map, "reason");
            object options;
            if (map.TryGetValue("options", out options) && options is List<object>)
            {
                foreach (var item in (List<object>)options)
                {
                    var optionMap = item as Dictionary<string, object>;
                    if (optionMap == null) continue;
                    var option = new PromptOption();
                    option.Id = Read(optionMap, "id") ?? "";
                    option.Label = Read(optionMap, "label") ?? option.Id;
                    if (option.Id.Length > 0) prompt.Options.Add(option);
                }
            }
            return prompt;
        }

        private static string Read(IDictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return null;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    internal sealed class PromptOption
    {
        public string Id = "";
        public string Label = "";
    }

    internal sealed class OverlayState
    {
        public long Now;
        public int RunningCount;
        public bool Idle = true;
        public SessionView Primary;
        public List<SessionView> Running = new List<SessionView>();
        public PromptView Prompt;

        public static OverlayState FromJson(Dictionary<string, object> map)
        {
            var state = new OverlayState();
            if (map == null) return state;
            state.Now = Long(map, "now", 0);
            state.RunningCount = Int(map, "runningCount", 0);
            state.Idle = Bool(map, "idle", true);
            object primary;
            if (map.TryGetValue("primary", out primary) && primary is Dictionary<string, object>)
                state.Primary = SessionView.FromJson((Dictionary<string, object>)primary);
            object prompt;
            if (map.TryGetValue("prompt", out prompt) && prompt is Dictionary<string, object>)
                state.Prompt = PromptView.FromJson((Dictionary<string, object>)prompt);
            object running;
            if (map.TryGetValue("running", out running) && running is List<object>)
            {
                foreach (var item in (List<object>)running)
                {
                    var viewMap = item as Dictionary<string, object>;
                    if (viewMap != null) state.Running.Add(SessionView.FromJson(viewMap));
                }
            }
            return state;
        }

        private static bool Bool(IDictionary<string, object> map, string key, bool fallback)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }

        private static long Long(IDictionary<string, object> map, string key, long fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static int Int(IDictionary<string, object> map, string key, int fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }

    // =====================================================================
    // Texts — 文案 / 颜色 / 格式化
    // =====================================================================
    internal static class Texts
    {
        public static string Phase(string phase)
        {
            switch (phase)
            {
                case "starting": return "\u5F00\u59CB\u6267\u884C";        // 开始执行
                case "thinking": return "\u601D\u8003\u4E2D";              // 思考中
                case "planning": return "\u89C4\u5212\u4E2D";              // 规划中
                case "reading": return "\u8BFB\u53D6\u6587\u4EF6";         // 读取文件
                case "searching": return "\u641C\u7D22\u4E2D";             // 搜索中
                case "editing": return "\u4FEE\u6539\u4E2D";               // 修改中
                case "writing": return "\u56DE\u590D\u751F\u6210\u4E2D";   // 回复生成中
                case "command": return "\u8FD0\u884C\u547D\u4EE4";         // 运行命令
                case "tool": return "\u8C03\u7528\u5DE5\u5177";            // 调用工具
                case "testing": return "\u8FD0\u884C\u6D4B\u8BD5";         // 运行测试
                case "building": return "\u6784\u5EFA\u4E2D";              // 构建中
                case "waiting": return "\u7B49\u5F85\u4E2D";               // 等待中
                case "completed": return "\u4EFB\u52A1\u5B8C\u6210";       // 任务完成
                case "failed": return "\u4EFB\u52A1\u5931\u8D25";          // 任务失败
                case "cancelled": return "\u4EFB\u52A1\u5DF2\u53D6\u6D88"; // 任务已取消
                default: return "\u7A7A\u95F2";                            // 空闲
            }
        }

        public static Color PhaseColor(string phase)
        {
            switch (phase)
            {
                case "reading":
                case "searching": return Color.FromRgb(0x7F, 0xB3, 0xFF);
                case "editing":
                case "writing": return Color.FromRgb(0xB3, 0x9D, 0xFF);
                case "command":
                case "testing":
                case "building": return Color.FromRgb(0xF2, 0xB6, 0x5C);
                case "waiting": return Color.FromRgb(0xE8, 0x8C, 0x5A);
                case "thinking": return Color.FromRgb(0x8A, 0xC6, 0xFF);
                case "completed": return Color.FromRgb(0x5F, 0xD1, 0x8F);
                case "failed": return Color.FromRgb(0xF0, 0x6A, 0x6A);
                case "cancelled": return Color.FromRgb(0xC9, 0xD1, 0xDE);
                default: return Color.FromRgb(0x9A, 0xA3, 0xB2);
            }
        }

        public static string FormatElapsed(long ms)
        {
            if (ms <= 0) return "00:00";
            var span = TimeSpan.FromMilliseconds(ms);
            return span.TotalHours >= 1
                ? string.Format("{0:00}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
                : string.Format("{0:00}:{1:00}", span.Minutes, span.Seconds);
        }

        public static string Clamp(string value, int max)
        {
            if (value == null) return null;
            return value.Length <= max ? value : value.Substring(0, max - 1) + "\u2026";
        }
    }

    // =====================================================================
    // OverlayWindow — 视觉与交互
    // =====================================================================
    internal sealed class OverlayWindow : Window
    {
        private readonly string _stateFile;
        private OverlayConfig _config = new OverlayConfig();
        private OverlayState _state;
        private long _clockOffset;
        private bool _hiddenByUser;
        private bool _fullscreenNow;
        private bool _fullscreenOverride;
        private bool _compact;
        private bool _shownOnce;

        private IntPtr _hwnd;
        private bool _clickThroughApplied;
        private long _lastElapsed = -1;

        // UI
        private Border _card;
        private Grid _expandedPanel;
        private Grid _compactPanel;
        private TextBlock _dot;
        private TextBlock _phaseText;
        private TextBlock _phaseEn;
        private TextBlock _taskText;
        private TextBlock _opText;
        private TextBlock _detailText;
        private TextBlock _stepsText;
        private TextBlock _elapsedText;
        private TextBlock _compactText;
        private Button _btnCompact;
        private StackPanel _opRow;
        private Grid _bottomRow;
        private StackPanel _taskList;
        private readonly List<Grid> _taskRows = new List<Grid>();
        private Border _flashBorder;
        private TextBlock _hintText;
        private StackPanel _promptPanel;
        private TextBlock _promptTitle;
        private TextBlock _promptText;
        private StackPanel _promptButtons;
        private string _baseUrl;
        private string _lastPromptKey;
        private string _lastFailedKey;
        private string _lastCompletedKey;
        private bool _firstStateSeen;
        private const int HotkeyId = 0x0D51;
        private string _hotkeyLabel;

        // 拖动
        private bool _dragging;
        private int _dragStartX, _dragStartY;
        private int _dragWinX, _dragWinY;

        // 位置 / 本地偏好（浮窗右键菜单直接可调，持久化到 --state 文件）
        private int? _savedX;
        private int? _savedY;
        private double? _localOpacity;
        private bool? _localClickThrough;
        private bool? _localAutoHide;

        private const double ExpandedBaseHeight = 118;
        private const double TaskRowHeight = 24;
        private const double CompactHeight = 36;
        private const double ExpandedWidth = 316;

        private static readonly SolidColorBrush TxtPrimary = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF6));
        private static readonly SolidColorBrush TxtDim = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB4));
        private static readonly SolidColorBrush TxtCode = new SolidColorBrush(Color.FromRgb(0xA9, 0xC4, 0xF2));

        public OverlayWindow(string stateFile, string baseUrl)
        {
            _stateFile = stateFile;
            _baseUrl = baseUrl;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            SizeToContent = SizeToContent.Manual;
            BuildUi();
            ContextMenu = BuildMenu();
            SourceInitialized += OnSourceInitialized;
            Closed += (s, e) =>
            {
                StopTimers();
                if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, HotkeyId);
            };
        }

        public IntPtr Hwnd { get { return _hwnd; } }

        // ------------------------------------------------------------- UI 结构
        private void BuildUi()
        {
            _card = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                Background = MakeBackground(0.94),
            };

            _expandedPanel = new Grid { Margin = new Thickness(14, 10, 12, 10) };
            _expandedPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            _expandedPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            _expandedPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            _expandedPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 头部
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dot = new TextBlock
            {
                Text = "\u25CF",
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 2, 0, 0),
            };
            Grid.SetColumn(_dot, 0);
            header.Children.Add(_dot);

            var brand = new TextBlock
            {
                Text = "DSH",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
            };
            Grid.SetColumn(brand, 1);
            header.Children.Add(brand);

            _phaseText = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(_phaseText, 3);
            header.Children.Add(_phaseText);

            _phaseEn = new TextBlock
            {
                FontSize = 10.5,
                Foreground = TxtDim,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(_phaseEn, 5);
            header.Children.Add(_phaseEn);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _btnCompact = MakeGlyphButton("\u2013", "\u6298\u53E0");   // 折叠
            _btnCompact.Click += (s, e) => SetCompact(true);
            buttons.Children.Add(_btnCompact);
            var btnHide = MakeGlyphButton("\u2715", "\u9690\u85CF");    // 隐藏
            btnHide.Click += (s, e) => HideByUser();
            buttons.Children.Add(btnHide);
            Grid.SetColumn(buttons, 7);
            header.Children.Add(buttons);
            Grid.SetRow(header, 0);
            _expandedPanel.Children.Add(header);

            _taskText = new TextBlock
            {
                FontSize = 12,
                Foreground = TxtPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(_taskText, 1);
            _expandedPanel.Children.Add(_taskText);

            _opRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _opText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = TxtCode,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _detailText = new TextBlock
            {
                FontSize = 10.5,
                Foreground = TxtDim,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150,
            };
            _opRow.Children.Add(_opText);
            _opRow.Children.Add(_detailText);
            Grid.SetRow(_opRow, 2);
            _expandedPanel.Children.Add(_opRow);

            _bottomRow = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            _bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bottomLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _stepsText = new TextBlock { FontSize = 10.5, Foreground = TxtDim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            bottomLeft.Children.Add(_stepsText);
            // 穿透模式提示：穿透时窗口收不到鼠标，靠全局热键 Ctrl+Alt+O 恢复
            _hintText = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xC4, 0xFF)),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Text = "\u7A7F\u900F\u4E2D \u00B7 \u9876\u90E8\u53EF\u70B9", // 穿透中 · 顶部可点
            };
            bottomLeft.Children.Add(_hintText);
            Grid.SetColumn(bottomLeft, 0);
            _bottomRow.Children.Add(bottomLeft);
            _elapsedText = new TextBlock { FontSize = 10.5, Foreground = TxtDim, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_elapsedText, 1);
            _bottomRow.Children.Add(_elapsedText);
            Grid.SetRow(_bottomRow, 3);
            _expandedPanel.Children.Add(_bottomRow);

            // 多任务并行列表：占用「任务/操作/底部」三行，逐条显示每个运行中任务的进度
            _taskList = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetRow(_taskList, 1);
            Grid.SetRowSpan(_taskList, 3);
            _expandedPanel.Children.Add(_taskList);

            // 待确认项面板：审批/提问时替代任务区，带可点击按钮
            _promptPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 0),
            };
            _promptTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xB6, 0x5C)), TextTrimming = TextTrimming.CharacterEllipsis };
            _promptText = new TextBlock { FontSize = 11, Foreground = TxtPrimary, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, Margin = new Thickness(0, 2, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
            _promptButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _promptPanel.Children.Add(_promptTitle);
            _promptPanel.Children.Add(_promptText);
            _promptPanel.Children.Add(_promptButtons);
            Grid.SetRow(_promptPanel, 1);
            Grid.SetRowSpan(_promptPanel, 3);
            _expandedPanel.Children.Add(_promptPanel);

            // 折叠面板：单行（双击或按钮展开）
            _compactPanel = new Grid { Margin = new Thickness(14, 0, 6, 0) };
            _compactPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _compactPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var compactDot = new TextBlock { Text = "\u25CF", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            Grid.SetColumn(compactDot, 0);
            _compactPanel.Children.Add(compactDot);
            _compactText = new TextBlock
            {
                FontSize = 12,
                Foreground = TxtPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(_compactText, 1);
            _compactPanel.Children.Add(_compactText);
            _compactPanel.Visibility = Visibility.Collapsed;   // 展开态下必须隐藏，否则两层面板重叠
            _expandedPanel.Visibility = Visibility.Visible;

            var content = new Grid { Margin = new Thickness(1) };
            content.Children.Add(_expandedPanel);
            content.Children.Add(_compactPanel);
            // 完成闪烁层：浅蓝描边由外向内收拢再散开（IsHitTestVisible=false 不挡交互）
            _flashBorder = new Border
            {
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xC4, 0xFF)),
                Opacity = 0,
                IsHitTestVisible = false,
                Margin = new Thickness(2),
            };
            content.Children.Add(_flashBorder);
            _card.Child = content;
            Content = _card;

            _compactPanel.MouseLeftButtonDown += (s, e) => SetCompact(false);

            MouseLeftButtonDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseLeftButtonUp += OnDragEnd;

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _elapsedTimer.Tick += (s, e) => OnTick();
        }

        private DispatcherTimer _elapsedTimer;
        private DispatcherTimer _fullscreenTimer;

        private static Brush MakeBackground(double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Min(1.0, Math.Max(0.3, opacity)) * 255);
            return new SolidColorBrush(Color.FromArgb(alpha, 0x0F, 0x14, 0x1D));
        }

        private Button MakeGlyphButton(string glyph, string tip)
        {
            return new Button
            {
                Content = new TextBlock { Text = glyph, FontSize = 11, Foreground = TxtDim },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                Width = 22,
                Height = 18,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = tip,
            };
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "\u6253\u5F00 DSH \u4E3B\u7A97\u53E3" };        // 打开 DSH 主窗口
            open.Click += (s, e) => BringDshToFront();
            var expand = new MenuItem { Header = "\u5C55\u5F00 / \u6298\u53E0" };             // 展开 / 折叠
            expand.Click += (s, e) => SetCompact(!_compact);
            var hide = new MenuItem { Header = "\u9690\u85CF\u6D6E\u7A97\uFF08\u4EFB\u52A1\u7EE7\u7EED\u8FD0\u884C\uFF09" }; // 隐藏浮窗（任务继续运行）
            hide.Click += (s, e) => HideByUser();

            // 透明度子菜单（本地即时生效并持久化）
            var opacityMenu = new MenuItem { Header = "\u900F\u660E\u5EA6" }; // 透明度
            foreach (double value in new[] { 0.60, 0.75, 0.85, 0.92, 1.00 })
            {
                double captured = value;
                var item = new MenuItem
                {
                    Header = string.Format("{0}%", (int)Math.Round(captured * 100)),
                    IsCheckable = true,
                    IsChecked = Math.Abs((_localOpacity ?? _config.Opacity) - captured) < 0.005,
                };
                item.Click += (s, e) =>
                {
                    _localOpacity = captured;
                    foreach (var sibling in opacityMenu.Items)
                    {
                        var menuItem = sibling as MenuItem;
                        if (menuItem != null) menuItem.IsChecked = false;
                    }
                    item.IsChecked = true;
                    ApplyLocalPreferences();
                };
                opacityMenu.Items.Add(item);
            }

            var clickThrough = new MenuItem
            {
                Header = "\u9F20\u6807\u7A7F\u900F\uFF08Ctrl+Alt+O \u5207\u6362\uFF09", // 鼠标穿透（Ctrl+Alt+O 切换）
                IsCheckable = true,
                IsChecked = _localClickThrough ?? _config.ClickThrough,
            };
            clickThrough.Click += (s, e) =>
            {
                _localClickThrough = clickThrough.IsChecked;
                ApplyLocalPreferences();
            };

            var autoHide = new MenuItem
            {
                Header = "\u5B8C\u6210\u540E\u81EA\u52A8\u9690\u85CF", // 完成后自动隐藏
                IsCheckable = true,
                IsChecked = _localAutoHide ?? _config.AutoHideOnComplete,
            };
            autoHide.Click += (s, e) =>
            {
                _localAutoHide = autoHide.IsChecked;
                ApplyLocalPreferences();
            };

            menu.Items.Add(open);
            menu.Items.Add(expand);
            menu.Items.Add(opacityMenu);
            menu.Items.Add(clickThrough);
            menu.Items.Add(autoHide);
            menu.Items.Add(hide);
            return menu;
        }

        // ------------------------------------------------------------- 状态接入
        public void ApplyConfig(OverlayConfig config)
        {
            _config = config;
            // 本地偏好（右键菜单）覆盖服务器配置：用户当场调过的值优先
            if (_localOpacity.HasValue) _config.Opacity = _localOpacity.Value;
            if (_localClickThrough.HasValue) _config.ClickThrough = _localClickThrough.Value;
            if (_localAutoHide.HasValue) _config.AutoHideOnComplete = _localAutoHide.Value;
            _card.Background = MakeBackground(_config.Opacity);
            SetClickThrough(_config.ClickThrough);
            _elapsedTimer.IsEnabled = _config.ShowElapsedTime;
            _fullscreenOverride = !_config.HideOnFullscreen;
            if (_config.CompactMode && _shownOnce) SetCompact(true);
            if (!_fullscreenOverride && _fullscreenNow && IsVisible) FadeOut();
            UpdateClickThroughHint();
            PositionByModeIfNeeded();
        }

        /// <summary>应用本地偏好（右键菜单动作）并落盘。</summary>
        private void ApplyLocalPreferences()
        {
            if (_localOpacity.HasValue) _config.Opacity = _localOpacity.Value;
            if (_localClickThrough.HasValue) _config.ClickThrough = _localClickThrough.Value;
            if (_localAutoHide.HasValue) _config.AutoHideOnComplete = _localAutoHide.Value;
            _card.Background = MakeBackground(_config.Opacity);
            SetClickThrough(_config.ClickThrough);
            UpdateClickThroughHint();
            SavePosition();
        }

        /// <summary>穿透模式下显示"Ctrl+Alt+O 恢复"提示（穿透时窗口收不到鼠标）。</summary>
        private void UpdateClickThroughHint()
        {
            if (_hintText != null)
            {
                _hintText.Text = _hotkeyLabel == null
                    ? "\u7A7F\u900F\u4E2D \u00B7 \u9876\u90E8\u53EF\u70B9"                       // 穿透中 · 顶部可点
                    : "\u7A7F\u900F\u4E2D \u00B7 \u9876\u90E8\u53EF\u70B9 \u00B7 " + _hotkeyLabel; // … · Ctrl+Alt+O
                _hintText.Visibility = _config.ClickThrough ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void ApplyState(OverlayState state)
        {
            _state = state;
            if (state.Now > 0)
                _clockOffset = state.Now - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            DetectCompletion(state);
            Render();
            ReconcileVisibility();
        }

        // ------------------------------------------------------------- 渲染
        private void Render()
        {
            var view = _state != null ? _state.Primary : null;
            Color accent = Texts.PhaseColor(view != null ? view.Phase : "idle");
            _dot.Foreground = new SolidColorBrush(accent);
            if (view == null && _state.Prompt == null)
            {
                _phaseText.Text = Texts.Phase("idle");
                _phaseText.Foreground = TxtDim;
                _phaseEn.Text = "Idle";
                _taskText.Text = "";
                _opText.Text = "";
                _detailText.Text = "";
                _stepsText.Text = "";
                _elapsedText.Text = "";
                _compactText.Text = "DSH \u00B7 " + Texts.Phase("idle");
                _compactText.Foreground = TxtDim;
                _taskList.Visibility = Visibility.Collapsed;
                _taskText.Visibility = Visibility.Collapsed;
                _opRow.Visibility = Visibility.Visible;
                _bottomRow.Visibility = Visibility.Visible;
                return;
            }

            // ---- 待确认项优先：审批/提问时替代任务区 ----
            if (_state.Prompt != null)
            {
                _promptPanel.Visibility = Visibility.Visible;
                _taskList.Visibility = Visibility.Collapsed;
                _taskText.Visibility = Visibility.Collapsed;
                _opRow.Visibility = Visibility.Collapsed;
                _bottomRow.Visibility = Visibility.Collapsed;
                RenderPrompt(_state.Prompt);
                var warn = new SolidColorBrush(Color.FromRgb(0xF2, 0xB6, 0x5C));
                _phaseText.Text = _state.Prompt.Title;
                _phaseText.Foreground = warn;
                _phaseEn.Text = _state.Prompt.Type == "question" ? "Question" : "Approval";
                _dot.Foreground = warn;
                _compactText.Text = "DSH \u00B7 " + _state.Prompt.Title;
                _compactText.Foreground = warn;
                ResizeForContent();
                TickElapsed(true);
                return;
            }
            _promptPanel.Visibility = Visibility.Collapsed;

            // ---- 多任务并行：逐条列出每个运行中任务的进度 ----
            bool multi = _state.RunningCount > 1 && _state.Running.Count > 0;
            if (multi)
            {
                _phaseText.Text = string.Format("{0} \u4E2A\u4EFB\u52A1\u8FD0\u884C\u4E2D", _state.RunningCount); // N 个任务运行中
                _phaseText.Foreground = TxtPrimary;
                _phaseEn.Text = string.Format("{0} tasks", _state.RunningCount);
                _dot.Foreground = new SolidColorBrush(Texts.PhaseColor("thinking"));
                _taskList.Visibility = Visibility.Visible;
                _taskText.Visibility = Visibility.Collapsed;
                _opRow.Visibility = Visibility.Collapsed;
                _bottomRow.Visibility = Visibility.Collapsed;
                RenderTaskRows();
                ResizeForContent();
                // 折叠文案照旧在下面统一处理
            }
            else
            {
                _taskList.Visibility = Visibility.Collapsed;
                _taskText.Visibility = Visibility.Visible;
                _opRow.Visibility = Visibility.Visible;
                _bottomRow.Visibility = Visibility.Visible;
                ResizeForContent();
            }

            string phaseZh = Texts.Phase(view.Phase);
            if (!multi)
            {
                _phaseText.Text = phaseZh;
                _phaseText.Foreground = new SolidColorBrush(accent);
                _phaseEn.Text = string.IsNullOrEmpty(view.PhaseLabel) ? "" : view.PhaseLabel;
                _dot.Foreground = new SolidColorBrush(accent);
            }

            // 任务行（todo 当前项或会话标题）
            string title = _config.ShowTitle ? (view.Todo ?? view.Title) : null;
            _taskText.Text = title != null ? Texts.Clamp(title, 44) : "";
            _taskText.Foreground = TxtPrimary;

            // 操作行（终态 / 错误摘要优先）
            string op = null;
            string detail = null;
            if (view.Status == "completed")
                op = "\u2713 " + (view.PhaseLabel ?? "Completed");          // ✓
            else if (view.Status == "failed")
                op = "\u2715 " + (view.Error != null ? Texts.Clamp(view.Error, 46) : "Failed");
            else if (view.Status == "cancelled")
                op = "\u2014 " + (view.PhaseLabel ?? "Cancelled");
            else if (!string.IsNullOrEmpty(view.Waiting))
                op = Texts.Clamp(view.Waiting, 40);
            else
            {
                op = Texts.Clamp(view.OpText, 50);
                detail = Texts.Clamp(view.Detail, 42);
                if (op == null && view.Phase == "thinking") op = "\u601D\u8003\u4E2D\u2026"; // 思考中…
            }
            _opText.Text = op ?? "";
            _opText.Visibility = op != null ? Visibility.Visible : Visibility.Collapsed;
            _detailText.Text = detail ?? "";
            _detailText.Visibility = detail != null ? Visibility.Visible : Visibility.Collapsed;

            // 步骤/百分比（仅当 percent 真实存在）
            bool showSteps = view.Percent.HasValue && _config.ShowPercentage;
            if (showSteps)
                _stepsText.Text = string.Format("{0} / {1} \u6B65\u9AA4 \u00B7 {2}%", view.StepsDone, view.StepsTotal, view.Percent.Value);
            else if (view.StepsTotal > 0)
                _stepsText.Text = string.Format("{0} / {1} \u6B65\u9AA4", view.StepsDone, view.StepsTotal);
            else
                _stepsText.Text = "";

            // 折叠文案
            string compact;
            if (view.Status == "completed")
                compact = "DSH \u00B7 \u2713 " + Texts.FormatElapsed(ElapsedFor(view));
            else if (view.Status == "failed")
                compact = "DSH \u00B7 \u2715 \u5931\u8D25";
            else
                compact = "DSH \u00B7 " + phaseZh + " \u00B7 " + Texts.FormatElapsed(ElapsedFor(view));
            if (_state.RunningCount > 1)
                compact += string.Format(" (+{0})", _state.RunningCount - 1);
            _compactText.Text = compact;
            _compactText.Foreground = TxtPrimary;

            TickElapsed(true);
        }

        // ------------------------------------------------------------- 多任务并行列表
        /// <summary>展开态高度：单任务固定，多任务按行数增长（最多 5 行）。</summary>
        private double DesiredExpandedHeight()
        {
            if (_state != null && _state.Prompt != null)
                return _state.Prompt.Options.Count > 0 ? 152 : 118;
            bool multi = _state != null && _state.RunningCount > 1 && _state.Running.Count > 0;
            if (!multi) return ExpandedBaseHeight;
            int rows = Math.Min(_state.Running.Count, 5);
            return 56 + rows * TaskRowHeight + 10;
        }

        private double _lastAppliedHeight = -1;

        /// <summary>内容变化导致高度变化时才真正调整窗口（避免每帧 SetWindowPos）。</summary>
        private void ResizeForContent()
        {
            if (_hwnd == IntPtr.Zero) return;
            double target = _compact ? CompactHeight : DesiredExpandedHeight();
            if (Math.Abs(_lastAppliedHeight - target) < 0.5) return;
            PositionByModeIfNeeded();
        }

        private static string ShortPhase(string phase)
        {
            switch (phase)
            {
                case "starting": return "\u542F\u52A8";    // 启动
                case "thinking": return "\u601D\u8003";    // 思考
                case "planning": return "\u89C4\u5212";    // 规划
                case "reading": return "\u8BFB\u53D6";     // 读取
                case "searching": return "\u641C\u7D22";   // 搜索
                case "editing": return "\u4FEE\u6539";     // 修改
                case "writing": return "\u751F\u6210";     // 生成
                case "command": return "\u547D\u4EE4";     // 命令
                case "tool": return "\u5DE5\u5177";        // 工具
                case "testing": return "\u6D4B\u8BD5";     // 测试
                case "building": return "\u6784\u5EFA";    // 构建
                case "waiting": return "\u7B49\u5F85";     // 等待
                case "completed": return "\u5B8C\u6210";   // 完成
                case "failed": return "\u5931\u8D25";      // 失败
                case "cancelled": return "\u53D6\u6D88";   // 取消
                default: return "\u7A7A\u95F2";            // 空闲
            }
        }

        private Grid BuildTaskRow()
        {
            var row = new Grid { Height = TaskRowHeight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

            var dot = new TextBlock { Text = "\u25CF", FontSize = 7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var phase = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(phase, 1);
            row.Children.Add(phase);

            var title = new TextBlock { FontSize = 11, Foreground = TxtPrimary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(title, 2);
            row.Children.Add(title);

            var steps = new TextBlock { FontSize = 10, Foreground = TxtDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(steps, 3);
            row.Children.Add(steps);

            var elapsed = new TextBlock { FontSize = 10, Foreground = TxtDim, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(elapsed, 4);
            row.Children.Add(elapsed);
            return row;
        }

        private void RenderTaskRows()
        {
            // 兼容旧版宿主：老核心只推送"其他"运行任务（不含 primary），
            // 这里把 primary 补进列表，保证任何版本下都能看到全部任务。
            var views = new List<SessionView>();
            if (_state.Primary != null && _state.Primary.Status == "running")
            {
                bool primaryListed = false;
                foreach (var item in _state.Running)
                {
                    if (item.Id == _state.Primary.Id) { primaryListed = true; break; }
                }
                if (!primaryListed) views.Add(_state.Primary);
            }
            views.AddRange(_state.Running);
            int needed = Math.Min(views.Count, 5);
            while (_taskRows.Count < needed)
            {
                var row = BuildTaskRow();
                _taskRows.Add(row);
                _taskList.Children.Add(row);
            }
            for (int i = 0; i < _taskRows.Count; i++)
            {
                var row = _taskRows[i];
                if (i >= needed) { row.Visibility = Visibility.Collapsed; continue; }
                row.Visibility = Visibility.Visible;
                var view = views[i];
                bool isPrimary = _state.Primary != null && view.Id == _state.Primary.Id;
                var accent = new SolidColorBrush(Texts.PhaseColor(view.Phase));
                ((TextBlock)row.Children[0]).Foreground = accent;
                var phase = (TextBlock)row.Children[1];
                phase.Text = ShortPhase(view.Phase);
                phase.Foreground = accent;
                var title = (TextBlock)row.Children[2];
                string label = view.Todo ?? view.Title ?? view.OpText ?? view.Id;
                title.Text = Texts.Clamp(label, 30);
                title.Foreground = isPrimary ? TxtPrimary : TxtDim;
                title.FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal;
                var steps = (TextBlock)row.Children[3];
                steps.Text = view.StepsTotal > 0 ? string.Format("{0}/{1}", view.StepsDone, view.StepsTotal) : "";
                var elapsed = (TextBlock)row.Children[4];
                elapsed.Text = _config.ShowElapsedTime ? Texts.FormatElapsed(ElapsedFor(view)) : "";
            }
        }

        private long ElapsedFor(SessionView view)
        {
            if (view == null || view.StartedAt <= 0) return 0;
            long end = view.EndedAt > 0 ? view.EndedAt : NowServer();
            return Math.Max(0, end - view.StartedAt);
        }

        private long NowServer()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _clockOffset;
        }

        /// <summary>500ms 心跳：刷新耗时 + 驱动显隐判定（完成后的自动隐藏在无新帧时也能生效）。</summary>
        private void OnTick()
        {
            TickElapsed(false);
            ReconcileVisibility();
        }

        /// <summary>
        /// 状态跃迁时触发一次描边内闪：
        ///   完成 → 浅蓝；失败 → 红；新出现的待确认项 → 橙。
        /// 每种跃迁只闪一次（用 key 去重）。
        /// </summary>
        private void DetectCompletion(OverlayState state)
        {
            var view = state != null ? state.Primary : null;
            string key = view == null ? null : view.Id + "|" + view.Status + "|" + view.EndedAt;
            string promptKey = state != null && state.Prompt != null ? state.Prompt.Id : null;
            if (!_firstStateSeen)
            {
                _firstStateSeen = true;
                _lastCompletedKey = key;
                _lastFailedKey = key;
                _lastPromptKey = promptKey;
                return;
            }
            if (promptKey != null && promptKey != _lastPromptKey)
            {
                _lastPromptKey = promptKey;
                ShowOverlayForAttention();
                PlayFlash(Color.FromRgb(0xF2, 0xB6, 0x5C));   // 橙：需要确认
            }
            else if (promptKey == null)
            {
                _lastPromptKey = null;
            }
            if (view != null && view.Status == "completed" && key != _lastCompletedKey)
            {
                _lastCompletedKey = key;
                if (IsVisible) PlayFlash(Color.FromRgb(0x7F, 0xC4, 0xFF));  // 浅蓝
            }
            if (view != null && view.Status == "failed" && key != _lastFailedKey)
            {
                _lastFailedKey = key;
                ShowOverlayForAttention();
                PlayFlash(Color.FromRgb(0xF0, 0x5A, 0x5A));                 // 红：出错
            }
        }

        /// <summary>需要用户注意（确认/失败）时把浮窗显示出来。</summary>
        private void ShowOverlayForAttention()
        {
            if (_hiddenByUser) _hiddenByUser = false;
            if (!IsVisible)
            {
                FadeIn();
                _shownOnce = true;
            }
        }

        /// <summary>渲染确认项面板（标题 + 说明 + 可点击按钮）。</summary>
        private void RenderPrompt(PromptView prompt)
        {
            _promptTitle.Text = prompt.Type == "question" ? "\u9700\u8981\u56DE\u7B54" : "\u9700\u8981\u786E\u8BA4"; // 需要回答 / 需要确认
            string body = prompt.Detail ?? prompt.Reason ?? prompt.ToolName ?? "";
            if (prompt.ToolName != null && prompt.Detail != null && prompt.Detail != prompt.ToolName)
                body = prompt.ToolName + " \u00B7 " + prompt.Detail;
            _promptText.Text = Texts.Clamp(body, 110);

            _promptButtons.Children.Clear();
            foreach (var option in prompt.Options)
            {
                var button = new Button
                {
                    Content = new TextBlock { Text = option.Label, FontSize = 11, Foreground = TxtPrimary },
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF2, 0xB6, 0x5C)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xB6, 0x5C)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                };
                string optionId = option.Id;
                button.Click += (sender, args) => SendDecision(optionId);
                _promptButtons.Children.Add(button);
            }
        }

        /// <summary>把用户在浮窗上的选择 POST 回宿主（后台线程，不阻塞 UI）。</summary>
        private void SendDecision(string optionId)
        {
            if (_state == null || _state.Prompt == null || string.IsNullOrEmpty(_baseUrl)) return;
            string url = _baseUrl + "/api/dsh-overlay/decision";
            string body = string.Format("{{\"id\":\"{0}\",\"optionId\":\"{1}\"}}", _state.Prompt.Id, optionId);
            var thread = new Thread(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Proxy = null;
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = 8000;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        LogLine("decision sent: " + optionId + " -> " + (int)response.StatusCode);
                    }
                }
                catch (Exception error)
                {
                    LogLine("decision failed: " + error.Message);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>完成：浅蓝；失败：红；需要确认：橙。</summary>
        private void PlayFlash(Color color)
        {
            if (_flashBorder == null) return;
            _flashBorder.BorderBrush = new SolidColorBrush(color);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var thickness = new ThicknessAnimation
            {
                From = new Thickness(0),
                To = new Thickness(14),
                Duration = TimeSpan.FromMilliseconds(360),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            var glow = new DoubleAnimation
            {
                From = 0.0,
                To = 0.95,
                Duration = TimeSpan.FromMilliseconds(360),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
                FillBehavior = FillBehavior.Stop,
            };
            _flashBorder.BeginAnimation(Border.BorderThicknessProperty, thickness);
            _flashBorder.BeginAnimation(OpacityProperty, glow);
        }

        /// <summary>切换鼠标穿透（全局热键 Ctrl+Alt+O 与右键菜单共用）。</summary>
        private void ToggleClickThrough()
        {
            bool next = !(_localClickThrough ?? _config.ClickThrough);
            _localClickThrough = next;
            ApplyLocalPreferences();
            ContextMenu = BuildMenu();   // 刷新勾选状态
        }

        private void TickElapsed(bool force)
        {
            if (_state == null) return;
            var view = _state.Primary;
            if (view == null) return;
            bool terminal = view.Status == "completed" || view.Status == "failed" || view.Status == "cancelled";
            long elapsed = ElapsedFor(view);
            if (_config.ShowElapsedTime && _elapsedText != null && (force || _lastElapsed != elapsed))
            {
                _lastElapsed = elapsed;
                _elapsedText.Text = Texts.FormatElapsed(elapsed);
            }
            // 运行中的折叠文案每秒跟随耗时；终态由 Render 定格
            if (!terminal && _compactText != null)
            {
                string phase = Texts.Phase(view.Phase);
                string compact = "DSH \u00B7 " + phase + " \u00B7 " + Texts.FormatElapsed(elapsed);
                if (_state.RunningCount > 1)
                    compact += string.Format(" (+{0})", _state.RunningCount - 1);
                if (_compactText.Text != compact) _compactText.Text = compact;
            }
        }

        // ------------------------------------------------------------- 显隐策略
        private void ReconcileVisibility()
        {
            if (_state == null || _config == null) return;
            bool running = _state.RunningCount > 0;
            bool show = running || ShouldKeepVisible();

            if (_hiddenByUser && running) show = true;      // 新任务开始时自动复现
            if (show && _fullscreenNow && !_fullscreenOverride) show = false;

            if (!show)
            {
                if (IsVisible) FadeOut();
                _elapsedTimer.IsEnabled = false;
                return;
            }
            if (!IsVisible)
            {
                _hiddenByUser = false;
                FadeIn();
                _shownOnce = true;
            }
            _elapsedTimer.IsEnabled = _config.ShowElapsedTime;
            TickElapsed(true);
        }

        /// <summary>
        /// 是否应保持可见。终态的停留时长由这里统一决定（避免与定时器重复计时
        /// 造成"隐藏→又显示"的抖动）：
        ///   running/waiting   → 始终可见
        ///   failed            → 始终可见（直到新任务或手动关闭）
        ///   completed/cancelled → autoHideOnComplete 时显示 autoHideDelayMs 后隐藏；
        ///                        关闭自动隐藏时保持可见
        /// </summary>
        private bool ShouldKeepVisible()
        {
            if (_state == null) return false;
            if (_state.Prompt != null) return true;   // 等待用户确认：始终可见
            if (_state.Primary == null) return false;
            var view = _state.Primary;
            if (view.Status == "running" || view.Status == "waiting") return true;
            if (view.Status == "failed") return true;
            if (view.Status == "completed" || view.Status == "cancelled")
            {
                if (!_config.AutoHideOnComplete) return true;
                if (view.EndedAt <= 0) return true;
                return NowServer() - view.EndedAt < _config.AutoHideDelayMs;
            }
            return false;
        }

        public void HideByUser()
        {
            _hiddenByUser = true;
            if (IsVisible) FadeOut();
        }

        private void FadeOut()
        {
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (s, e) =>
            {
                Opacity = 1.0;
                Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void FadeIn()
        {
            Opacity = 0.0;
            ShowNoActivate();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160)));
        }

        private void ShowNoActivate()
        {
            Show();
            if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            ApplyWindowStyles();
            PositionByModeIfNeeded();
        }

        // ------------------------------------------------------------- 折叠
        public void SetCompact(bool compact)
        {
            if (_compact == compact) return;
            _compact = compact;
            _expandedPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _compactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            _btnCompact.Content = new TextBlock { Text = compact ? "\u25A2" : "\u2013", FontSize = 11, Foreground = TxtDim };
            PositionByModeIfNeeded();
        }

        // ------------------------------------------------------------- Win32
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(_hwnd);
            if (source != null) source.AddHook(WndProc);
            ApplyWindowStyles();
            LoadPosition();
            PositionByModeIfNeeded();
            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _fullscreenTimer.Tick += (s, ev) => PollFullscreen();
            _fullscreenTimer.Start();
            // 全局热键（可选便利）：穿透模式下也能一键切回。组合可能被占用，
            // 逐个回退；实际结果写入 <state>.log 便于诊断。
            RegisterToggleHotkey();
        }

        /// <summary>依次尝试若干组合键注册全局热键（Ctrl+Alt+O 等可能被其他软件占用）。</summary>
        private void RegisterToggleHotkey()
        {
            uint[][] combos = new uint[][]
            {
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x4F },              // Ctrl+Alt+O
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT | 0x0004, 0x4F },     // Ctrl+Alt+Shift+O
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x50 },              // Ctrl+Alt+P
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x4B },              // Ctrl+Alt+K
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x78 },              // Ctrl+Alt+F9
            };
            string[] labels = new string[] { "Ctrl+Alt+O", "Ctrl+Alt+Shift+O", "Ctrl+Alt+P", "Ctrl+Alt+K", "Ctrl+Alt+F9" };
            for (int i = 0; i < combos.Length; i++)
            {
                if (Native.RegisterHotKey(_hwnd, HotkeyId, combos[i][0], combos[i][1]))
                {
                    _hotkeyLabel = labels[i];
                    LogLine("hotkey registered: " + labels[i]);
                    return;
                }
            }
            _hotkeyLabel = null;
            LogLine("hotkey registration failed for all combos (顶部细条仍可交互)");
        }

        /// <summary>轻量诊断日志：写到状态文件同目录的 .log（失败静默）。</summary>
        private void LogLine(string message)
        {
            try
            {
                string path = (_stateFile ?? "dsh-overlay") + ".log";
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x00A0) // WM_NCLBUTTONDOWN 安全网：永不因此激活
            {
                handled = true;
                return IntPtr.Zero;
            }
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                ToggleClickThrough();
                handled = true;
                return IntPtr.Zero;
            }
            if (msg == 0x0084 && _clickThroughApplied) // WM_NCHITTEST
            {
                int packed = lParam.ToInt32();
                int screenX = (short)(packed & 0xFFFF);
                int screenY = (short)((packed >> 16) & 0xFFFF);
                if (!IsInteractiveStrip(screenX, screenY))
                {
                    handled = true;
                    return new IntPtr(-1); // HTTRANSPARENT：点击落到下方窗口
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 窗口样式：始终置顶 + 不激活 + 不进 Alt-Tab。
        /// 鼠标穿透**不**用整窗 WS_EX_TRANSPARENT（那会导致窗口再也收不到点击，
        /// 无法从菜单里关掉）。改为 WM_NCHITTEST 选择性穿透：卡片主体透传，
        /// 顶部细条保持可交互（右键菜单/折叠按钮始终可用）。
        /// </summary>
        private void ApplyWindowStyles()
        {
            if (_hwnd == IntPtr.Zero) return;
            int style = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            int updated = (style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_TRANSPARENT;
            if (updated != style) Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, updated);
        }

        /// <summary>穿透模式下仍可交互的顶部细条（屏幕坐标判定）。</summary>
        private bool IsInteractiveStrip(int screenX, int screenY)
        {
            if (_hwnd == IntPtr.Zero) return false;
            var rect = Native.WindowRect(_hwnd);
            if (screenX < rect.Left || screenX > rect.Right || screenY < rect.Top || screenY > rect.Bottom) return false;
            // 有待确认项时整窗可点（按钮必须能按到）
            if (_state != null && _state.Prompt != null) return true;
            double dpi = Native.GetDpiForWindow(_hwnd) / 96.0;
            int strip = (int)Math.Round((_compact ? 60 : 30) * dpi);
            return screenY - rect.Top <= strip;
        }

        private void SetClickThrough(bool enabled)
        {
            _clickThroughApplied = enabled;
            ApplyWindowStyles();
        }

        // ------------------------------------------------------------- 拖动（物理像素）
        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) return;
            // 空白区域（含头部）按住拖动；按钮不触发
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button) return;
                source = VisualTreeHelper.GetParent(source);
            }
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;
            var rect = Native.WindowRect(_hwnd);
            _dragging = true;
            _dragStartX = cursor.X;
            _dragStartY = cursor.Y;
            _dragWinX = rect.Left;
            _dragWinY = rect.Top;
            try { CaptureMouse(); } catch { }
            e.Handled = true;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;
            Native.SetWindowPos(_hwnd, IntPtr.Zero,
                _dragWinX + cursor.X - _dragStartX,
                _dragWinY + cursor.Y - _dragStartY,
                0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            try { ReleaseMouseCapture(); } catch { }
            ClampIntoView();
            SavePosition();
            e.Handled = true;
        }

        private void ClampIntoView()
        {
            if (_hwnd == IntPtr.Zero) return;
            var rect = Native.WindowRect(_hwnd);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            var info = Native.MonitorInfoFromPoint(rect.Left + width / 2, rect.Top + height / 2);
            int x = rect.Left;
            int y = rect.Top;
            if (width <= info.rcWork.Right - info.rcWork.Left)
                x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - width));
            else x = info.rcWork.Left;
            if (height <= info.rcWork.Bottom - info.rcWork.Top)
                y = Math.Max(info.rcWork.Top, Math.Min(y, info.rcWork.Bottom - height));
            else y = info.rcWork.Top;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------- 位置 / 本地偏好持久化
        private void LoadPosition()
        {
            try
            {
                if (_stateFile == null || !File.Exists(_stateFile)) return;
                var map = Json.ToMap(File.ReadAllText(_stateFile));
                object value;
                if (map.TryGetValue("x", out value)) _savedX = ToInt(value);
                if (map.TryGetValue("y", out value)) _savedY = ToInt(value);
                if (map.TryGetValue("opacity", out value))
                {
                    double parsed;
                    if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        _localOpacity = Math.Min(1.0, Math.Max(0.5, parsed));
                }
                if (map.TryGetValue("clickThrough", out value) && value is bool) _localClickThrough = (bool)value;
                if (map.TryGetValue("autoHideOnComplete", out value) && value is bool) _localAutoHide = (bool)value;
            }
            catch { /* 状态文件损坏 → 使用默认 */ }
        }

        private static int? ToInt(object value)
        {
            if (value == null) return null;
            long parsed;
            if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)) return (int)parsed;
            double asDouble;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble)) return (int)asDouble;
            return null;
        }

        private void SavePosition()
        {
            if (_stateFile == null || _hwnd == IntPtr.Zero) return;
            try
            {
                var rect = Native.WindowRect(_hwnd);
                File.WriteAllText(_stateFile, string.Format(
                    "{{\"x\":{0},\"y\":{1},\"opacity\":{2},\"clickThrough\":{3},\"autoHideOnComplete\":{4}}}",
                    rect.Left, rect.Top,
                    (_localOpacity ?? _config.Opacity).ToString("0.##", CultureInfo.InvariantCulture),
                    (_localClickThrough ?? _config.ClickThrough) ? "true" : "false",
                    (_localAutoHide ?? _config.AutoHideOnComplete) ? "true" : "false"));
            }
            catch { }
        }

        private static bool IsOnAnyMonitor(int x, int y, int w, int h)
        {
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                if (x + w > b.Left - 8 && x < b.Right + 8 && y + h > b.Top - 8 && y < b.Bottom + 8)
                    return true;
            }
            return false;
        }

        private void PositionByModeIfNeeded()
        {
            if (_hwnd == IntPtr.Zero) return;
            double dipWidth = _compact ? CompactWidth() : DesiredExpandedWidth();
            double dipHeight = _compact ? CompactHeight : DesiredExpandedHeight();
            _lastAppliedHeight = dipHeight;
            double dpi = Native.GetDpiForWindow(_hwnd) / 96.0;
            int width = (int)Math.Round(dipWidth * dpi);
            int height = (int)Math.Round(dipHeight * dpi);
            bool hasSaved = _savedX.HasValue && _savedY.HasValue
                && IsOnAnyMonitor(_savedX.Value, _savedY.Value, width, height);

            var current = Native.WindowRect(_hwnd);
            if (_config.Position != "remember" || !hasSaved)
            {
                // 指定角落（或记忆位置无效）→ 主显示器工作区角落，距边 20px
                var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Right - width - 20;
                int y = work.Top + 20;
                if (_config.Position == "top-left") { x = work.Left + 20; y = work.Top + 20; }
                else if (_config.Position == "bottom-right") { x = work.Right - width - 20; y = work.Bottom - height - 20; }
                else if (_config.Position == "bottom-left") { x = work.Left + 20; y = work.Bottom - height - 20; }
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
            }
            else
            {
                // 记住的位置：宽度变化时优先保持靠屏幕右/下侧的边缘不动，避免"越变越出屏"
                var info = Native.MonitorInfoFromPoint(_savedX.Value + (current.Right - current.Left) / 2, _savedY.Value + 20);
                int x = _savedX.Value;
                int y = _savedY.Value;
                if (current.Right - current.Left != width && info.rcWork.Right - current.Right < 80)
                    x = current.Right - width;
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
                _savedX = x;
                _savedY = y;
            }
            Width = dipWidth;
            Height = dipHeight;
        }

        private double CompactWidth()
        {
            string text = _compactText.Text ?? "DSH \u00B7 \u7A7A\u95F2";
            return Math.Min(360, Math.Max(150, MeasureText(text, 12, FontWeights.Normal) + 46));
        }

        /// <summary>用与渲染一致的字体测量一段文本的宽度（DIP）。</summary>
        private double MeasureText(string text, double fontSize, FontWeight weight)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            try
            {
                var formatted = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
                    fontSize,
                    TxtPrimary,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                return formatted.Width;
            }
            catch
            {
                return text.Length * fontSize * 0.62;   // 兜底估算
            }
        }

        /// <summary>
        /// 展开态宽度：按当前内容自适应（单任务看操作行、多任务看最长一条、
        /// 确认项看问题文本），夹在 [300, 460] DIP 之间。
        /// </summary>
        private double DesiredExpandedWidth()
        {
            double natural = 300;
            if (_state != null && _state.Prompt != null)
            {
                natural = 60 + MeasureText(_state.Prompt.Title, 12, FontWeights.SemiBold)
                    + Math.Min(300, MeasureText(_state.Prompt.Detail ?? _state.Prompt.Reason ?? "", 11, FontWeights.Normal));
                if (_state.Prompt.Options.Count > 0) natural += 20;
            }
            else if (_state != null && _state.RunningCount > 1 && _state.Running.Count > 0)
            {
                // 表头：● + DSH + 阶段 + 英文 + 按钮
                double header = 14 + MeasureText("DSH", 13, FontWeights.SemiBold) + 10
                    + MeasureText(string.Format("{0} \u4E2A\u4EFB\u52A1\u8FD0\u884C\u4E2D", _state.RunningCount), 13, FontWeights.SemiBold) + 8
                    + MeasureText(string.Format("{0} tasks", _state.RunningCount), 10.5, FontWeights.Normal) + 60;
                natural = header;
                foreach (var view in _state.Running)
                {
                    string label = view.Todo ?? view.Title ?? view.OpText ?? view.Id;
                    double row = 14 + 46 + MeasureText(Texts.Clamp(label, 30), 11, FontWeights.Normal)
                        + 8 + MeasureText(view.StepsTotal > 0 ? string.Format("{0}/{1}", view.StepsDone, view.StepsTotal) : "", 10, FontWeights.Normal)
                        + 8 + MeasureText("00:00", 10, FontWeights.Normal) + 20;
                    if (row > natural) natural = row;
                }
            }
            else if (_state != null && _state.Primary != null)
            {
                var view = _state.Primary;
                double header = 14 + MeasureText("DSH", 13, FontWeights.SemiBold) + 10
                    + MeasureText(Texts.Phase(view.Phase), 13, FontWeights.SemiBold) + 8
                    + MeasureText(view.PhaseLabel ?? "", 10.5, FontWeights.Normal) + 60;
                double task = 28 + MeasureText(Texts.Clamp(view.Todo ?? view.Title ?? "", 44), 12, FontWeights.Normal);
                double op = 28 + MeasureText(Texts.Clamp(view.OpText ?? "", 50), 11.5, FontWeights.Normal)
                    + MeasureText(Texts.Clamp(view.Detail ?? "", 42), 10.5, FontWeights.Normal);
                double bottom = 28 + MeasureText("12 / 34 \u6B65\u9AA4 \u00B7 100%", 10.5, FontWeights.Normal)
                    + MeasureText("00:00:00", 10.5, FontWeights.Normal) + 16;
                natural = Math.Max(header, Math.Max(task, Math.Max(op, bottom)));
            }
            return Math.Min(460, Math.Max(300, natural + 8));
        }

        // ------------------------------------------------------------- 全屏检测
        private void PollFullscreen()
        {
            if (_hwnd == IntPtr.Zero) return;
            bool was = _fullscreenNow;
            _fullscreenNow = DetectFullscreen();
            if (_fullscreenNow != was)
            {
                if (_fullscreenNow && IsVisible) FadeOut();      // 进入全屏 → 暂时隐藏
                else if (!_fullscreenNow) ReconcileVisibility(); // 退出全屏 → 按策略恢复
            }
        }

        private static bool DetectFullscreen()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            var rect = Native.WindowRect(foreground);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < 200 || h < 200) return false;
            var info = Native.MonitorInfoFromWindow(foreground);
            int mw = info.rcMonitor.Right - info.rcMonitor.Left;
            int mh = info.rcMonitor.Bottom - info.rcMonitor.Top;
            if (Math.Abs(w - mw) > 4 || Math.Abs(h - mh) > 4) return false; // 需覆盖整屏
            var cls = new StringBuilder(64);
            Native.GetClassName(foreground, cls, 64);
            string className = cls.ToString();
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd") return false;
            return true;
        }

        private void StopTimers()
        {
            if (_elapsedTimer != null) _elapsedTimer.Stop();
            if (_fullscreenTimer != null) _fullscreenTimer.Stop();
        }

        // ------------------------------------------------------------- 打开 DSH 主窗口（用户主动）
        private void BringDshToFront()
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((hwnd, lParam) =>
            {
                if (hwnd == _hwnd) return true;
                if (!Native.IsWindowVisible(hwnd)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return true;
                Process process;
                try { process = Process.GetProcessById((int)pid); }
                catch { return true; }
                if (process == null || process.ProcessName.IndexOf("deepseek-harness-desktop",
                    StringComparison.OrdinalIgnoreCase) < 0) return true;
                var text = new StringBuilder(256);
                Native.GetWindowText(hwnd, text, 256);
                if (text.Length == 0) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                Native.ShowWindow(found, Native.SW_RESTORE);
                Native.SetForegroundWindow(found);
            }
        }
    }

    // =====================================================================
    // SseClient — 只读流订阅（后台线程 + Dispatcher 上行）
    // =====================================================================
    internal sealed class SseClient
    {
        private readonly string _url;
        private readonly Action<OverlayConfig> _onConfig;
        private readonly Action<OverlayState> _onState;
        private Thread _thread;
        private volatile bool _stop;
        private DateTime _lastAnyData = DateTime.UtcNow;

        public SseClient(string url, Action<OverlayConfig> onConfig, Action<OverlayState> onState)
        {
            _url = url;
            _onConfig = onConfig;
            _onState = onState;
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "dsh-overlay-sse" };
            _thread.Start();
        }

        public void Stop() { _stop = true; }

        private void Loop()
        {
            int delayMs = 2000;
            while (!_stop)
            {
                bool connected = false;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_url);
                    request.Proxy = null;
                    request.Timeout = 15000;
                    request.ReadWriteTimeout = 20000;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    {
                        if (stream == null) throw new IOException("empty stream");
                        var buffer = new byte[8192];
                        var pending = new StringBuilder();
                        while (!_stop)
                        {
                            int read;
                            try
                            {
                                var async = stream.BeginRead(buffer, 0, buffer.Length, null, null);
                                if (!async.AsyncWaitHandle.WaitOne(20000)) break; // 读超时 → 重连
                                read = stream.EndRead(async);
                            }
                            catch (Exception)
                            {
                                break;
                            }
                            if (read <= 0) break;
                            connected = true;
                            _lastAnyData = DateTime.UtcNow;
                            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            ParseChunk(pending);
                        }
                    }
                }
                catch (Exception)
                {
                    // 任何断线都走退避重连；DSH 长时间不可达则自行退出
                }
                if (_stop) break;
                if (!connected && DateTime.UtcNow - _lastAnyData > TimeSpan.FromSeconds(75))
                {
                    // DSH 核心长时间不可达：退出本进程（宿主按需重启或已关闭）
                    break;
                }
                delayMs = Math.Min(15000, delayMs + 2000);
                Thread.Sleep(delayMs);
            }
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_stop) Application.Current.Shutdown();
            }));
        }

        private void ParseChunk(StringBuilder pending)
        {
            string text = pending.ToString();
            int idx;
            while ((idx = text.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
            {
                string block = text.Substring(0, idx);
                text = text.Substring(idx + 2);
                foreach (string line in block.Split('\n'))
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string payload = line.Substring(5).Trim();
                    if (payload.Length == 0) continue;
                    try { Dispatch(payload); }
                    catch { /* 坏帧忽略（只读观察者） */ }
                }
            }
            pending.Clear();
            pending.Append(text);
        }

        private void Dispatch(string json)
        {
            _lastAnyData = DateTime.UtcNow;
            var map = Json.ToMap(json);
            object kindObj;
            if (!map.TryGetValue("kind", out kindObj)) return;
            string kind = Convert.ToString(kindObj, CultureInfo.InvariantCulture);
            object payloadObj;
            if (!map.TryGetValue("payload", out payloadObj)) return;
            var payload = payloadObj as Dictionary<string, object>;
            if (payload == null) return;
            if (kind == "config")
            {
                object configObj;
                var configMap = payload.TryGetValue("config", out configObj)
                    ? configObj as Dictionary<string, object> : null;
                var cfg = OverlayConfig.FromJson(configMap);
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _onConfig(cfg)));
            }
            else if (kind == "state")
            {
                var state = OverlayState.FromJson(payload);
                Application.Current.Dispatcher.BeginInvoke(new Action(() => _onState(state)));
            }
        }
    }

    // =====================================================================
    // Json — 极简 JSON 解析（无第三方依赖）
    // =====================================================================
    internal static class Json
    {
        public static Dictionary<string, object> ToMap(string json)
        {
            int index = 0;
            return ParseObject(json, ref index);
        }

        private static Dictionary<string, object> ParseObject(string json, ref int i)
        {
            var map = new Dictionary<string, object>();
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return map;
            i++;
            while (i < json.Length)
            {
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == '}') { i++; break; }
                string key = ParseString(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                map[key] = ParseValue(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') i++;
            }
            return map;
        }

        private static object ParseValue(string json, ref int i)
        {
            SkipWs(json, ref i);
            if (i >= json.Length) return null;
            char c = json[i];
            if (c == '"') return ParseString(json, ref i);
            if (c == '{') return ParseObject(json, ref i);
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                while (i < json.Length)
                {
                    SkipWs(json, ref i);
                    if (i < json.Length && json[i] == ']') { i++; break; }
                    list.Add(ParseValue(json, ref i));
                    SkipWs(json, ref i);
                    if (i < json.Length && json[i] == ',') i++;
                }
                return list;
            }
            int start = i;
            while (i < json.Length && "0123456789-+.eEtruefalsn".IndexOf(json[i]) >= 0) i++;
            string token = json.Substring(start, i - start);
            if (token == "true") return true;
            if (token == "false") return false;
            if (token == "null" || token.Length == 0) return null;
            double number;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                if (number == Math.Floor(number) && Math.Abs(number) < 9.2e18) return (long)number;
                return number;
            }
            return token;
        }

        private static string ParseString(string json, ref int i)
        {
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c == '\\' && i < json.Length)
                {
                    char escaped = json[i++];
                    if (escaped == 'n') sb.Append('\n');
                    else if (escaped == 't') sb.Append('\t');
                    else if (escaped == 'r') sb.Append('\r');
                    else if (escaped == 'u' && i + 4 <= json.Length)
                    {
                        try
                        {
                            sb.Append((char)int.Parse(json.Substring(i, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        catch { }
                    }
                    else sb.Append(escaped);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void SkipWs(string json, ref int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
        }
    }

    // =====================================================================
    // Program
    // =====================================================================
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            MakeDpiAware();
            string url = null;
            string stateFile = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--url") url = args[i + 1];
                if (args[i] == "--state") stateFile = args[i + 1];
            }
            if (url == null)
            {
                url = Environment.GetEnvironmentVariable("DSH_PROGRESS_OVERLAY_URL")
                    ?? "http://127.0.0.1:3080/api/dsh-overlay/stream";
            }
            var application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 决策回传地址：由 SSE 地址推导（去掉路径部分）
            string baseUrl = url;
            int apiIndex = url.IndexOf("/api/", StringComparison.Ordinal);
            if (apiIndex > 0) baseUrl = url.Substring(0, apiIndex);
            var window = new OverlayWindow(stateFile, baseUrl);
            var client = new SseClient(url, window.ApplyConfig, window.ApplyState);
            window.Closed += (s, e) => client.Stop();
            // 不直接 Run(window)：窗口首次展示必须走 ShowNoActivate/FadeIn，
            // 避免进程启动时抢用户焦点。
            client.Start();
            application.Run();
            client.Stop();
            return 0;
        }

        private static void MakeDpiAware()
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PMv2
                return;
            }
            catch { }
            try { Native.SetProcessDPIAware(); } catch { }
        }
    }
}

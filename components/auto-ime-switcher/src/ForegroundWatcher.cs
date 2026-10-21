using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AutoImeSwitcher
{
    public class ForegroundInfo
    {
        public IntPtr Hwnd = IntPtr.Zero;
        public uint Pid;
        public uint ThreadId;
        public string ProcessName = string.Empty;
        public string Title = string.Empty;
        public string ExePath = string.Empty;

        public bool Valid
        {
            get { return Hwnd != IntPtr.Zero; }
        }
    }

    /// <summary>
    /// 前台窗口监听。使用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 事件驱动，
    /// 同时监听前台顶层窗口的标题变化，覆盖同一主窗口内切换文档。
    /// </summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        // 委托必须长期持有引用，否则被 GC 后回调会导致崩溃
        private NativeMethods.WinEventDelegate _callback;
        private IntPtr _hook = IntPtr.Zero;
        private IntPtr _titleHook = IntPtr.Zero;
        private IntPtr _lastHwnd = IntPtr.Zero;
        private string _lastTitle;
        private uint _lastPid, _lastThread;
        private bool _disposed;

        public event EventHandler<ForegroundInfo> ForegroundChanged;

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;

            _callback = new NativeMethods.WinEventDelegate(OnWinEvent);
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);

            _titleHook = NativeMethods.SetWinEventHook(0x800C, 0x800C, IntPtr.Zero,
                _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (_titleHook == IntPtr.Zero)
                Logger.Warn("窗口标题监听安装失败，标题规则可能延迟生效");

            if (_hook == IntPtr.Zero)
                Logger.Error("SetWinEventHook 安装失败，lastError=" + Marshal.GetLastWin32Error());
            else
                Logger.Info("前台事件监听已启动 (EVENT_SYSTEM_FOREGROUND)");
        }

        public void Stop()
        {
            if (_titleHook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_titleHook); _titleHook = IntPtr.Zero; }
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
                Logger.Info("前台事件监听已停止");
            }
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;
            if (eventType == 0x800C && (idObject != 0 || idChild != 0 || hwnd != NativeMethods.GetForegroundWindow())) return;
            if (hwnd != NativeMethods.GetForegroundWindow()) return;
            var info = BuildInfo(hwnd);
            if (!AcceptsChange(info)) return;
            var handler = ForegroundChanged;
            if (handler != null)
            {
                try { handler(this, info); }
                catch (Exception ex) { Logger.Error("处理前台事件异常: " + ex.Message); }
            }
        }

        internal bool AcceptsChange(ForegroundInfo info)
        {
            if (info.Hwnd == _lastHwnd && info.Title == _lastTitle && info.Pid == _lastPid && info.ThreadId == _lastThread) return false;
            _lastHwnd = info.Hwnd; _lastTitle = info.Title; _lastPid = info.Pid; _lastThread = info.ThreadId;
            return true;
        }

        /// <summary>构造当前前台窗口信息。</summary>
        public static ForegroundInfo BuildInfo(IntPtr hwnd)
        {
            var info = new ForegroundInfo();
            info.Hwnd = hwnd;
            if (hwnd == IntPtr.Zero) return info;

            uint pid;
            info.ThreadId = NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            info.Pid = pid;
            info.Title = NativeMethods.GetWindowTitle(hwnd);

            string exe = NativeMethods.GetProcessPath(pid);
            if (!string.IsNullOrEmpty(exe))
            {
                info.ExePath = exe;
                try { info.ProcessName = Path.GetFileName(exe); }
                catch { info.ProcessName = string.Empty; }
            }

            if (string.IsNullOrEmpty(info.ProcessName))
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    info.ProcessName = p.ProcessName + ".exe";
                }
                catch { info.ProcessName = string.Empty; }
            }

            return info;
        }

        public static ForegroundInfo GetForegroundInfo()
        {
            return BuildInfo(NativeMethods.GetForegroundWindow());
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}

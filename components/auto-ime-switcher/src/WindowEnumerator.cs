using System;
using System.Collections.Generic;
using System.IO;

namespace AutoImeSwitcher
{
    public class WindowEntry
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string ProcessName = string.Empty;
        public string DisplayName = string.Empty;
        public string Title = string.Empty;
        public string ExePath = string.Empty;
    }

    /// <summary>枚举当前有可见窗口的进程，用于"从正在运行的程序中选择"。</summary>
    public static class WindowEnumerator
    {
        /// <summary>
        /// 枚举当前有可见窗口的进程。
        /// </summary>
        /// <param name="includeUntitled">
        /// true 时连没有标题的窗口一起枚举（托盘程序、无标题工具窗口等）。
        /// 默认 false，只列有标题的窗口，列表更干净。
        /// </param>
        /// <remarks>
        /// V1.0.6（中-10）：按 hwnd 去重而不是按进程名。旧实现下同一进程的
        /// 多个窗口只显示第一个（两个 SolidWorks 文档窗口只列一个），
        /// 用户无法确认选的是哪个；现在逐个窗口列出，标题列直接区分。
        /// </remarks>
        public static List<WindowEntry> EnumerateRunningWindows(bool includeUntitled = false)
        {
            var list = new List<WindowEntry>();
            var seen = new HashSet<IntPtr>();   // 按窗口句柄去重
            var selfId = System.Diagnostics.Process.GetCurrentProcess().Id;

            NativeMethods.EnumWindowsProc proc = delegate (IntPtr hwnd, IntPtr lParam)
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (!seen.Add(hwnd)) return true;

                    string title = NativeMethods.GetWindowTitle(hwnd);
                    if (title == "Program Manager") return true;   // 桌面
                    if (!includeUntitled && string.IsNullOrWhiteSpace(title)) return true;

                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                    if (pid == 0 || pid == (uint)selfId) return true;

                    string exe = NativeMethods.GetProcessPath(pid);
                    if (string.IsNullOrEmpty(exe)) return true;

                    string procName = Path.GetFileName(exe);
                    if (string.IsNullOrEmpty(procName)) return true;

                    var entry = new WindowEntry();
                    entry.Hwnd = hwnd;
                    entry.Pid = pid;
                    entry.ExePath = exe;
                    entry.ProcessName = procName;
                    entry.Title = title;
                    entry.DisplayName = StripExe(procName);
                    list.Add(entry);
                }
                catch
                {
                    // 单个窗口失败不影响整体
                }
                return true;
            };

            NativeMethods.EnumWindows(proc, IntPtr.Zero);

            list.Sort(delegate (WindowEntry a, WindowEntry b)
            {
                int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static string StripExe(string name)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }
    }
}

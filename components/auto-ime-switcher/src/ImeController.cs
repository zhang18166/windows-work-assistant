using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoImeSwitcher
{
    public enum SwitchMethod { KeyboardLayout = 0, ShiftKey = 1, CapsLockKey = 2, ImeOpenClose = 3 }
    public enum SwitchResult { AlreadyEnglish, Switched, RequestedButNotVerified, NoEnglishLayout, NoWindow, SendFailed, Skipped, UnknownState }
    // 回归测试使用假平台，不向用户桌面发送输入。
    internal interface IImePlatform
    {
        IntPtr Foreground { get; }
        bool IsWindowOf(IntPtr window, uint thread, uint process);
        IntPtr Focus(uint thread);
        IntPtr Layout(uint thread);
        bool? ReadOpen(IntPtr window);
        bool WriteOpen(IntPtr window, bool open);
        bool? ReadCapsLock();
        bool ModifiersDown { get; }
        bool Tap(byte key);
        bool RequestLayout(IntPtr window, IntPtr layout);
        void Wait(int milliseconds);
    }
    internal sealed class ImeRestorePoint
    {
        internal IntPtr Window, Target, Layout;
        internal uint Thread, Process;
        internal long ManualRevision;
        internal bool Invalidated;
    }
    public static class ImeController
    {
        internal static IImePlatform Platform = new NativeImePlatform();
        internal static Func<bool?> CapsLockReader;
        private static readonly object ActionLock = new object();
        private static IntPtr _englishHkl;
        private static long _manualRevision;
        internal static long ManualRevision { get { return Interlocked.Read(ref _manualRevision); } }
        internal static void NoteManualModeInput() { Interlocked.Increment(ref _manualRevision); }
        public static readonly SwitchMethod[] MethodOrder = { SwitchMethod.ImeOpenClose, SwitchMethod.ShiftKey, SwitchMethod.CapsLockKey, SwitchMethod.KeyboardLayout };
        public static bool EnglishLayoutAvailable { get { return _englishHkl != IntPtr.Zero; } }
        public static void RefreshEnglishLayout()
        {
            _englishHkl = IntPtr.Zero;
            int count = NativeMethods.GetKeyboardLayoutList(0, null);
            if (count <= 0) return;
            var layouts = new IntPtr[count];
            int got = NativeMethods.GetKeyboardLayoutList(count, layouts);
            for (int i = 0; i < got; i++)
                if (NativeMethods.LangIdOf(layouts[i]) == NativeMethods.LANGID_ENGLISH_US) { _englishHkl = layouts[i]; break; }
        }
        public static string DescribeMethod(SwitchMethod method)
        {
            switch (method)
            {
                case SwitchMethod.ImeOpenClose: return "输入法开关";
                case SwitchMethod.ShiftKey: return "Shift 键";
                case SwitchMethod.CapsLockKey: return "CapsLock 大写键";
                default: return "切换键盘布局";
            }
        }
        public static SwitchMethod MethodAt(int index) { return index >= 0 && index < MethodOrder.Length ? MethodOrder[index] : SwitchMethod.ImeOpenClose; }
        public static int MethodIndex(SwitchMethod method) { return Math.Max(0, Array.IndexOf(MethodOrder, method)); }
        public static bool IsEnglish(uint thread) { return thread != 0 && NativeMethods.LangIdOf(Platform.Layout(thread)) == NativeMethods.LANGID_ENGLISH_US; }
        public static bool? ReadImeOpen(IntPtr window) { return Platform.ReadOpen(window); }
        public static IntPtr GetFocusWindowOfThread(uint thread) { return Platform.Focus(thread); }
        public static string DescribeLayoutShort(IntPtr layout)
        {
            ushort lang = NativeMethods.LangIdOf(layout); string name;
            try { name = new CultureInfo(lang).DisplayName; } catch { name = "未知语言"; }
            return string.Format("{0} 0x{1:X4}", name, lang);
        }
        public static string DescribeState(IntPtr window, uint thread, SwitchMethod method) { return ReadIndicator(window, thread, method); }
        internal static string ReadIndicator(IntPtr window, uint thread, SwitchMethod method)
        {
            if (window == IntPtr.Zero || thread == 0) return "未知";
            if (method == SwitchMethod.CapsLockKey)
            {
                bool? caps = Platform.ReadCapsLock();
                return caps.HasValue ? (caps.Value ? "CAPS" : "caps关") : "未知";
            }
            IntPtr layout = Platform.Layout(thread);
            if (layout == IntPtr.Zero) return "未知";
            if (NativeMethods.LangIdOf(layout) == NativeMethods.LANGID_ENGLISH_US) return "EN";
            bool? open = Platform.ReadOpen(window);
            return open.HasValue ? (open.Value ? "中" : "EN") : "未知";
        }
        internal static bool SameWindow(IntPtr window, uint thread, uint process)
        { return window != IntPtr.Zero && thread != 0 && process != 0 && Platform.IsWindowOf(window, thread, process); }
        private static bool WaitForInputIdle(Func<bool> valid)
        {
            for (int waited = 0; Platform.ModifiersDown && waited < 240; waited += 20)
            {
                Platform.Wait(20);
                if (!valid()) return false;
            }
            return valid() && !Platform.ModifiersDown;
        }
        // 每次操作前检查任务版本、窗口身份、焦点及用户修饰键。没有隐式方式降级。
        internal static SwitchResult EnsureEnglish(IntPtr window, uint thread, uint process, SwitchMethod method, Func<bool> guard, out ImeRestorePoint restore)
        {
            restore = null;
            lock (ActionLock)
            {
                if (!SameWindow(window, thread, process)) return SwitchResult.NoWindow;
                long revision = ManualRevision;
                Func<bool> valid = () => guard() && Platform.Foreground == window && SameWindow(window, thread, process) && ManualRevision == revision;
                if (!valid()) return SwitchResult.Skipped;
                // Alt+Tab 的前台事件可能早于 Alt 松开；仅有界等待，不替用户释放修饰键。
                if (!WaitForInputIdle(valid)) return SwitchResult.Skipped;
                IntPtr target = Platform.Focus(thread);
                if (!SameWindow(target, thread, process)) target = window;
                IntPtr capturedTarget = target;
                Func<bool> canWrite = () => {
                    if (!valid() || Platform.ModifiersDown || !SameWindow(capturedTarget, thread, process)) return false;
                    IntPtr focus = Platform.Focus(thread);
                    return focus == IntPtr.Zero ? capturedTarget == window : focus == capturedTarget;
                };
                if (method == SwitchMethod.KeyboardLayout)
                {
                    if (!EnglishLayoutAvailable) return SwitchResult.NoEnglishLayout;
                    if (IsEnglish(thread)) return SwitchResult.AlreadyEnglish;
                    if (!canWrite()) return SwitchResult.Skipped;
                    if (!Platform.RequestLayout(target, _englishHkl)) return SwitchResult.SendFailed;
                    Platform.Wait(60);
                    if (!valid()) return SwitchResult.Skipped;
                    return IsEnglish(thread) ? SwitchResult.Switched : SwitchResult.RequestedButNotVerified;
                }
                if (method == SwitchMethod.CapsLockKey)
                {
                    bool? caps = Platform.ReadCapsLock();
                    if (!caps.HasValue) return SwitchResult.UnknownState;
                    if (caps.Value) return SwitchResult.AlreadyEnglish;
                    if (!canWrite()) return SwitchResult.Skipped;
                    if (!Platform.Tap(NativeMethods.VK_CAPITAL)) return SwitchResult.SendFailed;
                    Platform.Wait(60);
                    if (!valid()) return SwitchResult.Skipped;
                    caps = Platform.ReadCapsLock();
                    return !caps.HasValue ? SwitchResult.UnknownState : (caps.Value ? SwitchResult.Switched : SwitchResult.RequestedButNotVerified);
                }
                bool? before = Platform.ReadOpen(target);
                if (!before.HasValue) return SwitchResult.UnknownState;
                if (!before.Value) return SwitchResult.AlreadyEnglish;
                // Shift 是翻转操作，只发送一次；未验证成功也不再次翻转。
                int attempts = method == SwitchMethod.ShiftKey ? 1 : 2;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    if (!canWrite()) return SwitchResult.Skipped;
                    bool? current = Platform.ReadOpen(target);
                    if (!current.HasValue) return SwitchResult.UnknownState;
                    if (!current.Value) return SwitchResult.AlreadyEnglish;
                    if (!canWrite()) return SwitchResult.Skipped;
                    bool sent = method == SwitchMethod.ShiftKey ? Platform.Tap(NativeMethods.VK_LSHIFT) : Platform.WriteOpen(target, false);
                    if (!sent) return SwitchResult.SendFailed;
                    Platform.Wait(60);
                    if (!canWrite()) return SwitchResult.Skipped;
                    bool? after = Platform.ReadOpen(target);
                    if (!after.HasValue) return SwitchResult.UnknownState;
                    if (!after.Value)
                    {
                        restore = new ImeRestorePoint { Window = window, Target = target, Thread = thread,
                            Process = process, Layout = Platform.Layout(thread), ManualRevision = revision };
                        return SwitchResult.Switched;
                    }
                }
                return SwitchResult.RequestedButNotVerified;
            }
        }
        internal static void ObserveRestorePoint(ImeRestorePoint restore)
        {
            if (restore == null || restore.Invalidated) return;
            if (restore.ManualRevision != ManualRevision || restore.Layout == IntPtr.Zero ||
                restore.Layout != Platform.Layout(restore.Thread) ||
                !SameWindow(restore.Target, restore.Thread, restore.Process) || Platform.ReadOpen(restore.Target) != false)
                restore.Invalidated = true;
        }
        internal static bool RestorePrevious(ImeRestorePoint restore, Func<bool> guard)
        {
            if (restore == null) return false;
            lock (ActionLock)
            {
                if (restore.Invalidated || !WaitForInputIdle(() => guard() && restore.ManualRevision == ManualRevision)) return false;
                ObserveRestorePoint(restore);
                if (restore.Invalidated || !guard() || Platform.ModifiersDown) return false;
                if (!SameWindow(restore.Window, restore.Thread, restore.Process) || !SameWindow(restore.Target, restore.Thread, restore.Process) || !guard() || restore.ManualRevision != ManualRevision) return false;
                restore.Invalidated = true; // 每条恢复记录只消费一次；从不发送全局按键。
                if (!Platform.WriteOpen(restore.Target, true)) return false;
                Platform.Wait(30);
                return Platform.ReadOpen(restore.Target) == true;
            }
        }
        private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint vk, scan, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        private static IntPtr _hook;
        private static KeyboardProc _callback;
        public static bool StartKeyboardTracking()
        {
            if (_hook != IntPtr.Zero) return true;
            _callback = OnKey; _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }
        public static void StopKeyboardTracking()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; _callback = null;
        }
        // 仅观察可能的手动切换；不猜状态、不记录文字、不释放用户按键。
        private static IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (message.ToInt64() == 0x100 || message.ToInt64() == 0x104))
            {
                try {
                    KeyData key = (KeyData)Marshal.PtrToStructure(data, typeof(KeyData));
                    bool shiftOrCaps = key.vk == 0x10 || key.vk == 0x14 || key.vk == 0xA0 || key.vk == 0xA1;
                    bool languageShortcut = key.vk == 0x20 &&
                        (((NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) ||
                         ((NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0) ||
                         ((NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0));
                    // Alt+Tab / Ctrl+S 本身不代表手动切换输入法；Shift 及语言快捷键保守接管。
                    if (key.extra != NativeImePlatform.InputMarker && (shiftOrCaps || languageShortcut)) NoteManualModeInput();
                } catch { }
            }
            return CallNextHookEx(_hook, code, message, data);
        }
    }
}

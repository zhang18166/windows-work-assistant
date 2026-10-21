using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoImeSwitcher
{
    internal sealed class NativeImePlatform : IImePlatform
    {
        internal static readonly UIntPtr InputMarker = new UIntPtr(0x41494D45u);
        public IntPtr Foreground { get { return NativeMethods.GetForegroundWindow(); } }
        public bool IsWindowOf(IntPtr window, uint thread, uint process)
        {
            uint actual;
            return NativeMethods.IsWindow(window) && NativeMethods.GetWindowThreadProcessId(window, out actual) == thread && actual == process;
        }
        public IntPtr Focus(uint thread)
        {
            var info = new NativeMethods.GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO));
            return thread != 0 && NativeMethods.GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
        }
        public IntPtr Layout(uint thread) { return NativeMethods.GetKeyboardLayout(thread); }
        public bool? ReadOpen(IntPtr window)
        {
            IntPtr ime = NativeMethods.ImmGetDefaultIMEWnd(window), result;
            if (window == IntPtr.Zero || ime == IntPtr.Zero ||
                NativeMethods.SendMessageTimeout(ime, NativeMethods.WM_IME_CONTROL,
                    new IntPtr(NativeMethods.IMC_GETOPENSTATUS), IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG, 200, out result) == IntPtr.Zero) return null;
            return result.ToInt64() == 0 ? false : result.ToInt64() == 1 ? true : (bool?)null;
        }
        public bool WriteOpen(IntPtr window, bool open)
        {
            IntPtr ime = NativeMethods.ImmGetDefaultIMEWnd(window), result;
            return ime != IntPtr.Zero && NativeMethods.SendMessageTimeout(ime,
                NativeMethods.WM_IME_CONTROL, new IntPtr(NativeMethods.IMC_SETOPENSTATUS),
                new IntPtr(open ? 1 : 0), NativeMethods.SMTO_ABORTIFHUNG, 200, out result) != IntPtr.Zero;
        }
        public bool? ReadCapsLock()
        {
            // 从有消息循环的 UI 线程读取 toggle 位，后台不读 GetAsyncKeyState 的最低位。
            Func<bool?> reader = ImeController.CapsLockReader;
            return reader == null ? null : reader();
        }
        public bool ModifiersDown
        {
            get
            {
                foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x14 })
                    if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) return true;
                return false;
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput
        { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyInput key; }
        [StructLayout(LayoutKind.Sequential)] private struct Input
        { public uint type; public InputUnion data; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
        public bool Tap(byte key)
        {
            var inputs = new[] {
                new Input { type = 1, data = new InputUnion { key = new KeyInput { vk = key, extra = InputMarker } } },
                new Input { type = 1, data = new InputUnion { key = new KeyInput { vk = key, flags = 2, extra = InputMarker } } }
            };
            // 同一次提交 down/up，避免两次 keybd_event 之间焦点改变或用户按键插入。
            uint sent = SendInput(2, inputs, Marshal.SizeOf(typeof(Input)));
            if (sent == 1)
                SendInput(1, new[] { inputs[1] }, Marshal.SizeOf(typeof(Input)));
            return sent == 2;
        }
        public bool RequestLayout(IntPtr window, IntPtr layout)
        {
            IntPtr result;
            return NativeMethods.SendMessageTimeout(window, NativeMethods.WM_INPUTLANGCHANGEREQUEST,
                IntPtr.Zero, layout, NativeMethods.SMTO_ABORTIFHUNG, 200, out result) != IntPtr.Zero;
        }
        public void Wait(int milliseconds) { Thread.Sleep(milliseconds); }
    }
}

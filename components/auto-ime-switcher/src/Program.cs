using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    internal static class Program
    {
        public static string HostVersionText = null;
        // 界面统一读取当前程序集版本，避免显示值与实际运行文件不一致。
        public static string VersionText
        {
            get { return HostVersionText ?? ("v" + typeof(Program).Assembly.GetName().Version.ToString(3)); }
        }

        private const string MutexName = "Local\\AutoImeSwitcher_SingleInstance_v1";

        /// <summary>第二个实例用它通知第一个实例“把设置窗口弹出来”。</summary>
        public const string ShowSettingsEventName = "Local\\AutoImeSwitcher_ShowSettings_v1";

        [STAThread]
        private static void Main(string[] args)
        {
            // DPI 感知由 app.manifest 的 PerMonitorV2 声明（.NET Framework 4.8
            // 没有 Application.SetHighDpiMode，那是 .NET Core 的 API）。
            // 分层浮窗靠自绘位图，DPI 模式不一致会让位图尺寸与窗口边界错位。
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --settings / -s：启动时直接打开设置窗口，也用于“叫醒”已在运行的实例
            bool openSettings = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase))
                    {
                        openSettings = true;
                        break;
                    }
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已经在运行：如果对方要的是设置窗口，就通知它弹出来，不再弹“已经在运行”的提示
                    if (openSettings && TrySignalExisting())
                        return;

                    MessageBox.Show("自动输入法助手已经在运行了。\n请查看系统托盘，或双击托盘图标打开设置。",
                        "自动输入法助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AppConfig config = AppConfig.Load();
                using (var context = new AppContext(config, openSettings))
                    Application.Run(context);
                GC.KeepAlive(mutex);
            }
        }

        private static bool TrySignalExisting()
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowSettingsEventName))
                {
                    ev.Set();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

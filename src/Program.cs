using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

[assembly:AssemblyTitle("Windows效率助手")]
[assembly:AssemblyProduct("Windows效率助手")]
[assembly:AssemblyVersion("1.0.1.0")]
[assembly:AssemblyFileVersion("1.0.1.0")]

namespace WindowsWorkAssistant
{
    internal static class Program
    {
        internal const string ShowEvent = "Local\\WindowsWorkAssistant_Show_v1";
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using(var mutex=new Mutex(true,"Local\\WindowsWorkAssistant_v1",out created))
            {
                if(!created)
                {
                    try { using(var signal=EventWaitHandle.OpenExisting(ShowEvent)) signal.Set(); } catch { }
                    return;
                }
                // 与独立版互斥，避免两个监听器同时切换或重复导出同一文件。
                var guards=new List<Mutex>();
                try
                {
                    foreach(string name in new[] {"Local\\AutoImeSwitcher_SingleInstance_v1","Local\\ExcelAutoPDF_6F71F13C"})
                    {
                        bool fresh;var guard=new Mutex(true,name,out fresh);guards.Add(guard);
                        if(!fresh) { MessageBox.Show("请先退出独立版的自动输入法助手和 Excel 转 PDF，再打开整合版。","Windows效率助手"); return; }
                    }
                    bool silent=Array.IndexOf(args,"--silent")>=0;
                    using(var context=new SuiteContext(silent)) Application.Run(context);
                }
                catch(Exception ex) { TraceLog.Write(ex.ToString()); MessageBox.Show("启动失败："+ex.Message,"Windows效率助手"); }
                finally { foreach(var guard in guards) guard.Dispose(); }
            }
        }
    }
}

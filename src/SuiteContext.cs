using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace WindowsWorkAssistant
{
    /// <summary>一个消息循环、一个托盘；输入法核心直接在本进程中运行。</summary>
    internal sealed class SuiteContext : ApplicationContext
    {
        private readonly SuiteForm _form;
        private readonly NotifyIcon _tray;
        private readonly AutoImeSwitcher.AppContext _ime;
        private readonly EngineClient _engine;
        private readonly EventWaitHandle _show;
        private readonly System.Windows.Forms.Timer _timer;
        private bool _exiting;
        public SuiteContext(bool silent)
        {
            var settings=SuiteSettings.Load();
            AutoImeSwitcher.AutostartHelper.ConfigureHost("WindowsWorkAssistant","--silent");
            AutoImeSwitcher.Program.HostVersionText="v1.0.13";
            _form=new SuiteForm(settings);
            IntPtr handle=_form.Handle; // 后台消息进入前先创建 UI 调度目标。
            _engine=new EngineClient();
            var options=new AutoImeSwitcher.AppHostOptions { ModuleEnabled=settings.ImeEnabled,
                ShowSettings=delegate { ShowWindow(true); }, RequestExit=RequestExit };
            _ime=new AutoImeSwitcher.AppContext(AutoImeSwitcher.AppConfig.Load(),false,options);
            _form.Attach(_ime,_engine,RequestExit);
            _tray=new NotifyIcon { Icon=_form.Icon, Text="Windows效率助手 v"+BuildInfo.Version, Visible=true };
            var menu=new ContextMenuStrip();
            menu.Items.Add("打开主界面",null,delegate { ShowWindow(false); });
            menu.Items.Add("输入法设置",null,delegate { ShowWindow(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出程序",null,delegate { RequestExit(); });
            _tray.ContextMenuStrip=menu;
            _tray.DoubleClick+=delegate { ShowWindow(false); };
            _show=new EventWaitHandle(false,EventResetMode.AutoReset,Program.ShowEvent);
            _timer=new System.Windows.Forms.Timer { Interval=400 };
            _timer.Tick+=delegate {
                if(_show.WaitOne(0)) ShowWindow(false);
                _form.UpdateImeStatus(_ime.CurrentActivity);
            };
            _timer.Start();
            _engine.Start();
            TraceLog.Write("整合版已启动 v"+BuildInfo.Version);
            if(!silent) ShowWindow(false);
        }
        private void ShowWindow(bool ime)
        {
            if(_exiting)return;
            _form.SelectModule(ime);
            _form.Show();_form.WindowState=FormWindowState.Normal;_form.BringToFront();_form.Activate();
            TraceLog.Write("主界面已打开");
        }
        private async void RequestExit()
        {
            if(_exiting)return;
            _exiting=true;
            _form.Enabled=false;_form.Text="Windows效率助手 · 正在退出";
            _timer.Stop();_ime.SetModuleEnabled(false);
            await _engine.StopAsync();
            TraceLog.Write("整合版正常退出");
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                _timer.Stop();_timer.Dispose();_show.Dispose();
                _tray.Visible=false;_tray.Dispose();
                _engine.Dispose();_ime.Dispose();_form.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

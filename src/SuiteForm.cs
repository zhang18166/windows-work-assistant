using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WindowsWorkAssistant
{
    /// <summary>停靠/表格布局保证日志始终占用剩余区域；详细设置独立对话框。</summary>
    public sealed class SuiteForm : Form
    {
        private readonly SuiteSettings _settings;
        private AutoImeSwitcher.AppContext _ime;
        private EngineClient _engine;
        private Action _exit;
        private readonly TabControl _tabs=new TabControl();
        private readonly CheckBox _imeEnabled=new CheckBox { Text="启用输入法模块",AutoSize=true };
        private readonly CheckBox _pdfEnabled=new CheckBox { Text="启用 Excel 转 PDF",AutoSize=true };
        private readonly CheckBox _startup=new CheckBox { Text="开机启动整合版",AutoSize=true };
        private readonly TextBox _folder=new TextBox { ReadOnly=true,Dock=DockStyle.Fill };
        private readonly Label _status=new Label { Text="转换服务启动中…",AutoSize=true,ForeColor=Color.Teal };
        private readonly Label _progress=new Label { Text="剩余任务：0",AutoSize=true };
        private readonly Label _imeStatus=new Label { Text="等待识别前台程序",AutoSize=true };
        private readonly RichTextBox _logs=new RichTextBox { ReadOnly=true,Dock=DockStyle.Fill,WordWrap=true,BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle };
        private readonly Button _start=new Button { Text="开始监控",AutoSize=true };
        private readonly Button _stop=new Button { Text="停止监控",AutoSize=true };
        private readonly Button _configure=new Button { Text="监控设置…",AutoSize=true };
        private Dictionary<string,object> _config=new Dictionary<string,object>();
        private bool _ready,_running,_busy,_loading=true;
        private Control _imeSettings;
        public SuiteForm(SuiteSettings settings)
        {
            _settings=settings;
            Text="Windows效率助手 v"+BuildInfo.Version;
            Font=new Font("Microsoft YaHei UI",9F);
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(760,580);MinimumSize=new Size(660,500);
            BackColor=Color.FromArgb(244,247,250);StartPosition=FormStartPosition.CenterScreen;
            try { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var main=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));main.RowStyles.Add(new RowStyle(SizeType.AutoSize));main.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var heading=new Label { Text="Windows 效率助手   v"+BuildInfo.Version,AutoSize=true,Font=new Font(Font.FontFamily,15F,FontStyle.Bold),Margin=new Padding(0,0,0,8) };
            main.Controls.Add(heading,0,0);
            var switches=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=new Padding(0,0,0,8) };
            switches.Controls.AddRange(new Control[] {_imeEnabled,_pdfEnabled,_startup});
            main.Controls.Add(switches,0,1);
            _tabs.Dock=DockStyle.Fill;
            _tabs.SelectedIndexChanged+=delegate { FitImePage(); };
            _tabs.TabPages.Add(new TabPage("Excel 转 PDF") { BackColor=BackColor,Padding=new Padding(10) });
            _tabs.TabPages.Add(new TabPage("自动输入法") { BackColor=BackColor,Padding=new Padding(4) });
            main.Controls.Add(_tabs,0,2);Controls.Add(main);
            BuildPdfPage(_tabs.TabPages[0]);
            _imeEnabled.Checked=settings.ImeEnabled;_pdfEnabled.Checked=settings.PdfEnabled;
            _startup.Checked=AutoImeSwitcher.AutostartHelper.IsEnabled();
            _loading=false;
            _imeEnabled.CheckedChanged+=delegate {
                if(_loading||_ime==null)return;
                try { _settings.ImeEnabled=_imeEnabled.Checked;_settings.Save();_ime.SetModuleEnabled(_imeEnabled.Checked);_imeSettings.Enabled=_imeEnabled.Checked; }
                catch(Exception ex) { ShowError(ex.Message); }
            };
            _pdfEnabled.CheckedChanged+=delegate {
                if(_loading||!_ready)return;
                try { _settings.PdfEnabled=_pdfEnabled.Checked;_settings.Save();Send(_pdfEnabled.Checked?"start":"stop"); }
                catch(Exception ex) { ShowError(ex.Message); }
            };
            _startup.CheckedChanged+=delegate {
                if(_loading)return;
                try { AutoImeSwitcher.AutostartHelper.SetEnabled(_startup.Checked); }
                catch(Exception ex) { ShowError(ex.Message); }
                _loading=true;_startup.Checked=AutoImeSwitcher.AutostartHelper.IsEnabled();_loading=false;
            };
            Resize+=delegate { UpdateButtons(); };
        }
        private void BuildPdfPage(Control parent)
        {
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<6;i++)layout.RowStyles.Add(new RowStyle(i==5?SizeType.Percent:SizeType.AutoSize,i==5?100:0));
            layout.Controls.Add(new Label { Text="监控文件夹",AutoSize=true },0,0);
            layout.Controls.Add(_folder,0,1);
            var actions=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0,8,0,4) };
            var open=new Button { Text="打开目录",AutoSize=true };
            open.Click+=delegate { try { if(Directory.Exists(_folder.Text))Process.Start("explorer.exe","\""+_folder.Text+"\""); } catch(Exception ex) { ShowError(ex.Message); } };
            _start.Click+=delegate { Send("start"); };
            _stop.Click+=delegate { Send("stop"); };
            _configure.Click+=delegate { ConfigurePdf(); };
            actions.Controls.AddRange(new Control[] {_start,_stop,_configure,open});layout.Controls.Add(actions,0,2);
            var state=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,WrapContents=true };
            state.Controls.AddRange(new Control[] {_status,_progress});layout.Controls.Add(state,0,3);
            var logActions=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,WrapContents=true };
            logActions.Controls.Add(new Label { Text="转换日志 · 最新在上",AutoSize=true,Padding=new Padding(0,7,8,0) });
            var latest=new Button { Text="最新记录",AutoSize=true };
            latest.Click+=delegate { _logs.SelectionStart=0;_logs.ScrollToCaret(); };
            var logDir=new Button { Text="日志目录",AutoSize=true };
            logDir.Click+=delegate { Process.Start("explorer.exe",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Excel自动转PDF")); };
            logActions.Controls.Add(latest);logActions.Controls.Add(logDir);layout.Controls.Add(logActions,0,4);
            layout.Controls.Add(_logs,0,5);parent.Controls.Add(layout);
        }
        public void Attach(AutoImeSwitcher.AppContext ime,EngineClient engine,Action exit)
        {
            _ime=ime;_engine=engine;_exit=exit;
            var page=_tabs.TabPages[1];
            var panel=new Panel { Dock=DockStyle.Fill,AutoScroll=true };
            var form=ime.GetSettingsForm();form.TopLevel=false;form.FormBorderStyle=FormBorderStyle.None;
            form.Dock=DockStyle.Fill;form.ShowInTaskbar=false;
            panel.Controls.Add(form);page.Controls.Add(panel);form.Show();_imeSettings=form;form.Enabled=_settings.ImeEnabled;
            FitImePage();
            engine.Message+=message=>Post(delegate { OnEngineMessage(message); });
            engine.Failure+=error=>Post(delegate { _ready=false;_running=false;_busy=false;_status.Text="转换服务不可用";AppendLog(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | ERROR | "+error);UpdateButtons(); });
            UpdateButtons();
        }
        public void SelectModule(bool ime) { _tabs.SelectedIndex=ime?1:0;_loading=true;_startup.Checked=AutoImeSwitcher.AutostartHelper.IsEnabled();_loading=false; }
        private void FitImePage()
        {
            int width=660;
            var form=_imeSettings as AutoImeSwitcher.MainForm;
            if(_tabs.SelectedIndex==1&&form!=null)width=Math.Max(width,form.AutoScrollMinSize.Width+100);
            MinimumSize=new Size(width,500);
            if(Width<width)Width=width;
        }
        public void UpdateImeStatus(AutoImeSwitcher.ActivitySnapshot snapshot)
        {
            if(snapshot!=null) _imeStatus.Text=snapshot.DisplayName+" · "+snapshot.CurrentAction;
            _loading=true;_startup.Checked=AutoImeSwitcher.AutostartHelper.IsEnabled();_loading=false;
        }
        private void Post(Action action) { try { if(!IsDisposed) BeginInvoke(action); } catch(InvalidOperationException) { } }
        private static string TextOf(Dictionary<string,object> message,string key) { object value;return message.TryGetValue(key,out value)&&value!=null?Convert.ToString(value):""; }
        private void OnEngineMessage(Dictionary<string,object> message)
        {
            string type=TextOf(message,"event");
            if(type=="ready"||type=="config")
            {
                _config=(Dictionary<string,object>)message["config"];
                _folder.Text=TextOf(_config,"monitor_folder");
                if(type=="ready") { _ready=true;_status.Text="已停止";if(_settings.PdfEnabled&&!string.IsNullOrWhiteSpace(_folder.Text))Send("start"); }
            }
            else if(type=="log")AppendLog(TextOf(message,"line"));
            else if(type=="status")
            {
                string status=TextOf(message,"status");_status.Text=status;
                _running=status!="已停止";
                string detail=TextOf(message,"detail");if(detail.Length>0)_status.Text+=" · "+detail;
                _status.AutoEllipsis=true;_status.MaximumSize=new Size(Math.Max(200,_tabs.Width-70),0);
            }
            else if(type=="progress")
            {
                _progress.Text="剩余任务："+TextOf(message,"remaining");
                if(message.ContainsKey("eta")&&message["eta"]!=null)_progress.Text+=" · 预计剩余 "+Math.Ceiling(Convert.ToDouble(message["eta"])).ToString()+" 秒";
            }
            else if(type=="reply")
            {
                _busy=false;
                if(!Convert.ToBoolean(message["ok"])) { string error=TextOf(message,"error");_status.Text=error;AppendLog(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | ERROR | "+error); }
            }
            UpdateButtons();
        }
        private void Send(string command,Dictionary<string,object> config=null)
        {
            try { _busy=true;UpdateButtons();_engine.Send(command,config); }
            catch(Exception ex) { _busy=false;UpdateButtons();ShowError(ex.Message); }
        }
        private void UpdateButtons()
        {
            _start.Enabled=_ready&&!_running&&!_busy&&_pdfEnabled.Checked;
            _stop.Enabled=_ready&&_running&&!_busy;
            _configure.Enabled=_ready&&!_running&&!_busy;
            _pdfEnabled.Enabled=_ready&&!_busy;
        }
        private void AppendLog(string line)
        {
            // 最新时间和消息同时从顶部开始显示，不把最后一行碎片当成最新日志。
            int first=_logs.GetCharIndexFromPosition(new Point(2,2));bool follow=first<3;
            _logs.Select(0,0);_logs.SelectionColor=Color.FromArgb(24,48,71);_logs.SelectedText=line+Environment.NewLine+Environment.NewLine;
            _logs.Select(0,Math.Min(19,line.Length));_logs.SelectionColor=Color.Teal;
            if(_logs.TextLength>250000) { _logs.Select(200000,_logs.TextLength-200000);_logs.SelectedText=""; }
            _logs.SelectionStart=follow?0:Math.Min(first+line.Length+4,_logs.TextLength);_logs.SelectionLength=0;_logs.ScrollToCaret();
        }
        private void ConfigurePdf()
        {
            using(var dialog=new PdfSettingsForm(_config))
                if(dialog.ShowDialog(this)==DialogResult.OK)Send("set_config",dialog.Config);
        }
        private void ShowError(string message) { TraceLog.Write(message);MessageBox.Show(this,message,"Windows效率助手",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true;Hide();return; }
            base.OnFormClosing(e);
        }
    }
}

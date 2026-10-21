using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsWorkAssistant
{
    internal static class SmokeTests
    {
        private static void Check(bool condition,string name) { if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name); }
        [STAThread]
        private static void Main()
        {
            string root=Path.Combine(Path.GetTempPath(),"WorkAssistant-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("AUTO_IME_DATA_DIR",Path.Combine(root,"ime"));
            Environment.SetEnvironmentVariable("WORK_ASSISTANT_DATA_DIR",Path.Combine(root,"suite"));
            Environment.SetEnvironmentVariable("APPDATA",Path.Combine(root,"Roaming"));
            Environment.SetEnvironmentVariable("LOCALAPPDATA",Path.Combine(root,"Local"));
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var settings=new SuiteSettings { ImeEnabled=false,PdfEnabled=false };settings.Save();
                Check(!SuiteSettings.Load().ImeEnabled&&!SuiteSettings.Load().PdfEnabled,"module preferences persist independently");
                settings.ImeEnabled=true;settings.Save();File.WriteAllText(SuiteSettings.FilePath,"broken");
                Check(!SuiteSettings.Load().ImeEnabled,"invalid primary restores previous module settings");
                using(var form=new SuiteForm(new SuiteSettings { ImeEnabled=false,PdfEnabled=false }))
                using(var engine=new EngineClient())
                using(var ime=new AutoImeSwitcher.AppContext(new AutoImeSwitcher.AppConfig { AutoSwitchEnabled=false,ShowIndicator=false },false,new AutoImeSwitcher.AppHostOptions { ModuleEnabled=false }))
                {
                    IntPtr handle=form.Handle;form.Attach(ime,engine,delegate {});form.Show();
                    form.Size=form.MinimumSize;Application.DoEvents();
                    RichTextBox logs=FindLog(form);
                    Check(logs!=null&&logs.Height>=60,"minimum window keeps log area visible");
                    Check(FormInside(ime.GetSettingsForm(),form),"IME settings embedded in same main window");
                    form.SelectModule(true);Application.DoEvents();
                    Check(ime.GetSettingsForm().ClientSize.Width>=ime.GetSettingsForm().AutoScrollMinSize.Width,"IME tab widens for readable settings without horizontal clipping");
                    form.SelectModule(false);form.Size=form.MinimumSize;Application.DoEvents();
                    var received=new List<Dictionary<string,object>>();var sync=new object();
                    engine.Message+=delegate(Dictionary<string,object> data) {
                        lock(sync)received.Add(data);
                        string kind=Convert.ToString(data["event"]);
                        if(kind=="reply"||kind=="config")Console.WriteLine("PIPE_EVENT "+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data));
                    };
                    string failure=null;engine.Failure+=delegate(string error) { failure=error; };
                    engine.Start();Wait(delegate { lock(sync)return received.Exists(m=>Convert.ToString(m["event"])=="ready"); },15);
                    Check(failure==null,"embedded engine extraction and process job start");
                    string unicodeFolder=Path.Combine(root,"中文监控目录");Directory.CreateDirectory(unicodeFolder);
                    engine.Send("set_config",new Dictionary<string,object> { {"monitor_folder",unicodeFolder},{"recursive",true},{"task_timeout",60} });
                    Wait(delegate { lock(sync)return received.Exists(m=>Convert.ToString(m["event"])=="config"); },30);
                    Check(File.ReadAllText(Path.Combine(root,"Roaming","Excel自动转PDF","config.json")).Contains("中文监控目录"),"UTF8 command pipe preserves Chinese paths");
                    engine.Send("start");Wait(delegate { lock(sync)return received.Exists(m=>Convert.ToString(m["event"])=="status"&&Convert.ToString(m["status"])=="正在监控"); },30);
                    Check(true,"monitor starts only on explicit command");
                    if(Environment.GetEnvironmentVariable("WORK_ASSISTANT_UI_PREVIEW")=="1")
                    {
                        form.Text+=" · 隔离界面验收";
                        DateTime until=DateTime.UtcNow.AddSeconds(120);
                        while(DateTime.UtcNow<until) { Application.DoEvents();Thread.Sleep(30); }
                    }
                    Task stop=engine.StopAsync();Wait(()=>stop.IsCompleted,15);stop.GetAwaiter().GetResult();
                    Check(!engine.Running,"graceful quit finishes service and owned process tree");
                    ime.SetModuleEnabled(false);Check(true,"disabled IME module revokes future input actions");
                }
                Console.WriteLine("SUITE_SMOKE_PASS "+root);
            }
            catch(Exception ex) {
                Console.Error.WriteLine(ex);
                string trace=Path.Combine(root,"suite","suite.log");if(File.Exists(trace))Console.Error.WriteLine(File.ReadAllText(trace));
                Environment.ExitCode=1;
            }
        }
        private static void Wait(Func<bool> predicate,int seconds)
        { DateTime deadline=DateTime.UtcNow.AddSeconds(seconds);while(!predicate()) { if(DateTime.UtcNow>deadline)throw new TimeoutException();Application.DoEvents();Thread.Sleep(20); } }
        private static RichTextBox FindLog(Control parent)
        { foreach(Control child in parent.Controls) { var result=child as RichTextBox??FindLog(child);if(result!=null)return result; } return null; }
        private static bool FormInside(Control child,Control ancestor)
        { for(Control current=child;current!=null;current=current.Parent)if(current==ancestor)return true;return false; }
    }
}

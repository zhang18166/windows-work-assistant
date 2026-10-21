using System;
using System.IO;
using System.Web.Script.Serialization;

namespace WindowsWorkAssistant
{
    /// <summary>只保存整合层模块开关；两份组件配置继续使用各自已有目录。</summary>
    public sealed class SuiteSettings
    {
        public bool ImeEnabled { get; set; }
        public bool PdfEnabled { get; set; }
        public SuiteSettings() { ImeEnabled = true; PdfEnabled = true; }
        public static string DirectoryPath { get { return Environment.GetEnvironmentVariable("WORK_ASSISTANT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsWorkAssistant"); } }
        public static string FilePath { get { return Path.Combine(DirectoryPath, "settings.json"); } }
        public static SuiteSettings Load()
        {
            foreach (string path in new[] { FilePath, FilePath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    if (new FileInfo(path).Length > 65536) throw new IOException("整合配置过大");
                    SuiteSettings value = new JavaScriptSerializer().Deserialize<SuiteSettings>(File.ReadAllText(path));
                    if (value != null) return value;
                }
                catch (Exception ex) { TraceLog.Write("读取整合配置失败：" + ex.Message); }
            }
            return new SuiteSettings();
        }
        public void Save()
        {
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                { writer.Write(new JavaScriptSerializer().Serialize(this)); writer.Flush(); ((FileStream)writer.BaseStream).Flush(true); }
                if (File.Exists(FilePath))
                {
                    bool valid=false;
                    try { valid=new JavaScriptSerializer().Deserialize<SuiteSettings>(File.ReadAllText(FilePath))!=null; } catch { }
                    // 不让损坏的主配置覆盖上一份有效备份。
                    File.Replace(temporary, FilePath, valid ? FilePath+".bak" : null);
                }
                else File.Move(temporary, FilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static class TraceLog
    {
        private static readonly object Sync = new object();
        public static void Write(string text)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(SuiteSettings.DirectoryPath);
                    string path = Path.Combine(SuiteSettings.DirectoryPath, "suite.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 2*1024*1024)
                    { File.Copy(path, path+".1", true); File.Delete(path); }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + text + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}

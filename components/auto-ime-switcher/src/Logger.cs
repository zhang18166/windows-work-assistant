using System;
using System.IO;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 极简运行日志（V1.0.6 增加滚动归档）。
    ///
    /// 默认只记录切换与异常，不做分级。开机自启 + 高频切换下 run.log 会无限增长，
    /// 超过 MaxFileBytes 就整体滚到 run.log.1（只保留一代），防止数月后变成几十 MB。
    /// </summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static readonly string DirPath =
            Environment.GetEnvironmentVariable("AUTO_IME_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AutoImeSwitcher");
        private static readonly string FilePath = Path.Combine(DirPath, "run.log");
        private static bool _enabled = true;

        /// <summary>日志文件超过 2MB 就归档一轮，只保留上一代。</summary>
        private const long MaxFileBytes = 2 * 1024 * 1024;

        public static bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        public static string LogFilePath
        {
            get { return FilePath; }
        }

        /// <summary>日志所在目录（设置界面“打开日志目录”用）。</summary>
        public static string LogDirectory
        {
            get { return DirPath; }
        }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            if (!_enabled) return;
            try
            {
                lock (Sync)
                {
                    if (!Directory.Exists(DirPath))
                        Directory.CreateDirectory(DirPath);
                    RotateIfTooBig();
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败绝不能影响主功能
            }
        }

        /// <summary>超限时把当前日志整体滚成 .1（只保留一代，循环覆盖）。</summary>
        private static void RotateIfTooBig()
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists || fi.Length <= MaxFileBytes) return;

                string archive = FilePath + ".1";
                File.Copy(FilePath, archive, true);
                File.WriteAllText(FilePath, string.Empty);
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [INFO ] 日志超过 " +
                    (MaxFileBytes / 1024 / 1024) + "MB，已归档到 run.log.1" + Environment.NewLine);
            }
            catch
            {
                // 归档失败不碍事：最多就是日志继续变大，下次再试
            }
        }
    }
}

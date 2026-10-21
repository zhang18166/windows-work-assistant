using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AutoImeSwitcher
{
    /// <summary>一条目标程序规则。进程名为必填，窗口标题包含为可选高级条件。</summary>
    [DataContract]
    public class TargetRule
    {
        [DataMember(Name = "processName")]
        public string ProcessName { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        /// <summary>可选。为空表示不限制窗口标题。</summary>
        [DataMember(Name = "titleContains")]
        public string TitleContains { get; set; }

        public TargetRule() { }

        public TargetRule(string processName, string displayName)
        {
            ProcessName = processName;
            DisplayName = displayName;
            TitleContains = string.Empty;
        }

        public string Describe()
        {
            if (string.IsNullOrWhiteSpace(TitleContains))
                return ProcessName;
            return ProcessName + " (标题含 \"" + TitleContains + "\")";
        }
    }

    [DataContract]
    public class AppConfig
    {
        [DataMember(Name = "autoSwitchEnabled")]
        public bool AutoSwitchEnabled { get; set; }

        [DataMember(Name = "rules")]
        public List<TargetRule> Rules { get; set; }

        [DataMember(Name = "showIndicator")]
        public bool ShowIndicator { get; set; }

        [DataMember(Name = "indicatorTopMost")]
        public bool IndicatorTopMost { get; set; }

        /// <summary>浮窗鼠标穿透：开启后鼠标事件穿过浮窗，点到下方窗口。</summary>
        [DataMember(Name = "indicatorClickThrough")]
        public bool IndicatorClickThrough { get; set; }

        private int _indicatorTransparencyPercent;
        /// <summary>仅背景透明度：0 背景不透明，100 背景全透明；文字和图标保持清晰。兼容旧配置，默认 30%。</summary>
        [DataMember(Name = "indicatorTransparencyPercent")]
        public int IndicatorTransparencyPercent
        {
            get { return _indicatorTransparencyPercent; }
            set { _indicatorTransparencyPercent = Math.Max(0, Math.Min(100, value)); }
        }

        [DataMember(Name = "runAtStartup")]
        public bool RunAtStartup { get; set; }

        [DataMember(Name = "indicatorX")]
        public int IndicatorX { get; set; }

        [DataMember(Name = "indicatorY")]
        public int IndicatorY { get; set; }

        /// <summary>
        /// 浮窗最后所在显示器的设备名（Screen.DeviceName，如 "\\.\DISPLAY1"）。
        /// 配合 IndicatorX/Y 使用：显示配置变化（拔掉副屏、改分辨率）后，
        /// 按“原屏幕还在不在”决定沿用还是回钳，不会每次启动都被拉到主屏右下角。
        /// 老配置没有这个字段时为 null，按坐标所在屏幕兜底。
        /// </summary>
        [DataMember(Name = "indicatorScreen")]
        public string IndicatorScreen { get; set; }

        /// <summary>离开目标程序后把输入法切回中文（仅“输入法开关 / Shift 键”方式有效）。</summary>
        [DataMember(Name = "restoreChineseOnLeave")]
        public bool RestoreChineseOnLeave { get; set; }

        /// <summary>
        /// 中英切换方式：0=切换键盘布局，1=按 Shift，2=按 CapsLock，3=直接开关输入法。
        /// 存 int 而不是枚举，方便老配置反序列化（枚举在 DataContractJson 里也是数字，但显式 int 更好读）。
        /// 默认用 3：它是唯一“可读真实状态 + 可写 + 可复核”的方式，不会像 Shift 那样积累偏差。
        /// </summary>
        [DataMember(Name = "switchMethod")]
        public int SwitchMethod { get; set; }

        public SwitchMethod SwitchMethodValue
        {
            get
            {
                switch (SwitchMethod)
                {
                    case (int)AutoImeSwitcher.SwitchMethod.KeyboardLayout:
                        return AutoImeSwitcher.SwitchMethod.KeyboardLayout;
                    case (int)AutoImeSwitcher.SwitchMethod.ShiftKey:
                        return AutoImeSwitcher.SwitchMethod.ShiftKey;
                    case (int)AutoImeSwitcher.SwitchMethod.CapsLockKey:
                        return AutoImeSwitcher.SwitchMethod.CapsLockKey;
                    default:
                        return AutoImeSwitcher.SwitchMethod.ImeOpenClose;
                }
            }
            set { SwitchMethod = (int)value; }
        }

        public AppConfig() { SetDefaults(); }
        public void RestoreIndicatorVisibility()
        {
            ShowIndicator = true;
            IndicatorTopMost = true;
            IndicatorTransparencyPercent = 30;
        }
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }
        private void SetDefaults()
        {
            AutoSwitchEnabled = true;
            Rules = new List<TargetRule>();
            ShowIndicator = true;
            IndicatorTopMost = true;
            IndicatorTransparencyPercent = 30;
            IndicatorX = IndicatorY = -1;
            SwitchMethod = (int)AutoImeSwitcher.SwitchMethod.ImeOpenClose;
        }

        // ------------------------------------------------------------
        private static readonly string DirPath =
            Environment.GetEnvironmentVariable("AUTO_IME_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AutoImeSwitcher");

        private static readonly string FilePath = Path.Combine(DirPath, "config.json");

        public static string ConfigFilePath
        {
            get { return FilePath; }
        }

        /// <summary>首次运行时的预设规则。完全可配置，用户可自由删除。</summary>
        public static List<TargetRule> DefaultRules()
        {
            return new List<TargetRule>
            {
                new TargetRule("SLDWORKS.exe", "SolidWorks"),
                new TargetRule("ugraf.exe", "Siemens NX")
            };
        }

        /// <summary>去掉开头的 UTF-8 BOM（EF BB BF），没有则原样返回。</summary>
        private static byte[] StripBom(byte[] data)
        {
            if (data == null || data.Length < 3) return data ?? new byte[0];
            if (data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                var trimmed = new byte[data.Length - 3];
                Buffer.BlockCopy(data, 3, trimmed, 0, trimmed.Length);
                return trimmed;
            }
            return data;
        }

        /// <summary>
        /// 最近一次 Load() 是否因为配置损坏而回退了默认配置。
        /// AppContext 在启动时检查它，弹一次气球提示用户（而不是静默丢失全部规则）。
        /// </summary>
        public static bool LastLoadFailed { get; private set; }

        public static AppConfig Load()
        {
            LastLoadFailed = false;
            try
            {
                if (File.Exists(FilePath))
                {
                    // 读成字节后剥掉 UTF-8 BOM：DataContractJsonSerializer 不接受 BOM，
                    // 否则每次启动都会反序列化失败并退回默认配置，用户设置全部丢失。
                    byte[] raw = File.ReadAllBytes(FilePath);
                    var utf8 = new UTF8Encoding(false);
                    string json = utf8.GetString(StripBom(raw));

                    // 缺失字段由 OnDeserializing 初始化；显式保存的 false / 0 仍按原配置读取。

                    using (var ms = new MemoryStream(utf8.GetBytes(json), false))
                    {
                        var ser = new DataContractJsonSerializer(typeof(AppConfig));
                        var cfg = (AppConfig)ser.ReadObject(ms);
                        if (cfg != null)
                        {
                            if (cfg.Rules == null) cfg.Rules = new List<TargetRule>();
                            return cfg;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastLoadFailed = true;
                Logger.Warn("读取配置失败，使用默认配置: " + ex.Message);
                PreserveBrokenConfig();
            }

            var fresh = new AppConfig();
            fresh.Rules = DefaultRules();
            return fresh;
        }

        /// <summary>
        /// 把损坏的 config.json 改名留档（config.json.bad-时间戳），
        /// 否则下一次 Save 就会用默认值把它覆盖掉，用户再也没法找回原来的规则。
        /// </summary>
        private static void PreserveBrokenConfig()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string bad = Path.Combine(DirPath, "config.json.bad-" + stamp);
                int n = 1;
                while (File.Exists(bad))
                {
                    bad = Path.Combine(DirPath, "config.json.bad-" + stamp + "-" + n);
                    n++;
                }
                File.Move(FilePath, bad);
                Logger.Warn("已把损坏的配置另存为: " + bad);
            }
            catch (Exception ex)
            {
                Logger.Warn("保留损坏配置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 原子写入（V1.0.6）。先写临时文件再整体替换，避免“写一半断电/崩溃”
        /// 把 config.json 写成截断的坏 JSON——那种情况下旧实现会在下次启动
        /// 静默回退默认配置，并在用户下一次保存时把默认值覆盖上去，全部规则丢失。
        /// </summary>
        public void Save()
        {
            try
            {
                if (!Directory.Exists(DirPath))
                    Directory.CreateDirectory(DirPath);

                string json;
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(AppConfig));
                    ser.WriteObject(ms, this);
                    // 用不带 BOM 的 UTF8 写盘：带 BOM 会让下次读取失败。
                    var utf8NoBom = new UTF8Encoding(false);
                    json = utf8NoBom.GetString(ms.ToArray());
                }

                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(FilePath))
                {
                    try
                    {
                        // File.Replace 是原子的：目标要么是旧内容，要么是新内容，不会出现半截
                        File.Replace(tmp, FilePath, FilePath + ".bak");
                    }
                    catch
                    {
                        // 个别情况（目标被占用等）Replace 会失败，退回拷贝覆盖：
                        // 截断风险只出现在拷贝本身的瞬间，比直接写目标文件仍然小得多
                        File.Copy(tmp, FilePath, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, FilePath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置失败: " + ex.Message);
            }
        }
    }
}


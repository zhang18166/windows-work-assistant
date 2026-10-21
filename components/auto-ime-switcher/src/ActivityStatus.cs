using System;
using System.Drawing;
using System.IO;

namespace AutoImeSwitcher
{
    public sealed class ActivitySnapshot
    {
        public string ProcessName = "", DisplayName = "等待识别程序", ExePath = "";
        public string InputState = "未知", CurrentAction = "等待前台程序", LastAction = "尚无切换记录";
        public bool Enabled = true, Matched;
        public string ShortAction
        {
            get
            {
                if (!Enabled) return "已暂停";
                string action = CurrentAction ?? "";
                if (action.Contains("未验证")) return "未验证";
                if (action.Contains("失败")) return "失败";
                if (action.Contains("未知")) return "未知";
                if (action.Contains("未匹配")) return "未匹配";
                if (action.Contains("已是英文")) return "原本英文";
                if (action.Contains("大写已开启")) return "原本锁定";
                if (action.Contains("已切换英文")) return "已切换";
                if (action.Contains("已开启大写")) return "已锁定";
                if (action.Contains("保留当前选择")) return "状态变化";
                if (action.Contains("跳过") || action.Contains("失效")) return "已跳过";
                if (action.Contains("未安装")) return "缺少布局";
                if (action.Contains("正在")) return "切换中";
                return "等待";
            }
        }
        public string DetailsText
        {
            get
            {
                return "自动输入法助手 " + Program.VersionText + "\n" +
                    "当前程序：" + DisplayName + "\n进程：" + ProcessName +
                    "\n规则：" + (Matched ? "已匹配" : "未匹配") +
                    "\n自动切换：" + (Enabled ? "已启用" : "已暂停") +
                    "\n输入：" + ActivityJournal.FriendlyState(InputState) +
                    "\n当前：" + CurrentAction + "\n最近：" + LastAction;
            }
        }
        internal string Signature
        { get { return ProcessName + "|" + DisplayName + "|" + ExePath + "|" + InputState + "|" + CurrentAction + "|" + LastAction + "|" + Enabled + "|" + Matched; } }
    }

    internal sealed class ActivityJournal
    {
        private readonly object _sync = new object();
        private long _epoch;
        private IntPtr _window;
        private uint _pid;
        private string _current = "等待前台程序", _last = "尚无切换记录";
        private SwitchResult? _result;
        private SwitchMethod _method;
        internal void Begin(long epoch, ForegroundInfo info, bool enabled, bool matched)
        {
            lock (_sync)
            {
                _epoch = epoch; _window = info.Hwnd; _pid = info.Pid;
                _result = null;
                _current = !enabled ? "自动切换已暂停" : matched ? "已匹配规则，等待切换" : "未匹配规则，不执行切换";
            }
        }
        internal void Running(long epoch)
        { lock (_sync) { if (_epoch == epoch) _current = "正在检查并切换…"; } }
        internal void Complete(long epoch, string process, SwitchMethod method, SwitchResult result)
        {
            lock (_sync)
            {
                if (_epoch != epoch) return;
                _result = result; _method = method;
                _current = DescribeResult(method, result);
                _last = DateTime.Now.ToString("HH:mm:ss") + " · " + process + " · " + _current;
            }
        }
        internal void Restored(long epoch, string process, bool restored)
        {
            lock (_sync)
            {
                if (_epoch != epoch) return;
                _last = DateTime.Now.ToString("HH:mm:ss") + " · " + process +
                    (restored ? " · 已恢复原中文状态" : " · 恢复已跳过或未验证");
            }
        }
        internal ActivitySnapshot Snapshot(ForegroundInfo info, bool enabled, bool matched, string input, string displayName)
        {
            lock (_sync)
            {
                string current = _current;
                if (_result == SwitchResult.Switched || _result == SwitchResult.AlreadyEnglish)
                {
                    if (input == "未知") current = "当前状态未知，未追加切换";
                    else if ((_method == SwitchMethod.CapsLockKey && input == "caps关") ||
                        (_method != SwitchMethod.CapsLockKey && input == "中"))
                        current = "输入状态已变化，保留当前选择";
                }
                return new ActivitySnapshot { ProcessName = info.ProcessName, DisplayName = displayName,
                    ExePath = info.ExePath, Enabled = enabled, Matched = matched, InputState = input,
                    CurrentAction = !enabled ? "自动切换已暂停" :
                        info.Hwnd == _window && info.Pid == _pid ? current : "前台已变化，等待识别",
                    LastAction = _last };
            }
        }
        internal static string DescribeResult(SwitchMethod method, SwitchResult result)
        {
            switch (result)
            {
                case SwitchResult.Switched: return method == SwitchMethod.CapsLockKey ? "已开启大写锁定（已验证）" : "已切换英文（已验证）";
                case SwitchResult.AlreadyEnglish: return method == SwitchMethod.CapsLockKey ? "大写已开启，未执行切换" : "已是英文，未执行切换";
                case SwitchResult.RequestedButNotVerified: return "已发出请求，未验证成功";
                case SwitchResult.UnknownState: return "状态未知，已跳过或停止操作";
                case SwitchResult.SendFailed: return "切换发送失败";
                case SwitchResult.NoEnglishLayout: return "未安装英文布局，未执行切换";
                case SwitchResult.NoWindow: return "目标窗口失效，未执行切换";
                default: return "窗口或按键变化，已跳过或停止操作";
            }
        }
        internal static string FriendlyState(string input)
        {
            switch (input)
            {
                case "EN": return "EN  英文";
                case "中": return "中  中文";
                case "CAPS": return "CAPS  大写锁定";
                case "caps关": return "大写锁定关闭";
                case "暂停": return "自动切换已暂停";
                default: return "输入状态未知";
            }
        }
    }

    // 只保留当前程序的图标缓存；图标读取失败时显示通用程序图标。
    internal sealed class ProgramIconCache : IDisposable
    {
        private string _path;
        private Bitmap _image;
        internal Bitmap CopyFor(string path)
        {
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                if (_image != null) _image.Dispose();
                _image = null; _path = path;
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        using (Icon icon = Icon.ExtractAssociatedIcon(path))
                            if (icon != null) _image = icon.ToBitmap();
                }
                catch { }
            }
            return _image == null ? null : (Bitmap)_image.Clone();
        }
        public void Dispose() { if (_image != null) { _image.Dispose(); _image = null; } }
    }
}

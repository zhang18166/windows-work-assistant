using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoImeSwitcher
{
    /// <summary>开机自动启动（写入当前用户的 Run 注册表项，不需要管理员权限）。</summary>
    public static class AutostartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string ValueName = "AutoImeSwitcher";
        private static string Arguments = "";
        /// <summary>整合宿主使用自己的一个启动项；独立版不调用此方法。</summary>
        public static void ConfigureHost(string valueName, string arguments)
        {
            if (string.IsNullOrWhiteSpace(valueName)) throw new ArgumentException("valueName");
            ValueName = valueName; Arguments = arguments ?? "";
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取开机启动状态失败: " + ex.Message);
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        Logger.Warn("无法打开 Run 注册表项");
                        return;
                    }
                    if (enabled)
                    {
                        string exe = Application.ExecutablePath;
                        key.SetValue(ValueName, "\"" + exe + "\"" + (Arguments.Length == 0 ? "" : " " + Arguments));
                        Logger.Info("已设置开机启动: " + exe);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                        Logger.Info("已取消开机启动");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("设置开机启动失败: " + ex.Message);
                throw;
            }
        }
    }
}

using System;
namespace AutoImeSwitcher
{
    /// <summary>可选的宿主适配。没有宿主时保持独立软件的托盘、设置与退出入口。</summary>
    public sealed class AppHostOptions
    {
        public bool ModuleEnabled = true;
        public Action ShowSettings;
        public Action RequestExit;
    }
}

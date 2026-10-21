using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    internal static class UiPreview
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Logger.Enabled = false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = args[0]; Directory.CreateDirectory(output);
            var sample = new ActivitySnapshot { DisplayName = "SOLIDWORKS", ProcessName = "SLDWORKS.exe",
                Matched = true, Enabled = true, InputState = "EN", CurrentAction = "已切换英文（已验证）",
                LastAction = "10:28:06 · SLDWORKS.exe · 已切换英文" };
            var config = new AppConfig { Rules = AppConfig.DefaultRules() };
            using (var form = new MainForm(config, delegate { }, null, delegate { }))
            using (var cache = new ProgramIconCache())
            using (var appIcon = cache.CopyFor(args.Length > 1 ? args[1] : ""))
            {
                // 不显示窗口、不启动助手上下文、不读取/保存用户配置、不安装输入钩子。
                form.LoadFromConfig(); form.SetActivity(sample);
                IntPtr handle = form.Handle; GC.KeepAlive(handle);
                using (var settings = new Bitmap(form.Width, form.Height))
                using (var card = ActivityCardPainter.Render(sample, appIcon, Theme.Scale, config.IndicatorTransparencyPercent))
                using (var canvas = new Bitmap(settings.Width + Math.Max(card.Width, 420) + 60, Math.Max(settings.Height + 64, 610)))
                using (var font = new Font("Microsoft YaHei UI", 11))
                {
                    form.DrawToBitmap(settings, new Rectangle(Point.Empty, settings.Size));
                    // 隐藏窗体的 WM_PRINT 不会遍历不可见子控件；逐个绘制生产控件，仍不显示或激活窗口。
                    Point client = form.PointToScreen(Point.Empty);
                    Point origin = new Point(client.X - form.Left, client.Y - form.Top);
                    using (Graphics composed = Graphics.FromImage(settings))
                        foreach (Control control in form.Controls)
                            using (var child = new Bitmap(control.Width, control.Height))
                            {
                                control.DrawToBitmap(child, new Rectangle(Point.Empty, control.Size));
                                var numeric = control as NumericUpDown;
                                if (numeric != null)
                                    using (Graphics numericGraphics = Graphics.FromImage(child))
                                        TextRenderer.DrawText(numericGraphics, numeric.Value.ToString("0"), numeric.Font,
                                            new Rectangle(3, 0, Math.Max(1, child.Width - 24), child.Height), numeric.ForeColor,
                                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                                composed.DrawImageUnscaled(child, origin.X + control.Left, origin.Y + control.Top);
                            }
                    settings.Save(Path.Combine(output, "settings.png"), ImageFormat.Png);
                    card.Save(Path.Combine(output, "activity-card.png"), ImageFormat.Png);
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.FromArgb(233, 237, 243));
                        g.DrawString(Program.VersionText + " 界面预览 · 示例状态，未执行实际切换", font, Brushes.DarkSlateGray, 20, 14);
                        g.DrawImageUnscaled(settings, 20, 48);
                        int cardX = settings.Width + 40;
                        g.DrawString("右下角迷你浮窗", font, Brushes.DarkSlateGray, cardX, 65);
                        g.DrawImageUnscaled(card, cardX, 98);
                        g.DrawString("悬停查看程序、匹配规则和最近动作\n穿透开启时可从托盘查看详细状态\n\n重置入口：设置 / 托盘 → 找回浮窗", font, Brushes.DarkSlateGray, cardX, 120 + card.Height);
                        int rowY = 300;
                        foreach (int percent in new[] { 0, 50, 100 })
                        {
                            g.DrawString("背景透明度 " + percent + "%", font, Brushes.DarkSlateGray, cardX, rowY);
                            using (var example = ActivityCardPainter.Render(sample, appIcon, Theme.Scale, percent))
                            {
                                g.FillRectangle(Brushes.White, cardX, rowY + 30, example.Width, example.Height);
                                g.DrawImageUnscaled(example, cardX, rowY + 30);
                            }
                            rowY += 110;
                        }
                    }
                    canvas.Save(Path.Combine(output, "overview.png"), ImageFormat.Png);
                    Console.WriteLine("Settings client physical size: " + form.ClientSize + "; scale=" + Theme.Scale);
                }
                foreach (float scale in new[] { 1.5f, 2f })
                {
                    typeof(MainForm).GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, scale);
                    typeof(MainForm).GetMethod("BuildUi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                    form.LoadFromConfig(); form.SetActivity(sample);
                    foreach (Control control in form.Controls)
                        if (control.Right > form.ClientSize.Width || control.Bottom > form.ClientSize.Height || control.Left < 0 || control.Top < 0)
                            throw new Exception("Control outside client bounds at scale " + scale + ": " + control.Text);
                    Console.WriteLine("Layout bounds passed at scale " + scale);
                }
            }
        }
    }
}

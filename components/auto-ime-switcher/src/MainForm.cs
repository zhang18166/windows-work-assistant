using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 设置主界面（V1.0.4 扁平化重构，V1.0.6 修订）。
    ///
    /// 视觉：页面浅灰底 + 白色圆角卡片，卡片之间靠留白分区，不再用 GroupBox 的凹陷边框
    /// 和一堆分割线堆叠。开关、按钮、分段选择器、规则列表全部自绘。
    ///
/// 尺寸：所有坐标按 96dpi 逻辑像素书写，统一乘 _scale。
/// 构造期 _scale 取 Theme.Scale（屏幕 DC 物理 DPI）：CreateGraphics().DpiX 对
/// 未显示窗口不稳定、DeviceDpi 在 PMv2 下恒为 96，两者都不可用于构造期。
/// 显示后跨屏拖动靠 OnDpiChanged 按 Theme.ScaleFor(this) 整体重建（中-6）。
    ///
    /// 交互：点关闭 = 收进托盘；整行都能点；改动即时保存并立即生效；
    /// 规则列表双击可编辑"标题包含"条件；"退出程序"与托盘退出走同一条路径（低-4）。
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly Action _onConfigChanged;
        private readonly Action _onExit;
        private readonly Action _onResetIndicator;
        private readonly Action _onAppearanceChanged;

        private FlatSwitch swAuto;
        private FlatSegmented segMethod;
        private Label lblMethodDesc;
        private FlatRuleList listRules;
        private FlatButton btnPick, btnBrowse, btnEdit, btnRemove, btnExit;
        private ToggleRow rowShow, rowTop, rowThrough, rowStartup, rowRestore;
        private Label lblFore, lblLayout;
        private FlatButton btnRefresh, btnLog;
        private Timer diagTimer;
        private int _diagnosticBusy;
        private ActivitySnapshot _lastActivity;
        private TrackBar transparencySlider;
        private NumericUpDown transparencyValue;
        private Timer _appearanceSaveTimer;
        private bool _updatingTransparency, _appearanceSavePending;
        private readonly ToolTip _appearanceTip = new ToolTip();

        private float _scale = 1f;
        private bool _loading;
        private bool _applyingAutoStart;   // 回滚开机启动勾选时防重入
        private readonly List<Rectangle> _cards = new List<Rectangle>();
        private readonly List<Rectangle> _lines = new List<Rectangle>();

        private int S(int v)
        {
            return (int)Math.Round(v * _scale);
        }

        public MainForm(AppConfig config, Action onConfigChanged, Action onExit, Action onResetIndicator = null, Action onAppearanceChanged = null)
        {
            _config = config;
            _onConfigChanged = onConfigChanged;
            _onExit = onExit;
            _onResetIndicator = onResetIndicator;
            _onAppearanceChanged = onAppearanceChanged;
            _appearanceSaveTimer = new Timer { Interval = 250 };
            _appearanceSaveTimer.Tick += delegate {
                _appearanceSaveTimer.Stop();
                if (_appearanceSavePending) { _config.Save(); _appearanceSavePending = false; }
            };

            // 构造期取缩放：用 Theme.Scale（屏幕 DC 的物理 DPI，取主屏）而不是
            // Theme.ScaleFor(this)。两个原因：
            // 1) CreateGraphics().DpiX 对"已建句柄但未显示"的窗口在本机实测不稳
            //    （同一构建两次运行分别返回 96 与 144），构造期用它有偶发按 96dpi
            //    排版的风险；DeviceDpi 在 PMv2 下恒为 96，也不可用。
            // 2) 窗口是 CenterScreen，本来就开在主屏，主屏 DPI 就是正确值。
            // 窗口显示后跨屏拖动由 OnDpiChanged 用 Theme.ScaleFor(this) 整体重建（中-6），
            // 那时窗口已显示，CreateGraphics 取值稳定。
            _scale = Theme.Scale;
            if (_scale < 0.5f || _scale > 4f) _scale = 1f;

            BuildUi();
            LoadFromConfig();
            RefreshDiagnostics();

            diagTimer = new Timer();
            diagTimer.Interval = 2000;
            diagTimer.Tick += delegate (object s, EventArgs e)
            {
                if (Visible && !IsDisposed) RefreshDiagnostics();
            };
            diagTimer.Start();
        }

        // ------------------------------------------------------------ 构建界面
        private void BuildUi()
        {
            _appearanceTip.RemoveAll();
            AutoScrollPosition = Point.Empty;
            while (Controls.Count > 0) { Control c = Controls[0]; Controls.RemoveAt(0); c.Dispose(); }
            _cards.Clear(); _lines.Clear();
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Page; Font = Theme.Ui;
            Text = "自动输入法助手 " + Program.VersionText;
            FormBorderStyle = TopLevel ? FormBorderStyle.FixedDialog : FormBorderStyle.None;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            try { if (Icon == null) Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            int mx = S(16), cardW = S(448), ix = S(28), iw = S(424), y = S(10);
            AddLabel("自动输入法助手", Theme.Title, mx, y, S(300), S(27), Theme.Page);
            var version = AddLabel(Program.VersionText, Theme.Ui, mx + cardW - S(100), y, S(100), S(27), Theme.Page);
            version.TextAlign = ContentAlignment.MiddleRight; version.ForeColor = Theme.Sub;
            y += S(30);

            int top = y, cy = top + S(10);
            AddLabel("启用自动切换", Theme.Head, ix, cy, iw - S(60), S(22), Theme.CardBg);
            swAuto = new FlatSwitch { Location = new Point(ix + iw - S(44), cy), Size = new Size(S(44), S(24)) };
            swAuto.CheckedChanged += delegate { if (!_loading) SaveAndNotify(); }; Controls.Add(swAuto);
            cy += S(28);
            segMethod = new FlatSegmented { Location = new Point(ix, cy), Size = new Size(iw, S(30)),
                Items = new[] { "输入法开关", "Shift 键", "CapsLock", "键盘布局" } };
            segMethod.SelectedIndexChanged += delegate {
                if (_loading) return; _config.SwitchMethodValue = ImeController.MethodAt(segMethod.SelectedIndex);
                UpdateMethodDesc(); SaveAndNotify();
            }; Controls.Add(segMethod); cy += S(36);
            lblMethodDesc = AddLabel("", Theme.Small, ix, cy, iw, S(30), Theme.CardBg); lblMethodDesc.ForeColor = Theme.Sub;
            cy += S(30); y = cy + S(10); _cards.Add(new Rectangle(mx, top, cardW, y - top)); y += S(8);

            top = y; cy = top + S(10);
            AddLabel("自动英文程序", Theme.Head, ix, cy, iw, S(22), Theme.CardBg); cy += S(24);
            listRules = new FlatRuleList { Location = new Point(ix, cy), Size = new Size(iw, S(86)) };
            listRules.ItemActivated += delegate { EditSelected(); }; Controls.Add(listRules); cy += S(92);
            btnPick = AddButton("选择窗口", ix, cy, 86, 26, false, AddByPickRunning);
            btnBrowse = AddButton("浏览 exe", ix + S(92), cy, 80, 26, false, AddByBrowse);
            btnEdit = AddButton("编辑", ix + S(178), cy, 44, 26, false, EditSelected);
            btnRemove = AddButton("删除", ix + S(228), cy, 44, 26, true, RemoveSelected);
            btnExit = AddButton("退出", ix + iw - S(50), cy, 50, 26, true,
                delegate { if (_onExit != null) _onExit(); });
            cy += S(26); y = cy + S(10); _cards.Add(new Rectangle(mx, top, cardW, y - top)); y += S(8);

            top = y; cy = top + S(10);
            AddLabel("浮窗与启动", Theme.Head, ix, cy, S(180), S(23), Theme.CardBg);
            var reset = AddButton("重置浮窗到右下角", ix + iw - S(132), cy - S(2), 132, 26, false,
                delegate { if (_onResetIndicator != null) _onResetIndicator(); });
            reset.Name = "ResetIndicatorButton"; reset.AccessibleName = "重置浮窗到右下角";
            cy += S(28);
            int colW = (iw - S(10)) / 2, col2 = ix + colW + S(10);
            rowShow = AddToggle("显示迷你浮窗", ix, cy, colW, 30);
            rowTop = AddToggle("始终置顶", col2, cy, colW, 30); cy += S(30);
            rowThrough = AddToggle("鼠标穿透", ix, cy, colW, 30);
            rowStartup = new ToggleRow { Text = "开机启动", Location = new Point(col2, cy), Size = new Size(colW, S(30)) };
            rowStartup.CheckedChanged += delegate { if (!_loading && !_applyingAutoStart) ApplyAutoStart(); }; Controls.Add(rowStartup); cy += S(30);
            rowRestore = AddToggle("离开后恢复原中文（检测到手动切换时跳过）", ix, cy, iw, 28); cy += S(28);
            AddLabel("背景透明度", Theme.Ui, ix, cy, S(76), S(28), Theme.CardBg);
            transparencySlider = new TrackBar { Name = "TransparencySlider", AccessibleName = "浮窗背景透明度（仅背景，文字和图标不变）",
                Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, TickStyle = TickStyle.None,
                AutoSize = false, BackColor = Theme.CardBg, Location = new Point(ix + S(76), cy),
                Size = new Size(iw - S(161), S(28)) };
            transparencyValue = new NumericUpDown { Name = "TransparencyValue", AccessibleName = "背景透明度百分比",
                Minimum = 0, Maximum = 100, DecimalPlaces = 0, Increment = 1,
                Location = new Point(ix + iw - S(73), cy + S(2)), Size = new Size(S(54), S(24)), Font = Theme.Ui };
            transparencySlider.ValueChanged += delegate { ChangeTransparency(transparencySlider.Value); };
            transparencyValue.ValueChanged += delegate { ChangeTransparency((int)transparencyValue.Value); };
            Controls.Add(transparencySlider); Controls.Add(transparencyValue);
            _appearanceTip.SetToolTip(transparencySlider, "0% 背景不透明；100% 背景全透明，文字和图标保持清晰。找回浮窗会恢复为 30%。");
            _appearanceTip.SetToolTip(transparencyValue, "0% 背景不透明；100% 背景全透明，文字和图标保持清晰。找回浮窗会恢复为 30%。");
            AddLabel("%", Theme.Ui, ix + iw - S(15), cy, S(15), S(28), Theme.CardBg);
            cy += S(30);
            y = cy + S(8); _cards.Add(new Rectangle(mx, top, cardW, y - top)); y += S(8);

            top = y; cy = top + S(8);
            lblFore = AddLabel("当前程序：等待识别", Theme.Small, ix, cy, iw - S(96), S(18), Theme.CardBg);
            lblFore.AutoEllipsis = true; lblFore.ForeColor = Theme.Sub;
            btnLog = AddButton("日志", ix + iw - S(90), cy - S(3), 42, 23, true, OpenLogDirectory);
            btnRefresh = AddButton("刷新", ix + iw - S(44), cy - S(3), 44, 23, true, RefreshDiagnostics);
            cy += S(21);
            lblLayout = AddLabel("输入状态：等待识别", Theme.Small, ix, cy, iw, S(18), Theme.CardBg);
            lblLayout.AutoEllipsis = true; lblLayout.ForeColor = Theme.Sub; cy += S(18);
            y = cy + S(8); _cards.Add(new Rectangle(mx, top, cardW, y - top)); y += S(12);
            AutoScrollMinSize = new Size(S(480), y);
            ClientSize = new Size(S(480), y);
            ResumeLayout(false);
        }

        private Label AddLabel(string text, Font font, int x, int y, int width, int height, Color background)
        {
            var label = new Label { Text = text, Font = font, ForeColor = Theme.Ink, BackColor = background,
                Location = new Point(x, y), Size = new Size(width, height), TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(label); return label;
        }
        private void ChangeTransparency(int value)
        {
            if (_loading || _updatingTransparency) return;
            _updatingTransparency = true;
            try
            {
                _config.IndicatorTransparencyPercent = value;
                transparencySlider.Value = _config.IndicatorTransparencyPercent;
                transparencyValue.Value = _config.IndicatorTransparencyPercent;
            }
            finally { _updatingTransparency = false; }
            // 外观变化不撤销切换任务；落盘合并，避免拖动滑块时频繁阻塞 UI。
            if (_onAppearanceChanged != null) _onAppearanceChanged();
            _appearanceSavePending = true;
            _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Start();
        }
        private FlatButton AddButton(string text, int x, int y, int width, int height, bool ghost, Action action)
        {
            var button = new FlatButton { Text = text, Location = new Point(x, y), Size = new Size(S(width), S(height)),
                ButtonStyle = ghost ? FlatButton.Style.Ghost : FlatButton.Style.Secondary };
            button.Click += delegate { action(); }; Controls.Add(button); return button;
        }
        private ToggleRow AddToggle(string text, int x, int y, int width, int height)
        {
            var row = new ToggleRow { Text = text, Location = new Point(x, y), Size = new Size(width, S(height)) };
            row.CheckedChanged += delegate { if (!_loading) SaveAndNotify(); }; Controls.Add(row); return row;
        }

        internal void SetActivity(ActivitySnapshot activity)
        {
            if (IsDisposed) return;
            _lastActivity = activity;
            lblFore.Text = "当前：" + activity.DisplayName + (activity.Matched ? " · 已匹配" : " · 未匹配");
            lblLayout.Text = ActivityJournal.FriendlyState(activity.InputState) + " · " + activity.CurrentAction;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(Theme.Page))
                g.FillRectangle(b, ClientRectangle);

            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Rectangle r in _cards)
            {
                var rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                using (var path = Theme.Rounded(rr, S(12)))
                {
                    using (var b = new SolidBrush(Theme.CardBg)) g.FillPath(b, path);
                    using (var p = new Pen(Theme.Hair, 1f)) g.DrawPath(p, path);
                }
            }
            foreach (Rectangle l in _lines)
            {
                using (var b = new SolidBrush(Theme.HairSoft)) g.FillRectangle(b, l);
            }
        }

        /// <summary>窗口比工作区还高时（有些缩放比例下会这样）先压回屏幕，再居中。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                if (Height > wa.Height) Height = wa.Height;
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            }
            catch { }
        }

        /// <summary>
        /// 窗口被拖到另一块缩放不同的显示器（中-6）。
        /// AutoScaleMode=None，WinForms 不会自动重排，这里按新 DPI 整体重建一次。
        /// 注意：.NET Framework 4.8 的 DpiChangedEventArgs 不带 DeviceDpiY
        /// （那是 .NET Core 的 API），所以直接用 Theme.ScaleFor(this) 取当前 DPI。
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            try
            {
                float ns = Theme.ScaleFor(this);
                if (Math.Abs(ns - _scale) < 0.01f) return;

                _scale = ns;
                BuildUi();
                LoadFromConfig();
                RefreshDiagnostics();
                Logger.Info("设置窗口 DPI 变为 " + (int)Math.Round(ns * 96f) + "，已按新缩放重建界面");
            }
            catch (Exception ex)
            {
                Logger.Warn("按新 DPI 重建界面失败: " + ex.Message);
            }
        }

        /// <summary>用资源管理器打开日志目录，并选中 run.log。</summary>
        private void OpenLogDirectory()
        {
            try
            {
                string log = Logger.LogFilePath;
                if (System.IO.File.Exists(log))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + log + "\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", Logger.LogDirectory);
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(this, "无法打开日志目录：\n" + Logger.LogDirectory + "\n\n" + ex.Message,
                        "自动输入法助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ 配置
        public void LoadFromConfig()
        {
            _loading = true;
            try
            {
                swAuto.Checked = _config.AutoSwitchEnabled;
                segMethod.SelectedIndex = ImeController.MethodIndex(_config.SwitchMethodValue);
                rowShow.Checked = _config.ShowIndicator;
                rowTop.Checked = _config.IndicatorTopMost;
                rowThrough.Checked = _config.IndicatorClickThrough;
                rowRestore.Checked = _config.RestoreChineseOnLeave;
                transparencySlider.Value = _config.IndicatorTransparencyPercent;
                transparencyValue.Value = _config.IndicatorTransparencyPercent;

                try { rowStartup.Checked = AutostartHelper.IsEnabled(); }
                catch { rowStartup.Checked = _config.RunAtStartup; }

                UpdateMethodDesc();
                RebuildRuleList();
            }
            finally
            {
                _loading = false;
            }
        }

        private void UpdateMethodDesc()
        {
            // 低-8：只有"输入法开关 / Shift 键"两种方式能自动恢复中文，
            // CapsLock / 键盘布局方式下直接置灰，免得用户开了却没效果。
            SwitchMethod m = _config.SwitchMethodValue;
            if (rowRestore != null)
            {
                bool supported = m == SwitchMethod.ImeOpenClose || m == SwitchMethod.ShiftKey;
                rowRestore.Enabled = supported;
                rowRestore.Checked = supported && rowRestore.Checked;
            }

            switch (m)
            {
                case SwitchMethod.ImeOpenClose:
                    lblMethodDesc.Text = "定向切英文；状态未知时跳过，不模拟按键。";
                    break;
                case SwitchMethod.ShiftKey:
                    lblMethodDesc.Text = "仅轻敲一次并复核；窗口或按键变化时取消。";
                    break;
                case SwitchMethod.CapsLockKey:
                    lblMethodDesc.Text = "只开大写锁定，不保证所有输入法均为英文。";
                    break;
                default:
                    lblMethodDesc.Text = "切到英语（美国）布局，需要先安装该键盘。";
                    break;
            }
        }

        private void RebuildRuleList()
        {
            var items = new List<FlatRuleList.Item>();
            foreach (TargetRule r in _config.Rules)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.ProcessName)) continue;
                items.Add(new FlatRuleList.Item
                {
                    Program = string.IsNullOrWhiteSpace(r.DisplayName) ? r.ProcessName : r.DisplayName,
                    Process = r.ProcessName,
                    Title = string.IsNullOrWhiteSpace(r.TitleContains) ? "（不限）" : r.TitleContains,
                    Tag = r
                });
            }
            listRules.SetItems(items);
        }

        private void SaveAndNotify()
        {
            _config.AutoSwitchEnabled = swAuto.Checked;
            _config.SwitchMethodValue = ImeController.MethodAt(segMethod.SelectedIndex);
            _config.ShowIndicator = rowShow.Checked;
            _config.IndicatorTopMost = rowTop.Checked;
            _config.IndicatorClickThrough = rowThrough.Checked;
            _config.RestoreChineseOnLeave = rowRestore.Checked;
            _config.RunAtStartup = rowStartup.Checked;
            if (_onConfigChanged != null) _onConfigChanged();
            _config.Save();
        }

        private void ApplyAutoStart()
        {
            bool want = rowStartup.Checked;

            // 中-5：以前 SetEnabled 抛异常后只弹提示，勾选状态和配置都还写着"已开启"，
            // 下次启动又以注册表为准显示"未开启"，开关来回横跳。
            // 现在一律以注册表的真实结果为准：失败就回滚勾选，配置也写真话。
            bool actual;
            try
            {
                AutostartHelper.SetEnabled(want);
                actual = AutostartHelper.IsEnabled();
                if (actual != want)
                    MessageBox.Show(this, "设置开机启动失败：请检查是否有其他安全软件拦截了注册表写入。",
                        "自动输入法助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                actual = AutostartHelper.IsEnabled();
                MessageBox.Show(this, "设置开机启动失败：" + ex.Message, "自动输入法助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _applyingAutoStart = true;
            try { rowStartup.Checked = actual; }
            finally { _applyingAutoStart = false; }

            _config.RunAtStartup = actual;
            _config.Save();
        }

        // ------------------------------------------------------------ 规则增删
        private void AddByBrowse()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要自动切换为英文的程序";
                dlg.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string name = Path.GetFileName(dlg.FileName);
                if (string.IsNullOrEmpty(name)) return;
                AddRule(name, StripExe(name));
            }
        }

        /// <summary>
        /// 打开「选择程序」窗口，从当前所有可见窗口里挑一个加入规则。
        ///
        /// 注意：不能靠"点击瞬间读前台窗口"——点按钮时前台已经变成设置窗口自己了，
        /// 永远只会识别到自己。必须弹出列表让用户主动选，并且提供「刷新」，
        /// 让用户可以先切到目标程序（SolidWorks / NX）再刷新抓取。
        /// </summary>
        private void AddByPickRunning()
        {
            using (var picker = new WindowPickerForm(this))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;

                WindowEntry sel = picker.Selected;
                if (sel == null || string.IsNullOrEmpty(sel.ProcessName)) return;

                AddRule(sel.ProcessName, StripExe(sel.ProcessName));
            }
        }

        /// <summary>
        /// 已知 Windows 系统进程（中-11）。把这些加进规则，一切桌面就关闭输入法，
        /// 行为难以理解且不易排查，添加前必须二次确认。
        /// </summary>
        private static readonly HashSet<string> SystemProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "explorer.exe", "svchost.exe", "taskhostw.exe", "taskhost.exe",
                "searchui.exe", "searchindexer.exe", "shellexperiencehost.exe",
                "runtimebroker.exe", "ctfmon.exe", "dwm.exe", "csrss.exe",
                "wininit.exe", "services.exe", "lsass.exe", "smss.exe",
                "winlogon.exe", "logonui.exe", "sihost.exe", "audiodg.exe",
                "conhost.exe", "applicationframehost.exe", "startmenuexperiencehost.exe",
                "textinputhost.exe", "systemsettingsbroker.exe", "useroobebroker.exe",
                "securityhealthsystray.exe", "msmpeng.exe", "spoolsv.exe",
                "wlanext.exe", "unsecapp.exe", "wermgr.exe"
            };

        private void AddRule(string processName, string displayName)
        {
            foreach (TargetRule r in _config.Rules)
            {
                if (r != null && string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "该程序已在列表中。", "自动输入法助手",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            // 中-11：系统进程加入前给一次明确警告（可取消）
            if (SystemProcesses.Contains(processName))
            {
                var answer = MessageBox.Show(this,
                    "「" + processName + "」是 Windows 系统进程。\n" +
                    "加入后，只要它位于前台就会被强制切成英文（例如整个桌面、任务栏、开始菜单），\n" +
                    "可能导致输入行为难以理解。\n\n确定仍要添加吗？",
                    "自动输入法助手", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (answer != DialogResult.OK) return;
            }

            _config.Rules.Add(new TargetRule(processName, displayName));
            if (_onConfigChanged != null) _onConfigChanged();
            _config.Save();
            RebuildRuleList();
        }

        /// <summary>双击列表行或点"编辑"：修改显示名与"标题包含"条件（中-9）。</summary>
        private void EditSelected()
        {
            FlatRuleList.Item sel = listRules.SelectedItem;
            if (sel == null)
            {
                MessageBox.Show(this, "请先在列表里选中要编辑的程序。", "自动输入法助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var rule = sel.Tag as TargetRule;
            if (rule == null) return;

            using (var dlg = new RuleEditForm(rule))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(rule);
                if (_onConfigChanged != null) _onConfigChanged();
                _config.Save();
                RebuildRuleList();
            }
        }

        private void RemoveSelected()
        {
            FlatRuleList.Item sel = listRules.SelectedItem;
            if (sel == null)
            {
                MessageBox.Show(this, "请先在列表里选中要删除的程序。", "自动输入法助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var rule = sel.Tag as TargetRule;
            if (rule == null) return;

            // 低-6：删除不再无条件执行，先确认一次
            string label = string.IsNullOrWhiteSpace(rule.DisplayName)
                ? rule.ProcessName : rule.DisplayName + "（" + rule.ProcessName + "）";
            var answer = MessageBox.Show(this,
                "确定删除「" + label + "」吗？\n删除后该程序进入前台时不再自动切换。",
                "自动输入法助手", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            _config.Rules.Remove(rule);
            if (_onConfigChanged != null) _onConfigChanged();
            _config.Save();
            RebuildRuleList();
        }

        private static string StripExe(string n)
        {
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, n.Length - 4);
            return n;
        }

        // ------------------------------------------------------------ 诊断
        public void RefreshDiagnostics()
        {
            if (_lastActivity != null) { SetActivity(_lastActivity); return; }
            if (!IsHandleCreated || IsDisposed || System.Threading.Interlocked.Exchange(ref _diagnosticBusy, 1) != 0) return;
            SwitchMethod method = _config.SwitchMethodValue;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    ForegroundInfo info = ForegroundWatcher.GetForegroundInfo();
                    string title = info.Title.Length > 24 ? info.Title.Substring(0, 24) + "…" : info.Title;
                    string foreground = "前台程序：" + (info.Valid ? info.ProcessName : "-") +
                        (string.IsNullOrEmpty(title) ? "" : "  [" + title + "]");
                    string line = "布局 " + ImeController.DescribeLayoutShort(NativeMethods.GetKeyboardLayout(info.ThreadId));
                    bool unavailable = method == SwitchMethod.KeyboardLayout && !ImeController.EnglishLayoutAvailable;
                    if (unavailable) line += " · 未安装英语(美国)，此方式不可用";
                    IntPtr focus = ImeController.GetFocusWindowOfThread(info.ThreadId);
                    line += " · 输入状态 " + ImeController.DescribeState(focus == IntPtr.Zero ? info.Hwnd : focus, info.ThreadId, method);
                    if (!IsDisposed) BeginInvoke((Action)delegate {
                        if (IsDisposed || NativeMethods.GetForegroundWindow() != info.Hwnd || method != _config.SwitchMethodValue) return;
                        lblFore.Text = foreground;
                        lblLayout.Text = line;
                        lblLayout.ForeColor = unavailable ? Theme.Danger : Theme.Sub;
                    });
                }
                catch { /* 关闭窗口或权限不足时不阻塞输入，也不更新过期诊断。 */ }
                finally { System.Threading.Interlocked.Exchange(ref _diagnosticBusy, 0); }
            });
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关闭按钮 = 收进托盘，不退出程序
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _appearanceTip.Dispose();
                if (_appearanceSaveTimer != null)
                {
                    _appearanceSaveTimer.Stop(); _appearanceSaveTimer.Dispose(); _appearanceSaveTimer = null;
                    if (_appearanceSavePending) { _config.Save(); _appearanceSavePending = false; }
                }
                if (diagTimer != null) { diagTimer.Stop(); diagTimer.Dispose(); diagTimer = null; }
            }
            base.Dispose(disposing);
        }
    }
}



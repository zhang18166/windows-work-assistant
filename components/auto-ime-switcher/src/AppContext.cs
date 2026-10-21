using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 程序主上下文：常驻系统托盘，驱动前台监听与浮窗。
    /// </summary>
    public sealed class AppContext : ApplicationContext
    {
        private readonly AppConfig _config;
        private readonly AppHostOptions _host;
        private bool _moduleEnabled = true;
        public ActivitySnapshot CurrentActivity { get { return _lastSnapshot; } }
        private readonly NotifyIcon _tray;
        private readonly ForegroundWatcher _watcher;
        private FloatingIndicator _indicator;
        private MainForm _settingsForm;
        private Icon _trayIcon;
        private bool _warnedNoEnglishLayout;
        private bool _warnedHookFailed;
        private bool _warnedBrokenConfig;

        private ToolStripMenuItem miAutoSwitch;
        private ToolStripMenuItem miShowIndicator;
        private ToolStripMenuItem miClickThrough;
        private ToolStripMenuItem[] miMethods;
        private EventWaitHandle _showEvent;
        private System.Windows.Forms.Timer _showTimer;
        private System.Windows.Forms.Timer _stateTimer;
        private readonly Control _dispatcher;
        private readonly int _uiThread;
        private readonly OperationEpoch _epoch = new OperationEpoch();
        private volatile bool _exiting;
        private bool _wasMatched;
        private bool _trackingAvailable;
        private ImeRestorePoint _restorePoint; // 只由后台执行器访问。
        private string _restoreProcess;
        private readonly ActivityJournal _activity = new ActivityJournal();
        private ActivitySnapshot _lastSnapshot;
        private readonly ProgramIconCache _iconCache = new ProgramIconCache();

        // ---- V1.0.6：切换动作全部挪出 UI 线程 ----
        /// <summary>专用后台执行器：切换动作有同步等待，跑在 UI 线程会延迟用户真实按键。</summary>
        private readonly WorkerQueue _worker;
        /// <summary>UI 线程同步上下文，用于把切换结果/气泡提示送回界面线程。</summary>
        private readonly SynchronizationContext _uiContext;

        public AppContext(AppConfig config, bool openSettings = false, AppHostOptions host = null)
        {
            _config = config;
            _host = host;
            _moduleEnabled = host == null || host.ModuleEnabled;

            // 构造在 Application.Run 之前执行。显式创建消息窗口，不能假定 Current 已存在。
            _uiThread = Thread.CurrentThread.ManagedThreadId;
            _dispatcher = new Control();
            IntPtr dispatcherHandle = _dispatcher.Handle;
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            ImeController.CapsLockReader = ReadCapsLockOnUi;
            _worker = new WorkerQueue("AutoImeSwitcher-Action", _iconCache.Dispose);

            // 显示配置变化（拔屏/改分辨率）后主动重钳浮窗位置
            try { SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; }
            catch (Exception ex) { Logger.Warn("监听显示配置变化失败: " + ex.Message); }

            ImeController.RefreshEnglishLayout();
            Logger.Info("程序启动，配置路径=" + AppConfig.ConfigFilePath);

            _tray = new NotifyIcon();
            _trayIcon = LoadAppIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "自动输入法助手 " + Program.VersionText;
            _tray.Visible = host == null;
            _tray.DoubleClick += delegate { ShowSettings(); };
            _tray.ContextMenuStrip = BuildMenu();

            _watcher = new ForegroundWatcher();
            _watcher.ForegroundChanged += OnForegroundChanged;
            ApplyKeyboardTracking();
            _watcher.Start();

            // 让状态跟踪层一开始就知道当前前台是谁（否则用户此刻按 Shift 会记成“未知进程”）
            try
            {
                ForegroundInfo now = ForegroundWatcher.GetForegroundInfo();
                if (now != null && now.Valid)
                {
                    OnForegroundChanged(this, now);
                }
            }
            catch { }

            ApplyIndicatorSettings();
            _stateTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _stateTimer.Tick += delegate { QueueStateRead(); };
            _stateTimer.Start();

            if (!ImeController.EnglishLayoutAvailable && _config.AutoSwitchEnabled
                && _config.SwitchMethodValue == SwitchMethod.KeyboardLayout)
                WarnNoEnglishLayout();

            // 配置损坏已回退默认：说一次，别让用户以为规则"自己消失了"
            if (AppConfig.LastLoadFailed)
                WarnBrokenConfig();

            if (openSettings) ShowSettings();

            // 让第二个实例（快捷方式 / 命令行 --settings）能把设置窗口叫出来。
            // 用 400ms 轮询一个命名事件，比跨线程 Invoke 更简单也更不容易出竞态。
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEventName);
                _showTimer = new System.Windows.Forms.Timer();
                _showTimer.Interval = 400;
                _showTimer.Tick += delegate
                {
                    try { if (_showEvent.WaitOne(0)) ShowSettings(); }
                    catch { }
                };
                _showTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Warn("设置窗口联动不可用: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ 托盘
        private static Icon LoadAppIcon()
        {
            // 优先：从 exe 嵌入资源提取（csproj <ApplicationIcon>app.ico</ApplicationIcon>）
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
            catch (Exception ex)
            {
                Logger.Warn("提取 exe 图标失败: " + ex.Message);
            }
            return SystemIcons.Application;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var miTitle = new ToolStripMenuItem("自动输入法助手 " + Program.VersionText) { Enabled = false };
            menu.Items.Add(miTitle);
            menu.Items.Add(new ToolStripSeparator());

            miAutoSwitch = new ToolStripMenuItem("自动切换");
            miAutoSwitch.CheckOnClick = true;
            miAutoSwitch.Click += delegate
            {
                _config.AutoSwitchEnabled = miAutoSwitch.Checked;
                OnConfigChanged();
                _config.Save();
                if (_config.AutoSwitchEnabled && _config.SwitchMethodValue == SwitchMethod.KeyboardLayout
                    && !ImeController.EnglishLayoutAvailable)
                    WarnNoEnglishLayout();
            };
            menu.Items.Add(miAutoSwitch);

            // 切换方式：按 ImeController.MethodOrder 生成，界面顺序与枚举值解耦（不能强转下标）
            var miMethod = new ToolStripMenuItem("切换方式");
            SwitchMethod[] order = ImeController.MethodOrder;
            miMethods = new ToolStripMenuItem[order.Length];
            for (int i = 0; i < order.Length; i++)
            {
                SwitchMethod m = order[i];
                var mi = new ToolStripMenuItem(ImeController.DescribeMethod(m));
                mi.Click += delegate { SetSwitchMethod(m); };
                miMethods[i] = mi;
                miMethod.DropDownItems.Add(mi);
            }
            // 中-7：英文布局可用性以前只在启动时检测一次，用户运行中装了"英语(美国)"
            // 键盘后菜单仍置灰。每次展开子菜单时重新枚举一次即可。
            miMethod.DropDownOpening += delegate
            {
                try
                {
                    ImeController.RefreshEnglishLayout();
                    SyncMethodChecks();
                }
                catch { }
            };
            menu.Items.Add(miMethod);

            miShowIndicator = new ToolStripMenuItem("显示输入状态浮窗");
            miShowIndicator.CheckOnClick = true;
            miShowIndicator.Click += delegate
            {
                _config.ShowIndicator = miShowIndicator.Checked;
                _config.Save();
                ApplyIndicatorSettings();
            };
            menu.Items.Add(miShowIndicator);
            var resetIndicator = new ToolStripMenuItem("找回浮窗 · 重置到右下角");
            resetIndicator.Click += delegate { ResetIndicatorPosition(); };
            menu.Items.Add(resetIndicator);
            var details = new ToolStripMenuItem("查看当前详细状态");
            details.Click += delegate { MessageBox.Show(_lastSnapshot == null ? "等待识别当前程序" : _lastSnapshot.DetailsText,
                "当前详细状态", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            menu.Items.Add(details);

            // 浮窗穿透后右键菜单点不到，必须留一个托盘入口来关掉它
            miClickThrough = new ToolStripMenuItem("浮窗鼠标穿透");
            miClickThrough.CheckOnClick = true;
            miClickThrough.Click += delegate
            {
                _config.IndicatorClickThrough = miClickThrough.Checked;
                _config.Save();
                ApplyIndicatorSettings();
            };
            menu.Items.Add(miClickThrough);

            menu.Items.Add(new ToolStripSeparator());

            var miSettings = new ToolStripMenuItem("设置");
            miSettings.Click += delegate { ShowSettings(); };
            menu.Items.Add(miSettings);

            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Exit(); };
            menu.Items.Add(miExit);

            miAutoSwitch.Checked = _config.AutoSwitchEnabled;
            miShowIndicator.Checked = _config.ShowIndicator;
            miClickThrough.Checked = _config.IndicatorClickThrough;
            SyncMethodChecks();

            return menu;
        }

        private void SyncMethodChecks()
        {
            if (miMethods == null) return;
            SwitchMethod[] order = ImeController.MethodOrder;
            for (int i = 0; i < miMethods.Length && i < order.Length; i++)
            {
                if (miMethods[i] == null) continue;
                SwitchMethod m = order[i];
                miMethods[i].Checked = (m == _config.SwitchMethodValue);
                // 键盘布局方式不可用时置灰，免得用户反复试
                miMethods[i].Enabled = m != SwitchMethod.KeyboardLayout || ImeController.EnglishLayoutAvailable;
            }
        }

        private void SetSwitchMethod(SwitchMethod m)
        {
            _config.SwitchMethodValue = m;
            OnConfigChanged();
            _config.Save();
            _warnedNoEnglishLayout = false;
            SyncMethodChecks();
            Logger.Info("切换方式改为: " + ImeController.DescribeMethod(m));

            if (_settingsForm != null && !_settingsForm.IsDisposed)
                _settingsForm.LoadFromConfig();

            if (m == SwitchMethod.KeyboardLayout && !ImeController.EnglishLayoutAvailable)
                WarnNoEnglishLayout();
        }

        /// <summary>Shift 操作和安全恢复需要观察手动模式切换；不靠钩子推断中文或英文。</summary>
        private void ApplyKeyboardTracking()
        {
            if (!_moduleEnabled || !_config.AutoSwitchEnabled || (!_config.RestoreChineseOnLeave && _config.SwitchMethodValue != SwitchMethod.ShiftKey))
            {
                ImeController.StopKeyboardTracking();
                _trackingAvailable = false;
                return;
            }

            _trackingAvailable = ImeController.StartKeyboardTracking();
            if (_trackingAvailable) return;

            // 低-10：钩子装不上时 Shift 方式会静默退化（用户手动 Shift 不再被跟踪）。
            // 弹一次气球说明，别让人以为是切换逻辑坏了。
            if (_warnedHookFailed) return;
            _warnedHookFailed = true;
            Logger.Warn("键盘钩子安装失败，手动接管观察及自动恢复不可用");
            try
            {
                _tray.ShowBalloonTip(8000, "自动输入法助手",
                    "手动切换观察不可用，已暂停“离开后恢复”功能。\n" +
                    "自动切英文仍会读取真实状态，并检查用户是否按着修饰键。",
                    ToolTipIcon.Warning);
            }
            catch { }
        }

        // ------------------------------------------------------------ 设置界面
        /// <summary>宿主统一展示设置；独立版保持原窗口行为。</summary>
        public MainForm GetSettingsForm()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
                _settingsForm = new MainForm(_config, OnConfigChanged,
                    delegate { if (_host != null && _host.RequestExit != null) _host.RequestExit(); else Exit(); },
                    ResetIndicatorPosition, ApplyIndicatorSettings);
            return _settingsForm;
        }

        /// <summary>只改变本次运行的模块开关，不覆盖用户保存的输入法选项。</summary>
        public void SetModuleEnabled(bool enabled)
        {
            _moduleEnabled = enabled;
            OnConfigChanged();
        }

        public void ShowSettings()
        {
            if (_host != null && _host.ShowSettings != null) { _host.ShowSettings(); return; }
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                // 退出动作也走统一的 Exit() 路径（低-4）
                _settingsForm = new MainForm(_config, delegate { OnConfigChanged(); }, delegate { Exit(); }, ResetIndicatorPosition, ApplyIndicatorSettings);
            }
            _settingsForm.LoadFromConfig();
            _settingsForm.RefreshDiagnostics();
            if (!_settingsForm.Visible) _settingsForm.Show();
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.BringToFront();
            _settingsForm.Activate();
        }

        private void OnConfigChanged()
        {
            // 先撤销后台任务，再处理可能涉及绘制/文件 IO 的界面刷新。
            _epoch.Advance();
            _worker.CancelPending("switch");
            miAutoSwitch.Checked = _config.AutoSwitchEnabled;
            miShowIndicator.Checked = _config.ShowIndicator;
            if (miClickThrough != null) miClickThrough.Checked = _config.IndicatorClickThrough;
            SyncMethodChecks();
            ApplyIndicatorSettings();
            ApplyKeyboardTracking();
            RescheduleCurrent();
            QueueStateRead();

            if (_config.AutoSwitchEnabled && _config.SwitchMethodValue == SwitchMethod.KeyboardLayout
                && !ImeController.EnglishLayoutAvailable)
                WarnNoEnglishLayout();
        }

        // ------------------------------------------------------------ 浮窗
        private void ResetIndicatorPosition()
        {
            _config.RestoreIndicatorVisibility();
            ApplyIndicatorSettings();
            _indicator.ResetToBottomRight();
            miShowIndicator.Checked = true;
            if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.LoadFromConfig();
            _config.Save();
            QueueStateRead();
        }

        private void ApplyIndicatorSettings()
        {
            if (_moduleEnabled && _config.ShowIndicator)
            {
                if (_indicator == null || _indicator.IsDisposed)
                {
                    _indicator = new FloatingIndicator();
                    _indicator.RequestOpenSettings += delegate { ShowSettings(); };
                    _indicator.LocationChangedByUser += delegate
                    {
                        _config.IndicatorX = _indicator.Location.X;
                        _config.IndicatorY = _indicator.Location.Y;
                        _config.IndicatorScreen = _indicator.CurrentScreenDeviceName;
                        _config.Save();
                    };
                    Point actual = _indicator.ApplyPosition(
                        _config.IndicatorX, _config.IndicatorY, _config.IndicatorScreen);
                    // 原位置可能越界被钳到了别处，这里把实际生效的位置与所在屏幕一起写回配置，
                    // 避免每次启动都重新钳制一遍（中-1）。
                    if (_config.IndicatorX != actual.X || _config.IndicatorY != actual.Y
                        || string.IsNullOrEmpty(_config.IndicatorScreen))
                    {
                        _config.IndicatorX = actual.X;
                        _config.IndicatorY = actual.Y;
                        _config.IndicatorScreen = _indicator.CurrentScreenDeviceName;
                        _config.Save();
                    }
                }
                _indicator.TopMostEnabled = _config.IndicatorTopMost;
                _indicator.ClickThrough = _config.IndicatorClickThrough;
                _indicator.TransparencyPercent = _config.IndicatorTransparencyPercent;
                if (!_indicator.Visible) _indicator.Show();
            }
            else
            {
                if (_indicator != null && !_indicator.IsDisposed)
                {
                    _indicator.Hide();
                }
            }
        }

        /// <summary>显示配置变化（拔屏/改分辨率/改缩放）后重新钳一次浮窗位置。</summary>
        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // SystemEvents 的回调不在 UI 线程，必须转回去再动窗口
            if (_uiContext != null)
                _uiContext.Post(delegate { ReclampIndicator(); }, null);
            else
                ReclampIndicator();
        }

        private void ReclampIndicator()
        {
            try
            {
                if (_indicator == null || _indicator.IsDisposed || !_indicator.Visible) return;

                // 显示配置变化可能连带缩放变化：先按新 DPI 重排，再钳位置
                _indicator.RefreshDpiLayout();

                Point actual = _indicator.ApplyPosition(
                    _config.IndicatorX, _config.IndicatorY, _config.IndicatorScreen);
                if (_config.IndicatorX != actual.X || _config.IndicatorY != actual.Y)
                {
                    _config.IndicatorX = actual.X;
                    _config.IndicatorY = actual.Y;
                    _config.IndicatorScreen = _indicator.CurrentScreenDeviceName;
                    _config.Save();
                    Logger.Info("显示配置变化，浮窗位置已重新钳制");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("重钳浮窗位置失败: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ 核心逻辑
        private ForegroundInfo _lastInfo;

        private bool? ReadCapsLockOnUi()
        {
            if (_exiting) return null;
            if (Thread.CurrentThread.ManagedThreadId == _uiThread)
                return (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
            var completion = new TaskCompletionSource<bool?>();
            try
            {
                _uiContext.Post(delegate {
                    try { completion.TrySetResult(_exiting ? (bool?)null : (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0); }
                    catch { completion.TrySetResult(null); }
                }, null);
                return completion.Task.Wait(200) ? completion.Task.Result : null;
            }
            catch { return null; }
        }

        private TargetRule[] SnapshotRules()
        {
            return _config.Rules.ConvertAll(r => r == null ? null :
                new TargetRule(r.ProcessName, r.DisplayName) { TitleContains = r.TitleContains }).ToArray();
        }

        private static bool SameIdentity(ForegroundInfo a, ForegroundInfo b)
        {
            return a != null && b != null && a.Hwnd == b.Hwnd && a.Pid == b.Pid && a.ThreadId == b.ThreadId;
        }

        private static bool MatchesRules(ForegroundInfo info, TargetRule[] rules)
        {
            if (info == null || string.IsNullOrEmpty(info.ProcessName)) return false;
            foreach (TargetRule rule in rules)
            {
                if (rule == null || !string.Equals(rule.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(rule.TitleContains) ||
                    (!string.IsNullOrEmpty(info.Title) && info.Title.IndexOf(rule.TitleContains, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            return false;
        }

        private void RescheduleCurrent()
        {
            if (_exiting) return;
            ForegroundInfo now = ForegroundWatcher.GetForegroundInfo();
            _lastInfo = now;
            _wasMatched = now.Valid && MatchesAnyRule(now);
            ScheduleTransition(now, _wasMatched, false);
        }

        private void OnForegroundChanged(object sender, ForegroundInfo info)
        {
            if (_exiting || info == null || !info.Valid) return;
            bool matched = MatchesAnyRule(info);
            bool same = SameIdentity(_lastInfo, info);
            bool previousMatched = _wasMatched;
            _lastInfo = info;
            _wasMatched = matched;
            // 标题中的保存星号等变化不重复强制切换；匹配状态改变才是新任务。
            if (same && previousMatched == matched) return;
            ScheduleTransition(info, matched, previousMatched);
        }

        private void ScheduleTransition(ForegroundInfo info, bool matched, bool mayRestore)
        {
            long epoch = _epoch.Advance();
            _worker.CancelPending("state");
            _worker.CancelPending("switch");
            bool enabled = _moduleEnabled && _config.AutoSwitchEnabled;
            bool allowRestore = _config.RestoreChineseOnLeave && _trackingAvailable;
            SwitchMethod method = _config.SwitchMethodValue;
            TargetRule[] rules = SnapshotRules();
            long revision = ImeController.ManualRevision;
            _activity.Begin(epoch, info, enabled, matched);
            _worker.EnqueueLatest("switch", delegate {
                Func<bool> current = () => _epoch.IsCurrent(epoch) && enabled &&
                    NativeMethods.GetForegroundWindow() == info.Hwnd &&
                    ImeController.SameWindow(info.Hwnd, info.ThreadId, info.Pid) &&
                    MatchesRules(new ForegroundInfo { ProcessName = info.ProcessName,
                        Title = NativeMethods.GetWindowTitle(info.Hwnd) }, rules) == matched;
                ImeRestorePoint old = _restorePoint;
                string oldProcess = _restoreProcess;
                _restorePoint = null;
                _restoreProcess = null;
                if (!current()) return;
                if (mayRestore && allowRestore && old != null)
                    _activity.Restored(epoch, oldProcess ?? "原目标程序", ImeController.RestorePrevious(old, current));
                if (!matched || !current()) return;
                if (revision != ImeController.ManualRevision)
                { _activity.Complete(epoch, info.ProcessName, method, SwitchResult.Skipped); return; }
                _activity.Running(epoch);
                ImeRestorePoint restore;
                SwitchResult result = ImeController.EnsureEnglish(info.Hwnd, info.ThreadId, info.Pid,
                    method, current, out restore);
                if (allowRestore && current()) { _restorePoint = restore; _restoreProcess = info.ProcessName; }
                _activity.Complete(epoch, info.ProcessName, method, result);
                PostToUi(delegate {
                    if (_epoch.IsCurrent(epoch)) ReportSwitchResult(result, info.ProcessName, method);
                });
            });
            QueueStateRead();
        }

        private void QueueStateRead()
        {
            if (_exiting) return;
            long epoch = _epoch.Current;
            bool enabled = _moduleEnabled && _config.AutoSwitchEnabled;
            SwitchMethod method = _config.SwitchMethodValue;
            TargetRule[] rules = SnapshotRules();
            bool showIndicator = _moduleEnabled && _config.ShowIndicator;
            _worker.EnqueueLatest("state", delegate {
                if (!_epoch.IsCurrent(epoch)) return;
                ImeController.ObserveRestorePoint(_restorePoint);
                ForegroundInfo now = ForegroundWatcher.GetForegroundInfo();
                IntPtr focus = ImeController.GetFocusWindowOfThread(now.ThreadId);
                if (focus == IntPtr.Zero) focus = now.Hwnd;
                string text = ImeController.ReadIndicator(focus, now.ThreadId, method);
                if (NativeMethods.GetForegroundWindow() != now.Hwnd) return;
                bool matched = MatchesRules(now, rules);
                string name = now.ProcessName;
                foreach (TargetRule rule in rules)
                    if (rule != null && string.Equals(rule.ProcessName, now.ProcessName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rule.DisplayName))
                    { name = rule.DisplayName; break; }
                if (string.IsNullOrWhiteSpace(name)) name = "未识别程序";
                if (string.Equals(name, "AutoImeSwitcher.exe", StringComparison.OrdinalIgnoreCase)) name = "自动输入法助手";
                ActivitySnapshot snapshot = _activity.Snapshot(now, enabled, matched, text, name);
                Bitmap icon = showIndicator ? _iconCache.CopyFor(now.ExePath) : null;
                try
                {
                    _uiContext.Post(delegate {
                        bool transferred = false;
                        try
                        {
                            if (_exiting || !_epoch.IsCurrent(epoch) || NativeMethods.GetForegroundWindow() != now.Hwnd) return;
                            _lastSnapshot = snapshot;
                            if (_indicator != null && !_indicator.IsDisposed)
                            { _indicator.SetActivity(snapshot, icon); transferred = true; }
                            if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.SetActivity(snapshot);
                        }
                        finally { if (!transferred && icon != null) icon.Dispose(); }
                    }, null);
                }
                catch { if (icon != null) icon.Dispose(); }
            });
        }

        private void PostToUi(Action action)
        {
            if (action == null || _exiting) return;
            _uiContext.Post(delegate { if (!_exiting) action(); }, null);
        }
        private void ReportSwitchResult(SwitchResult result, string processName, SwitchMethod method)
        {
            switch (result)
            {
                case SwitchResult.AlreadyEnglish:
                    // 已经是英文，不做任何操作
                    break;

                case SwitchResult.Switched:
                    Logger.Info("已切英文(" + ImeController.DescribeMethod(method) + "): " + processName);
                    break;

                case SwitchResult.NoEnglishLayout:
                    WarnNoEnglishLayout();
                    break;

                case SwitchResult.RequestedButNotVerified:
                    Logger.Warn("已请求但未复核通过: " + processName);
                    break;

                case SwitchResult.SendFailed:
                    Logger.Warn("切换请求发送失败: " + processName);
                    break;

                case SwitchResult.Skipped:
                    // 用户正在按键或前台已变化，这一次不插手
                    Logger.Info("已跳过（前台刚变化或用户正在按 Shift）");
                    break;
                case SwitchResult.UnknownState:
                    Logger.Warn("状态未知，未继续切换: " + processName);
                    break;
            }
        }

        private bool MatchesAnyRule(ForegroundInfo info)
        {
            if (string.IsNullOrEmpty(info.ProcessName)) return false;

            foreach (var rule in _config.Rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.ProcessName)) continue;
                if (!string.Equals(rule.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(rule.TitleContains))
                {
                    if (string.IsNullOrEmpty(info.Title)) continue;
                    if (info.Title.IndexOf(rule.TitleContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                return true;
            }
            return false;
        }

        private void WarnNoEnglishLayout()
        {
            if (_warnedNoEnglishLayout) return;
            _warnedNoEnglishLayout = true;

            Logger.Warn("系统未启用英文(美国)键盘布局，无法自动切换；未对系统输入法做任何修改。");
            try
            {
                _tray.ShowBalloonTip(8000, "自动输入法助手",
                    "系统当前没有可用的“英语(美国)”键盘布局，自动切换无法工作。\n" +
                    "请在 Windows 设置 → 时间和语言 → 语言 中添加 English (United States) 键盘。\n" +
                    "本程序不会自动修改系统输入法配置。",
                    ToolTipIcon.Warning);
            }
            catch { }
        }

        private void WarnBrokenConfig()
        {
            if (_warnedBrokenConfig) return;
            _warnedBrokenConfig = true;

            Logger.Warn("配置文件已损坏，本次使用默认配置；原文件已另存为 config.json.bad-*");
            try
            {
                _tray.ShowBalloonTip(10000, "自动输入法助手",
                    "原来的配置文件已损坏，本次先用默认配置启动。\n" +
                    "原文件已备份为 config.json.bad-时间戳，可在配置目录查看。",
                    ToolTipIcon.Warning);
            }
            catch { }
        }

        // ------------------------------------------------------------ 退出
        /// <summary>
        /// 统一的退出入口（低-4）。
        /// 托盘"退出"和设置窗口"退出程序"以前走两条路径（ExitThread / Application.Exit），
        /// 清理动作不一致。现在两边都收敛到这里：先做完整清理，再结束消息循环。
        /// ApplicationContext 的标准退出方式就是 ExitThread()；Application.Exit()
        /// 对没有主窗体的 ApplicationContext 语义不明确，所以不采用。
        /// </summary>
        private void Exit()
        {
            StopActions();
            try
            {
                if (_showTimer != null) { _showTimer.Stop(); _showTimer.Dispose(); _showTimer = null; }
                if (_showEvent != null) { _showEvent.Close(); _showEvent = null; }
                if (_watcher != null) _watcher.Dispose();
                ImeController.StopKeyboardTracking();
                if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.Dispose();
                if (_indicator != null && !_indicator.IsDisposed) { _indicator.Hide(); _indicator.Dispose(); }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }
                _config.Save();
                Theme.DisposeFonts();
                Logger.Info("程序退出");
            }
            catch { }
            ExitThread();
        }

        private void StopActions()
        {
            if (_exiting) return;
            _exiting = true;
            _epoch.Stop();
            if (_stateTimer != null) { _stateTimer.Stop(); _stateTimer.Dispose(); _stateTimer = null; }
            _worker.Dispose();
            ImeController.CapsLockReader = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopActions();
                try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
                catch { }
                try { if (_worker != null) _worker.Dispose(); }
                catch { }
                _dispatcher.Dispose();

                try
                {
                    if (_showTimer != null) { _showTimer.Stop(); _showTimer.Dispose(); _showTimer = null; }
                    if (_showEvent != null) { _showEvent.Close(); _showEvent = null; }
                    ImeController.StopKeyboardTracking();
                    if (_watcher != null) _watcher.Dispose();
                    if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                    if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }
                    if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.Dispose();
                    if (_indicator != null && !_indicator.IsDisposed) _indicator.Dispose();
                    Theme.DisposeFonts();
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}


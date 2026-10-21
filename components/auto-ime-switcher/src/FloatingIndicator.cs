using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    /// <summary>不抢焦点的状态卡片：程序图标、匹配状态、输入状态和最近操作。</summary>
    public sealed class FloatingIndicator : Form
    {
        private bool _dragging;
        private Point _dragCursorOffset;
        private bool _topMostEnabled = true;
        private bool _clickThrough;
        private int _transparencyPercent = 30;
        public int TransparencyPercent
        {
            get { return _transparencyPercent; }
            set
            {
                int next = Math.Max(0, Math.Min(100, value));
                if (_transparencyPercent == next) return;
                _transparencyPercent = next;
                Render();
            }
        }
        internal static byte TransparencyToAlpha(int transparencyPercent)
        {
            int clamped = Math.Max(0, Math.Min(100, transparencyPercent));
            return (byte)Math.Round((100 - clamped) * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        }
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miTopMost, _miClickThrough;
        private float _scale = 1f;
        private bool _suspendRender;
        private ActivitySnapshot _activity = new ActivitySnapshot();
        private Bitmap _programIcon;
        private string _iconKey;
        private readonly ToolTip _detailsTip = new ToolTip {
            InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 15000, ShowAlways = true
        };

        // 接管 image 的所有权；每个快照的图标最多保留一份。
        public void SetActivity(ActivitySnapshot activity, Bitmap image)
        {
            if (activity == null) { if (image != null) image.Dispose(); return; }
            bool sameIcon = string.Equals(_iconKey, activity.ExePath, StringComparison.OrdinalIgnoreCase);
            bool changed = _activity.Signature != activity.Signature;
            if (!sameIcon || (_programIcon == null && image != null))
            {
                if (_programIcon != null) _programIcon.Dispose();
                _programIcon = image; image = null; _iconKey = activity.ExePath; changed = true;
            }
            if (image != null) image.Dispose();
            _activity = activity;
            if (changed) { _detailsTip.SetToolTip(this, activity.DetailsText); Render(); }
        }
        // ---------------------------------------------------------------- Win32
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x02;

        // ---------------------------------------------------------------- 事件
        public event EventHandler RequestOpenSettings;
        public event EventHandler LocationChangedByUser;

        public FloatingIndicator()
        {
            SuspendLayout();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;   // 尺寸全部自己按 DPI 算，不让 WinForms 二次缩放
            DoubleBuffered = true;
            TopMost = true;

            SetStyle(ControlStyles.Opaque |
                     ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint, true);

            BuildContextMenu();

            Resize += delegate { Render(); };
            VisibleChanged += delegate { if (Visible) Render(); };
            Shown += delegate { ApplyDpiLayout(); Render(); };

            ResumeLayout(false);
        }

        // 分层窗口：不给 WS_EX_LAYERED 就没法用 UpdateLayeredWindow
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 强制 WS_POPUP：否则 WinForms 建的是 WS_OVERLAPPED 顶层窗口，
                // 系统会按最小跟踪尺寸（150% DPI 下 202×56）把窗口撑大，纯文字窗口会被加高一大截。
                cp.Style = (cp.Style & ~0x00C00000) | WS_POPUP;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Logger.Info("浮窗句柄已创建");
            ApplyDpiLayout();
            // 高-3：ClickThrough 可能是在 Show() 之前赋值的，那时句柄还不存在，
            // ApplyClickThrough() 会直接返回，WS_EX_TRANSPARENT 从未被写入。
            // 句柄建好立刻补设一次，"鼠标穿透"才是真正开箱即生效。
            ApplyClickThrough();
            Render();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpiLayout();
            Render();
        }

        // ---------------------------------------------------------------- 尺寸与字体

        /// <summary>当前窗口所在显示器缩放比（1.0 = 96dpi）。</summary>
        private float DpiScale
        {
            get
            {
                try
                {
                    using (var g = CreateGraphics())
                        return g.DpiX / 96f;
                }
                catch { return 1f; }
            }
        }

        private void ApplyDpiLayout()
        {
            if (!IsHandleCreated) return;
            _scale = DpiScale;
            _suspendRender = true;
            Size = new Size((int)Math.Round(ActivityCardPainter.Width * _scale),
                (int)Math.Round(ActivityCardPainter.Height * _scale));
            _suspendRender = false;
            Render();
        }
        // ---------------------------------------------------------------- 绘制
        // 分层窗口的内容完全由 UpdateLayeredWindow 推送，WM_PAINT 里什么都不画
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        private void Render()
        {
            if (!IsHandleCreated || _suspendRender || Width <= 0 || Height <= 0) return;
            int w = Width, h = Height;
            using (var bmp = ActivityCardPainter.Render(_activity, _programIcon, _scale, _transparencyPercent))
            {
                IntPtr screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return;

                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBmp = IntPtr.Zero;
                IntPtr oldBmp = IntPtr.Zero;
                try
                {
                    hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    oldBmp = SelectObject(memDc, hBmp);

                    var size = new SIZE { cx = w, cy = h };
                    var src = new POINT { x = 0, y = 0 };
                    // 单位标定（实测）：本进程 PerMonitorV2 下 WinForms 的 DeviceDpi 为 96
                    // （.NET Framework 的 PMv2 只在 WM_DPICHANGED 时更新 DPI，创建时从 96 起算），
                    // 因此 Location/Size 与屏幕坐标同为物理像素、1:1 映射；
                    // UpdateLayeredWindow 的 pptDst 也是物理像素，直接推 Left/Top 即可。
                    // 曾经这里乘过 _scale，结果 ULW 移动窗口后 Left 读回新物理位置、
                    // 下次 Render 再乘一次，形成"每帧放大 1.5 倍"的反馈环，窗口被推出屏幕。
                    var dst = new POINT { x = Left, y = Top };
                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255, // 仅绘制背景时调 Alpha，不能再降低文字和图标的不透明度。
                        AlphaFormat = AC_SRC_ALPHA
                    };

                    bool ok = UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                                                  memDc, ref src, 0, ref blend, ULW_ALPHA);
                    if (!ok)
                        Logger.Warn("UpdateLayeredWindow 失败，lastError=" + Marshal.GetLastWin32Error());
                }
                finally
                {
                    if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    if (memDc != IntPtr.Zero) DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        // ---------------------------------------------------------------- 右键菜单

        private void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9F);

            _miTopMost = new ToolStripMenuItem("始终置顶");
            _miTopMost.CheckOnClick = true;
            _miTopMost.Checked = _topMostEnabled;
            _miTopMost.Click += delegate { TopMostEnabled = _miTopMost.Checked; };
            _menu.Items.Add(_miTopMost);

            _miClickThrough = new ToolStripMenuItem("鼠标穿透（鼠标直接点到下面窗口）");
            _miClickThrough.CheckOnClick = true;
            _miClickThrough.Checked = _clickThrough;
            _miClickThrough.Click += delegate { ClickThrough = _miClickThrough.Checked; };
            _menu.Items.Add(_miClickThrough);

            var miReset = new ToolStripMenuItem("重置到主屏右下角");
            miReset.Click += delegate { ResetPosition(); };
            _menu.Items.Add(miReset);

            _menu.Items.Add(new ToolStripSeparator());

            var miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { RaiseRequestOpenSettings(); };
            _menu.Items.Add(miSettings);
            var details = new ToolStripMenuItem("查看详细状态");
            details.Click += delegate { MessageBox.Show(this, _activity.DetailsText, "当前详细状态", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            _menu.Items.Add(details);
        }

        // ---------------------------------------------------------------- 状态属性

        public bool TopMostEnabled
        {
            get { return _topMostEnabled; }
            set
            {
                _topMostEnabled = value;
                TopMost = value;
                if (_miTopMost != null) _miTopMost.Checked = value;
            }
        }

        /// <summary>鼠标穿透：true 时鼠标事件直接穿过浮窗落到下方窗口。</summary>
        public bool ClickThrough
        {
            get { return _clickThrough; }
            set
            {
                _clickThrough = value;
                ApplyClickThrough();
                if (_miClickThrough != null) _miClickThrough.Checked = value;
            }
        }

        private void ApplyClickThrough()
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong32(Handle, GWL_EXSTYLE);
            int want = _clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLong32(Handle, GWL_EXSTYLE, want);
            if (_clickThrough) Cursor = Cursors.Default;
        }

        /// <summary>浮窗当前所在显示器的设备名（用于随位置一起记住屏幕）。</summary>
        public string CurrentScreenDeviceName
        {
            get
            {
                try { return Screen.FromControl(this).DeviceName; }
                catch { return null; }
            }
        }

        /// <summary>
        /// 应用保存的位置（V1.0.6 多屏修正 + 单位统一）。
        ///
        /// 单位约定：本窗口的 Location/Size 一律按**逻辑像素**（WinForms 语义），
        /// Render() 里推给 UpdateLayeredWindow 时再乘 DPI 换算成物理像素。
        /// 注意 Screen.WorkingArea 在 PerMonitorV2 进程里返回的是**物理像素**，
        /// 必须先换算成逻辑像素再比较，否则 150% 缩放下会把窗口放到屏幕外。
        ///
        /// 越界判定按"保存时所在的那块屏幕"的工作区；屏幕已经不在了
        /// （拔掉副屏/改分辨率）才退回按坐标找最近屏幕，最后兜底主屏。
        /// 返回实际生效的位置，供调用方写回配置（避免每次启动重复钳制）。
        /// </summary>
        public Point ApplyPosition(int x, int y, string screenDeviceName)
        {
            // 句柄必须先建好：否则 Width/Height 还是默认值（300×300），DPI 也取不到，
            // 钳位就会按错误的窗口尺寸计算。建句柄会触发 OnHandleCreated
            // （ApplyDpiLayout 收紧尺寸 + ApplyClickThrough 补穿透样式）。
            if (!IsHandleCreated)
            {
                IntPtr force = Handle;
                GC.KeepAlive(force);
            }

            Screen target = ResolveScreen(screenDeviceName, x, y);
            Rectangle area = target != null ? target.WorkingArea : FallbackArea();

            int nx = x;
            int ny = y;

            bool invalid = (x == -1 && y == -1 && string.IsNullOrEmpty(screenDeviceName))
                        || nx < area.Left || ny < area.Top
                        || nx > area.Right - Width || ny > area.Bottom - Height;
            if (invalid)
            {
                // 原位置不可用（越界或从未设置）：回落到该屏幕右下角
                nx = area.Right - Width - 12;
                ny = area.Bottom - Height - 12;
            }

            // 再钳一次，防止屏幕比窗口还小的极端情况
            nx = Math.Max(area.Left, Math.Min(area.Right - Width, nx));
            ny = Math.Max(area.Top, Math.Min(area.Bottom - Height, ny));

            float s = _scale > 0.01f ? _scale : 1f;
            Location = new Point(nx, ny);
            string afterLocation = PhysicalRect();
            Render();   // 分层窗口的位图要跟着新坐标重新推送

            Logger.Info(string.Format(
                "ApplyPosition 保存=({0},{1}) 屏幕={2} 工作区(原始)=({3},{4})-({5},{6}) 窗口={7}x{8} scale={9} DeviceDpi={10} → Location=({11},{12}) 设Location后={13} 最终={14}",
                x, y, target != null ? target.DeviceName : "(null)",
                area.Left, area.Top, area.Right, area.Bottom,
                Width, Height, s.ToString("0.##"), DeviceDpi, nx, ny, afterLocation, PhysicalRect()));

            return new Point(nx, ny);
        }

        private static Rectangle FallbackArea()
        {
            try
            {
                if (Screen.PrimaryScreen != null) return Screen.PrimaryScreen.WorkingArea;
            }
            catch { }
            return new Rectangle(0, 0, 1024, 768);
        }

        /// <summary>按"设备名 → 坐标 → 主屏"的顺序找目标屏幕，取不到返回 null。
        /// 配置里存的就是物理像素坐标，Screen.FromPoint 也按物理像素比较，无需换算。</summary>
        private static Screen ResolveScreen(string deviceName, int x, int y)
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceName))
                {
                    foreach (Screen s in Screen.AllScreens)
                    {
                        if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            return s;
                    }
                }
                if (x >= 0 && y >= 0) return Screen.FromPoint(new Point(x, y));
            }
            catch { }
            return null;
        }

        /// <summary>当前光标所在屏幕的物理像素工作区（拖动钳制用）。</summary>
        private Rectangle PhysicalWorkArea()
        {
            try { return Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch { return FallbackArea(); }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>窗口当前物理像素矩形（诊断/标定用）。</summary>
        private string PhysicalRect()
        {
            if (!IsHandleCreated) return "(无句柄)";
            var r = new RECT();
            if (!GetWindowRect(Handle, out r))
                return "(GetWindowRect 失败 " + Marshal.GetLastWin32Error() + ")";
            return string.Format("({0},{1})-({2},{3}) {4}x{5}",
                r.Left, r.Top, r.Right, r.Bottom, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>右键菜单"重置位置"：移回当前屏幕右下角，并通知保存。</summary>
        private void ResetPosition() { ResetToBottomRight(); }

        public void ResetToBottomRight()
        {
            if (!IsHandleCreated) { IntPtr ignored = Handle; GC.KeepAlive(ignored); }
            Rectangle area = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : FallbackArea();
            Location = BottomRight(area, Size, (int)Math.Round(16 * _scale));
            SyncDpiLayout();
            Location = BottomRight(area, Size, (int)Math.Round(16 * _scale));
            Render();
            RaiseLocationChanged();
        }

        internal static Point BottomRight(Rectangle area, Size size, int margin)
        {
            return new Point(Math.Max(area.Left, area.Right - size.Width - margin),
                Math.Max(area.Top, area.Bottom - size.Height - margin));
        }
        // ---------------------------------------------------------------- 鼠标

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _detailsTip.Hide(this);
                _dragging = true;
                _dragCursorOffset = new Point(e.X, e.Y);
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                // PointToScreen 与 Width/Height 在本进程下同为物理像素（实测标定），直接相减。
                Point p = PointToScreen(e.Location);
                int nx = p.X - _dragCursorOffset.X;
                int ny = p.Y - _dragCursorOffset.Y;

                // 拖动时按光标所在屏幕的工作区钳制（低-2）：
                // 以前可以拖到屏幕外，鼠标找不到就再也点不回来，只能删配置。
                Rectangle area = PhysicalWorkArea();
                nx = Math.Max(area.Left, Math.Min(area.Right - Width, nx));
                ny = Math.Max(area.Top, Math.Min(area.Bottom - Height, ny));

                Location = new Point(nx, ny);
                Render();   // 分层窗口：ULW 推送是位置的最终裁决者，拖动时也要跟着刷新
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Cursor = Cursors.Default;
                RaiseLocationChanged();
                // 拖到了缩放不同的显示器上：按新 DPI 重排字体与尺寸
                SyncDpiLayout();
            }
            if (e.Button == MouseButtons.Right && _menu != null)
                _menu.Show(this, new Point(e.X, e.Y));
        }

        /// <summary>
        /// 窗口所在显示器 DPI 变化后重排（多屏不同缩放）。
        /// WinForms 的 DeviceDpi 在 .NET Framework PMv2 下不随窗口移动而更新
        /// （创建时从 96 起算，只有 WM_DPICHANGED 才刷新），所以主动比对真实 DPI。
        /// </summary>
        private void SyncDpiLayout()
        {
            if (!IsHandleCreated) return;
            try
            {
                float s = DpiScale;
                if (Math.Abs(s - _scale) < 0.01f) return;
                Logger.Info("浮窗所在显示器 DPI 变为 " + (int)Math.Round(s * 96f) + "，重新排版");
                ApplyDpiLayout();
            }
            catch { }
        }

        /// <summary>供外部（显示配置变化处理）触发一次 DPI 重排。</summary>
        public void RefreshDpiLayout()
        {
            SyncDpiLayout();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            RaiseRequestOpenSettings();
        }

        private void RaiseRequestOpenSettings()
        {
            var handler = RequestOpenSettings;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseLocationChanged()
        {
            var handler = LocationChangedByUser;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _detailsTip.Dispose();
                if (_programIcon != null) { _programIcon.Dispose(); _programIcon = null; }
                if (_menu != null) { _menu.Dispose(); _menu = null; }
            }
            base.Dispose(disposing);
        }
    }
}


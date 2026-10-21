using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 扁平化主题：统一配色、字体、圆角路径与开关绘制。
    ///
    /// 设计约定：
    ///   - 页面底色 #F6F7FA，内容卡片纯白 + 1px 极淡描边，不用 GroupBox 的凹陷边框；
    ///   - 强调色沿用图标里的橙粉（#FF7542），只用于开关/选中态；
    ///   - 所有自绘控件都按所在窗口的实时 DPI 换算尺寸（Theme.ScaleFor），
    ///     150% 缩放下不会糊也不会错位，副屏不同缩放也不会按主屏排版。
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Page = Color.FromArgb(246, 247, 250);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Hair = Color.FromArgb(234, 236, 241);
        public static readonly Color HairSoft = Color.FromArgb(242, 244, 247);
        public static readonly Color Ink = Color.FromArgb(26, 28, 33);
        public static readonly Color Sub = Color.FromArgb(122, 128, 138);
        public static readonly Color Faint = Color.FromArgb(174, 180, 190);
        public static readonly Color Accent = Color.FromArgb(255, 117, 66);
        public static readonly Color AccentHover = Color.FromArgb(245, 104, 52);
        public static readonly Color AccentSoft = Color.FromArgb(255, 243, 237);
        public static readonly Color HoverFill = Color.FromArgb(246, 247, 250);
        public static readonly Color TrackOff = Color.FromArgb(221, 226, 233);
        public static readonly Color Danger = Color.FromArgb(221, 79, 69);

        private static Font _title, _head, _ui, _uiBold, _small;

        public static Font Title
        {
            get { if (_title == null) _title = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold); return _title; }
        }

        /// <summary>卡片小标题（加粗 9.5pt）。</summary>
        public static Font Head
        {
            get { if (_head == null) _head = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold); return _head; }
        }

        public static Font Ui
        {
            get { if (_ui == null) _ui = new Font("Microsoft YaHei UI", 9F); return _ui; }
        }

        public static Font UiBold
        {
            get { if (_uiBold == null) _uiBold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold); return _uiBold; }
        }

        public static Font Small
        {
            get { if (_small == null) _small = new Font("Microsoft YaHei UI", 8.4F); return _small; }
        }

        /// <summary>
        /// 全局 UI 缩放兜底值（1.0 = 96dpi）。
        ///
        /// 屏幕 DC 的 DpiX 是物理 DPI（取主屏），用于句柄还没建好时的估算。
        /// 控件真正排版用 <see cref="ScaleFor(Control)"/>：按控件**所在窗口**的
        /// 实时 DPI 计算——多屏不同缩放下，副屏上的窗口不会被按主屏 DPI 排错（中-6）。
        /// </summary>
        private static float _scale = -1f;

        public static float Scale
        {
            get
            {
                if (_scale < 0f)
                {
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                            _scale = g.DpiX / 96f;
                    }
                    catch (Exception)
                    {
                        _scale = 1f;
                    }
                    if (_scale < 0.5f || _scale > 4f) _scale = 1f;
                }
                return _scale;
            }
        }

        /// <summary>
        /// 控件所在窗口的实时缩放。句柄未创建时退回全局 Scale。
        /// 用 CreateGraphics().DpiX 而不是 Control.DeviceDpi：与 FloatingIndicator
        /// 的做法保持一致，PerMonitorV2 下两者都反映窗口当前所在显示器。
        /// </summary>
        public static float ScaleFor(Control c)
        {
            try
            {
                if (c != null && c.IsHandleCreated)
                {
                    using (Graphics g = c.CreateGraphics())
                    {
                        float dpi = g.DpiX / 96f;
                        if (dpi >= 0.5f && dpi <= 4f) return dpi;
                    }
                }
            }
            catch { }
            return Scale;
        }

        public static float ScaleOf(Control c)
        {
            return ScaleFor(c);
        }

        public static int Px(Control c, float v)
        {
            return (int)Math.Round(v * ScaleFor(c));
        }

        /// <summary>释放静态字体（进程退出前调用，低-9）。</summary>
        public static void DisposeFonts()
        {
            if (_title != null) { _title.Dispose(); _title = null; }
            if (_head != null) { _head.Dispose(); _head = null; }
            if (_ui != null) { _ui.Dispose(); _ui = null; }
            if (_uiBold != null) { _uiBold.Dispose(); _uiBold = null; }
            if (_small != null) { _small.Dispose(); _small = null; }
        }

        /// <summary>圆角矩形路径（radius 为半径，内部自动把直径钳到短边）。</summary>
        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 1f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        /// <summary>画一个 iOS 风格开关（不负责文字）。</summary>
        public static void DrawSwitch(Graphics g, RectangleF r, bool on, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = on
                ? (hover ? AccentHover : Accent)
                : (hover ? Color.FromArgb(206, 212, 221) : TrackOff);

            using (var p = Rounded(r, r.Height / 2f))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, p);

            float pad = r.Height * 0.13f;
            float d = r.Height - pad * 2f;
            float x = on ? r.Right - pad - d : r.Left + pad;
            var knob = new RectangleF(x, r.Top + pad, d, d);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, knob);
        }
    }

    /// <summary>扁平按钮：Primary 橙色实心、Secondary 白底细边、Ghost 无边框。</summary>
    internal sealed class FlatButton : Control
    {
        public enum Style { Primary, Secondary, Ghost }

        private Style _style = Style.Secondary;
        private bool _hover;
        private bool _down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            TabStop = false;
            BackColor = Theme.CardBg;   // 卡片内是白底，显式指定避免继承成页面灰
        }

        public Style ButtonStyle
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color parentBg = BackColor;
            using (var b = new SolidBrush(parentBg))
                g.FillRectangle(b, ClientRectangle);

            Color fill, border, fore;
            switch (_style)
            {
                case Style.Primary:
                    fill = _down ? Theme.AccentHover : (_hover ? Theme.AccentHover : Theme.Accent);
                    border = fill;
                    fore = Color.White;
                    break;
                case Style.Ghost:
                    fill = _hover ? Theme.HoverFill : parentBg;
                    border = fill;
                    fore = _hover ? Theme.Ink : Theme.Sub;
                    break;
                default:
                    fill = _down ? Color.FromArgb(232, 235, 241)
                           : (_hover ? Color.FromArgb(240, 242, 246) : Color.FromArgb(245, 246, 249));
                    border = _hover ? Color.FromArgb(208, 213, 222) : Color.FromArgb(227, 230, 236);
                    fore = Theme.Ink;
                    break;
            }

            float r = Theme.Px(this, 7);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(rect, r))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (_style == Style.Secondary)
                {
                    using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
            }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>纯开关（不含文字），click 整块切换。</summary>
    internal sealed class FlatSwitch : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public FlatSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.CardBg;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);
            var r = new RectangleF(0, 0, Width - 1f, Height - 1f);
            Theme.DrawSwitch(g, r, _checked, _hover);
        }
    }

    /// <summary>整行开关：左侧文字，右侧开关，点整行都能切换。</summary>
    internal sealed class ToggleRow : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public ToggleRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            BackColor = Theme.CardBg;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            int sw = Theme.Px(this, 42);
            int sh = Theme.Px(this, 24);
            int right = Theme.Px(this, 2);
            var swRect = new RectangleF(Width - right - sw, (Height - sh) / 2f, sw, sh);

            var textRect = new Rectangle(0, 0, Math.Max(0, Width - sw - Theme.Px(this, 12)), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, _checked ? Theme.Ink : Color.FromArgb(58, 62, 70),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);

            Theme.DrawSwitch(g, swRect, _checked, _hover);
        }
    }

    /// <summary>分段选择器（3~4 个互斥选项）。</summary>
    internal sealed class FlatSegmented : Control
    {
        private string[] _items = new string[0];
        private int _index;
        private int _hover = -1;

        public event EventHandler SelectedIndexChanged;

        public FlatSegmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            BackColor = Theme.CardBg;
        }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; _index = 0; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = Math.Max(0, Math.Min(_items.Length - 1, value));
                if (v == _index) return;
                _index = v;
                Invalidate();
                EventHandler h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private int HitTest(int x)
        {
            if (_items.Length == 0) return -1;
            int seg = Width / _items.Length;
            if (seg <= 0) return -1;
            int i = x / seg;
            return Math.Max(0, Math.Min(_items.Length - 1, i));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.X);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Point p = PointToClient(Cursor.Position);
            int i = HitTest(p.X);
            if (i >= 0) SelectedIndex = i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            float radius = Theme.Px(this, 9);
            var track = new RectangleF(0, 0, Width - 1f, Height - 1f);
            using (var path = Theme.Rounded(track, radius))
            using (var b = new SolidBrush(Color.FromArgb(241, 243, 247)))
                g.FillPath(b, path);

            if (_items.Length == 0) return;

            float segW = (Width - 1f) / _items.Length;
            for (int i = 0; i < _items.Length; i++)
            {
                var cell = new RectangleF(i * segW, 0, segW, Height - 1f);
                if (i == _index)
                {
                    var pill = new RectangleF(cell.X + 2f, 2f, cell.Width - 4f, cell.Height - 3f);
                    using (var path = Theme.Rounded(pill, radius - 2f))
                    {
                        using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                        using (var p = new Pen(Color.FromArgb(228, 231, 237), 1f)) g.DrawPath(p, path);
                    }
                }
                else if (i == _hover)
                {
                    var pill = new RectangleF(cell.X + 2f, 2f, cell.Width - 4f, cell.Height - 3f);
                    using (var path = Theme.Rounded(pill, radius - 2f))
                    using (var b = new SolidBrush(Color.FromArgb(247, 248, 251)))
                        g.FillPath(b, path);
                }

                var textRect = new Rectangle((int)Math.Round(cell.X), 0, (int)Math.Round(cell.Width), Height);
                TextRenderer.DrawText(g, _items[i], i == _index ? Theme.UiBold : Theme.Ui, textRect,
                    i == _index ? Theme.Ink : Theme.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>扁平规则列表：自绘表头 + 行，选中行用浅橙底 + 左侧橙条。</summary>
    internal sealed class FlatRuleList : Control
    {
        public sealed class Item
        {
            public string Program;
            public string Process;
            public string Title;
            public object Tag;
        }

        private readonly List<Item> _items = new List<Item>();
        private int _selected = -1;
        private int _hover = -1;
        private int _scroll;

        public event EventHandler SelectionChanged;

        /// <summary>双击某一行（用于“双击即确定”）。</summary>
        public event EventHandler ItemActivated;

        public FlatRuleList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Ui;
            BackColor = Theme.CardBg;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = (value < 0 || value >= _items.Count) ? -1 : value;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                EventHandler h = SelectionChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Item SelectedItem
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : null; }
        }

        public int Count { get { return _items.Count; } }

        public void SetItems(IEnumerable<Item> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            _selected = -1;
            _scroll = 0;
            _hover = -1;
            Invalidate();
        }

        private int HeaderHeight { get { return Theme.Px(this, 26); } }
        private int RowHeight { get { return Theme.Px(this, 30); } }
        private int BodyHeight { get { return Math.Max(0, Height - HeaderHeight); } }
        private int VisibleRows { get { int rh = RowHeight; return rh <= 0 ? 0 : BodyHeight / rh; } }
        private int MaxScroll { get { return Math.Max(0, _items.Count - VisibleRows); } }

        /// <summary>
        /// 三列的起始 X（低-13：按容器宽度比例分配）。
        /// 旧实现写死逻辑像素，缩放或容器宽度一变，标题列就被挤出可见区域。
        /// </summary>
        private int[] ColumnX()
        {
            int w = Math.Max(0, Width);
            int c1 = Theme.Px(this, 16);
            int c2 = (int)Math.Round(w * 0.34f);
            int c3 = (int)Math.Round(w * 0.66f);
            if (c2 <= c1) c2 = c1 + Theme.Px(this, 90);
            if (c3 <= c2) c3 = c2 + Theme.Px(this, 90);
            return new[] { c1, c2, c3 };
        }

        private int HitTestRow(int y)
        {
            if (y < HeaderHeight) return -1;
            int idx = (y - HeaderHeight) / RowHeight + _scroll;
            return idx >= 0 && idx < _items.Count ? idx : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTestRow(e.Y);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = HitTestRow(e.Y);
            SelectedIndex = i;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = HitTestRow(e.Y);
            if (i < 0) return;
            SelectedIndex = i;
            EventHandler h = ItemActivated;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = e.Delta > 0 ? -1 : 1;
            int next = Math.Max(0, Math.Min(MaxScroll, _scroll + delta));
            if (next != _scroll) { _scroll = next; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);

            // -------- 表头
            int hh = HeaderHeight;
            var header = new Rectangle(0, 0, Width, hh);
            using (var b = new SolidBrush(Color.FromArgb(249, 250, 252)))
                g.FillRectangle(b, header);
            using (var p = new Pen(Theme.Hair))
                g.DrawLine(p, 0, hh - 1, Width, hh - 1);

            int[] cx = ColumnX();
            string[] heads = { "程序", "进程", "标题包含" };
            for (int i = 0; i < heads.Length; i++)
            {
                var r = new Rectangle(cx[i], 0, Width - cx[i], hh);
                TextRenderer.DrawText(g, heads[i], Theme.Small, r, Theme.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // -------- 空状态
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "还没有添加程序，点下面的按钮添加一个", Theme.Ui, ClientRectangle,
                    Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // -------- 行
            int rh = RowHeight;
            int y = hh;
            int first = _scroll;
            int last = Math.Min(_items.Count, first + VisibleRows + 1);

            for (int i = first; i < last; i++)
            {
                var row = new Rectangle(0, y, Width, rh);
                bool sel = i == _selected;

                if (sel)
                {
                    using (var b = new SolidBrush(Theme.AccentSoft)) g.FillRectangle(b, row);
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, new Rectangle(0, row.Top + Theme.Px(this, 6), Theme.Px(this, 3), rh - Theme.Px(this, 12)));
                }
                else if (i == _hover)
                {
                    using (var b = new SolidBrush(Theme.HoverFill)) g.FillRectangle(b, row);
                }

                Item it = _items[i];
                var r1 = new Rectangle(cx[0], row.Top, cx[1] - cx[0] - Theme.Px(this, 8), rh);
                var r2 = new Rectangle(cx[1], row.Top, cx[2] - cx[1] - Theme.Px(this, 8), rh);
                var r3 = new Rectangle(cx[2], row.Top, Width - cx[2] - Theme.Px(this, 8), rh);

                TextRenderer.DrawText(g, it.Program, Theme.Ui, r1, Theme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Process, Theme.Ui, r2, Theme.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Title, Theme.Ui, r3, Theme.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                if (i != last - 1)
                {
                    using (var p = new Pen(Theme.HairSoft))
                        g.DrawLine(p, cx[0], row.Bottom - 1, Width - Theme.Px(this, 12), row.Bottom - 1);
                }
                y += rh;
            }
        }
    }
}

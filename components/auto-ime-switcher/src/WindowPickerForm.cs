using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 选择要加入自动英文列表的程序（V1.0.4 扁平化）。
    ///
    /// 流程：点「从当前窗口选择」→ 弹出本窗口并列出所有有可见窗口的进程
    /// → 用户选中某项 → 确定（或双击）→ 加入规则。
    ///
    /// 关键点：**不依赖弹出瞬间的前台窗口**。用户在弹窗还开着的时候
    /// 可以切到目标程序（SolidWorks / NX 等），再点「刷新」重新抓取，
    /// 目标程序就会出现在列表里。
    /// </summary>
    public sealed class WindowPickerForm : Form
    {
        private FlatRuleList _list;
        private FlatButton _btnRefresh;
        private FlatButton _btnOk;
        private FlatButton _btnCancel;
        private ToggleRow _rowAll;
        private Label _lblHint;

        private readonly float _scale;
        private int S(int v) { return (int)Math.Round(v * _scale); }

        /// <summary>用户确认后选中的条目；取消时保持 null。</summary>
        public WindowEntry Selected { get; private set; }

        /// <param name="owner">主窗口。缩放跟随它所在的显示器，避免副屏不同 DPI 时排版失准。</param>
        public WindowPickerForm(IWin32Window owner)
        {
            IntPtr ignored = Handle;
            GC.KeepAlive(ignored);
            _scale = owner != null ? Theme.ScaleFor(owner as Control) : Theme.Scale;
            if (_scale < 0.5f || _scale > 4f) _scale = Theme.Scale;

            SuspendLayout();

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Font = Theme.Ui;
            Text = "选择程序";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(S(520), S(346));

            var lblTop = new Label
            {
                Text = "请选择要自动切换为英文的程序",
                Font = Theme.UiBold,
                ForeColor = Theme.Ink,
                BackColor = Color.White,
                AutoSize = true,
                Location = new Point(S(14), S(14))
            };
            Controls.Add(lblTop);

            _list = new FlatRuleList
            {
                Location = new Point(S(14), S(40)),
                Size = new Size(S(492), S(26 + 30 * 5))
            };
            _list.ItemActivated += delegate { Accept(); };
            Controls.Add(_list);

            _rowAll = new ToggleRow
            {
                Text = "包含没有标题的窗口（托盘程序等）",
                Location = new Point(S(14), S(226)),
                Size = new Size(S(320), S(32))
            };
            _rowAll.CheckedChanged += delegate { Reload(); };
            Controls.Add(_rowAll);

            _btnRefresh = new FlatButton
            {
                Text = "刷新",
                Location = new Point(S(410), S(227)),
                Size = new Size(S(74), S(30))
            };
            _btnRefresh.Click += delegate { Reload(); };
            Controls.Add(_btnRefresh);

            _lblHint = new Label
            {
                Text = "提示：切到目标程序后再点「刷新」，它就会出现在列表里。",
                Location = new Point(S(14), S(262)),
                Size = new Size(S(492), S(34)),
                Font = Theme.Small,
                ForeColor = Theme.Sub,
                BackColor = Color.White
            };
            Controls.Add(_lblHint);

            _btnOk = new FlatButton
            {
                Text = "确定",
                ButtonStyle = FlatButton.Style.Primary,
                Location = new Point(S(338), S(300)),
                Size = new Size(S(80), S(32))
            };
            _btnOk.Click += delegate { Accept(); };
            Controls.Add(_btnOk);

            _btnCancel = new FlatButton
            {
                Text = "取消",
                ButtonStyle = FlatButton.Style.Ghost,
                Location = new Point(S(426), S(300)),
                Size = new Size(S(80), S(32))
            };
            _btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            // 低-5：以前 Esc 不能取消（CancelButton 不接受自绘按钮），
            // 改在 ProcessCmdKey 里直接处理。

            ResumeLayout(false);
            Reload();
        }

        /// <summary>Esc = 取消（低-5）。自绘 FlatButton 不实现 IButtonControl，
        /// Form.CancelButton 用不了，直接在命令键预处理里拦。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>重新枚举当前可见窗口并重建列表。</summary>
        private void Reload()
        {
            IntPtr keep = IntPtr.Zero;
            if (_list.SelectedItem != null)
            {
                var prev = _list.SelectedItem.Tag as WindowEntry;
                if (prev != null) keep = prev.Hwnd;
            }

            List<WindowEntry> entries;
            try
            {
                entries = WindowEnumerator.EnumerateRunningWindows();
            }
            catch (Exception ex)
            {
                Logger.Warn("枚举窗口失败: " + ex.Message);
                entries = new List<WindowEntry>();
            }

            bool includeUntitled = _rowAll.Checked;
            var items = new List<FlatRuleList.Item>();
            int restored = -1;

            foreach (WindowEntry e in entries)
            {
                if (!includeUntitled && string.IsNullOrWhiteSpace(e.Title)) continue;

                if (keep != IntPtr.Zero && keep == e.Hwnd)
                    restored = items.Count;

                items.Add(new FlatRuleList.Item
                {
                    Program = e.DisplayName,
                    Process = e.ProcessName,
                    Title = string.IsNullOrWhiteSpace(e.Title) ? "（无标题）" : e.Title,
                    Tag = e
                });
            }

            _list.SetItems(items);
            if (restored >= 0) _list.SelectedIndex = restored;
            else if (items.Count > 0) _list.SelectedIndex = 0;

            _lblHint.Text = string.Format(
                "共 {0} 个窗口。切到目标程序后再点「刷新」，它就会出现在列表里。", items.Count);
        }

        private void Accept()
        {
            FlatRuleList.Item sel = _list.SelectedItem;
            if (sel == null)
            {
                MessageBox.Show(this, "请先在列表里选中一个程序。", "选择程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = sel.Tag as WindowEntry;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

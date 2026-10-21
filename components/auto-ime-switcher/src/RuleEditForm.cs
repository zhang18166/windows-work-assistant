using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoImeSwitcher
{
    /// <summary>
    /// 规则编辑对话框（V1.0.6，中-9）。
    ///
    /// 数据模型一直支持"标题包含"条件（同一进程多个窗口时只对标题命中的生效，
    /// 例如 SolidWorks 的零件图和装配体），但界面上没有入口，列表里的
    /// "标题包含"列永远是"（不限）"。这个对话框补上该入口：
    /// 在主界面双击规则行或点"编辑"打开。
    /// </summary>
    public sealed class RuleEditForm : Form
    {
        private readonly TargetRule _rule;
        private readonly float _scale;
        private int S(int v) { return (int)Math.Round(v * _scale); }

        private TextBox _txtDisplay;
        private TextBox _txtProcess;
        private TextBox _txtTitle;
        private FlatButton _btnOk;
        private FlatButton _btnCancel;

        public RuleEditForm(TargetRule rule)
        {
            _rule = rule;

            // 与 MainForm 同理：构造期用屏幕 DC 的 Theme.Scale（稳定），不用
            // CreateGraphics().DpiX（未显示窗口上会返回 96/144 摇摆值）。
            _scale = Theme.Scale;
            if (_scale < 0.5f || _scale > 4f) _scale = 1f;

            SuspendLayout();

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Page;
            Font = Theme.Ui;
            Text = "编辑规则";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(S(440), S(300));

            int mx = S(20);
            int iw = S(400);
            int y = S(20);

            var lblTitle = new Label
            {
                Text = "编辑自动切换规则",
                Font = Theme.Head,
                ForeColor = Theme.Ink,
                BackColor = Theme.Page,
                AutoSize = true,
                Location = new Point(mx, y)
            };
            Controls.Add(lblTitle);
            y += S(28);

            // ---- 卡片：三个字段各 S(58)，上下各留 S(12)
            var card = new Rectangle(mx, y, iw, S(58 * 3 + 24));
            y += S(12);

            AddField(card, "显示名称", ref y,
                _txtDisplay = new TextBox { Text = rule.DisplayName ?? string.Empty },
                "列表里显示的名字，可随意改");

            AddField(card, "进程名（不可改）", ref y,
                _txtProcess = new TextBox { Text = rule.ProcessName ?? string.Empty, ReadOnly = true },
                "匹配依据：可执行文件名");

            AddField(card, "标题包含（可留空）", ref y,
                _txtTitle = new TextBox { Text = rule.TitleContains ?? string.Empty },
                "只在该程序的窗口标题包含这段文字时才切换；留空表示不限标题");

            y = card.Bottom + S(16);

            _btnOk = new FlatButton
            {
                Text = "保存",
                ButtonStyle = FlatButton.Style.Primary,
                Location = new Point(mx + iw - S(168), y),
                Size = new Size(S(80), S(32))
            };
            _btnOk.Click += delegate { Accept(); };
            Controls.Add(_btnOk);

            _btnCancel = new FlatButton
            {
                Text = "取消",
                ButtonStyle = FlatButton.Style.Ghost,
                Location = new Point(mx + iw - S(80), y),
                Size = new Size(S(80), S(32))
            };
            _btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            ResumeLayout(false);
        }

        private void AddField(Rectangle card, string labelText, ref int y, TextBox box, string hint)
        {
            var lbl = new Label
            {
                Text = labelText,
                Font = Theme.Small,
                ForeColor = Theme.Sub,
                BackColor = Theme.CardBg,
                AutoSize = true,
                Location = new Point(card.X + S(16), y)
            };
            Controls.Add(lbl);

            box.Font = Theme.Ui;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Location = new Point(card.X + S(16), y + S(16));
            box.Size = new Size(card.Width - S(32), S(24));
            Controls.Add(box);

            var lblHint = new Label
            {
                Text = hint,
                Font = Theme.Small,
                ForeColor = Theme.Faint,
                BackColor = Theme.CardBg,
                AutoSize = false,
                Location = new Point(card.X + S(16), y + S(42)),
                Size = new Size(card.Width - S(32), S(14))
            };
            Controls.Add(lblHint);

            y += S(58);
        }

        /// <summary>把界面上的值写回规则对象。</summary>
        public void ApplyTo(TargetRule rule)
        {
            rule.DisplayName = _txtDisplay.Text.Trim();
            rule.TitleContains = _txtTitle.Text.Trim();
        }

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(_rule.ProcessName))
            {
                MessageBox.Show(this, "进程名不能为空。", "编辑规则",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Esc = 取消（低-5）。Form.CancelButton 只接受 IButtonControl，自绘按钮不实现该接口，
        /// 所以直接在 ProcessCmdKey 里处理。</summary>
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(Theme.Page))
                g.FillRectangle(b, ClientRectangle);

            // 找出字段卡片区域：与构造函数里的排布保持一致
            var cardRect = new Rectangle(S(20), S(48), S(400), S(58 * 3 + 24));
            using (var path = Theme.Rounded(new RectangleF(cardRect.X + 0.5f, cardRect.Y + 0.5f,
                                                           cardRect.Width - 1f, cardRect.Height - 1f), S(12)))
            {
                using (var b = new SolidBrush(Theme.CardBg)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Hair, 1f)) g.DrawPath(p, path);
            }
        }
    }
}

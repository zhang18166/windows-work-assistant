using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace AutoImeSwitcher
{
    internal static class ActivityCardPainter
    {
        internal const int Width = 168, Height = 34;
        // 预览与生产窗口复用同一绘制方法。
        internal static Bitmap Render(ActivitySnapshot activity, Image icon, float scale, int backgroundTransparencyPercent = 0)
        {
            scale = Math.Max(0.5f, Math.Min(4f, scale));
            // 分层窗口要求预乘 Alpha；背景使用逐像素 Alpha，前景始终保持原始透明度。
            var bitmap = new Bitmap((int)Math.Round(Width * scale), (int)Math.Round(Height * scale), PixelFormat.Format32bppPArgb);
            byte backgroundAlpha = FloatingIndicator.TransparencyToAlpha(backgroundTransparencyPercent);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var state = new Font("Microsoft YaHei UI", 13, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var normal = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.Clear(Color.Transparent);
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var path = Theme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 9))
                {
                    using (var brush = new SolidBrush(Color.FromArgb(backgroundAlpha, 27, 33, 44))) g.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(backgroundAlpha, 86, 98, 118))) g.DrawPath(pen, path);
                }
                Color ink = Color.FromArgb(243, 246, 252);
                Color accent = !activity.Enabled ? Color.FromArgb(167, 177, 194) :
                    activity.Matched ? Color.FromArgb(102, 220, 176) : Color.FromArgb(255, 182, 112);
                if (activity.Enabled && (activity.ShortAction == "失败" || activity.ShortAction == "未知" ||
                    activity.ShortAction == "未验证" || activity.ShortAction == "已跳过"))
                    accent = Color.FromArgb(255, 182, 112);
                if (icon != null) g.DrawImage(icon, new Rectangle(9, 8, 18, 18));
                else g.DrawIcon(SystemIcons.Application, new Rectangle(9, 8, 18, 18));
                string input = activity.InputState == "CAPS" ? "CAP" : activity.InputState == "caps关" ? "cap-" :
                    activity.InputState == "EN" ? "EN" : activity.InputState == "中" ? "中" : "?";
                Text(g, input, state, ink, new RectangleF(36, 3, 40, 28));
                using (var pen = new Pen(Color.FromArgb(backgroundAlpha, 70, 82, 100))) g.DrawLine(pen, 79, 10, 79, 24);
                Text(g, activity.ShortAction, normal, accent, new RectangleF(88, 3, 72, 28));
            }
            return bitmap;
        }
        private static void Text(Graphics g, string text, Font font, Color color, RectangleF bounds)
        {
            using (var brush = new SolidBrush(color))
            using (var outline = new SolidBrush(Color.FromArgb(27, 33, 44)))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                // 固定的细暗色轮廓让无底色时的浅色文字在白色窗口上也能辨认。
                foreach (var offset in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) })
                {
                    var shadowBounds = bounds; shadowBounds.Offset(offset.X, offset.Y);
                    g.DrawString(text ?? "", font, outline, shadowBounds, format);
                }
                g.DrawString(text ?? "", font, brush, bounds, format);
            }
        }
    }
}

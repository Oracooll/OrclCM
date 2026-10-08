using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OrclCM
{
    /// <summary>Draws the live wattage as digits on a tray-sized icon.</summary>
    static class TrayIconRenderer
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static string Text(double? watts)
        {
            if (!watts.HasValue) return "--";
            double w = Math.Abs(watts.Value);
            string text = w < 9.95 ? w.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : w.ToString("0");
            return text.Length > 3 ? "99+" : text;
        }

        /// <summary>Returns an icon that owns its handle; dispose it when replaced.</summary>
        public static Icon Render(double? watts, Color color, int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    float r = size / 5f;
                    using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), r))
                    using (var bg = new SolidBrush(Present.Bg))
                        g.FillPath(bg, path);

                    string text = Text(watts);
                    float bar = Math.Max(1, size / 10f);
                    var area = new RectangleF(0, 0, size, size - bar - 1);
                    using (var fmt = new StringFormat(StringFormat.GenericTypographic)
                           { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var brush = new SolidBrush(color))
                    {
                        // the largest bold font whose digits still fit
                        for (float px = size * 0.8f; px >= 5; px -= 0.5f)
                        {
                            using (var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                var m = g.MeasureString(text, font, PointF.Empty, fmt);
                                if (m.Width <= size - 1 && m.Height <= area.Height + px * 0.15f)
                                {
                                    g.DrawString(text, font, brush, area, fmt);
                                    break;
                                }
                            }
                        }
                        float inset = size / 8f;
                        g.FillRectangle(brush, inset, size - bar - Math.Max(1, size / 16f), size - 2 * inset, bar);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var borrowed = Icon.FromHandle(h))
                        return (Icon)borrowed.Clone();  // the clone owns a copy of the handle
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

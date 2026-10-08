using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OrclCM
{
    /// <summary>Small borderless always-on-top box with just the wattage and status.
    /// Drag it anywhere; double-click opens the full window; right-click shows the menu.</summary>
    sealed class MiniForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

        readonly Font bigFont = new Font("Segoe UI", 15, FontStyle.Bold);
        readonly Font smallFont = new Font("Segoe UI", 8.25f);
        string big = "--", line = "";
        Tone tone = Tone.Fg;

        public event Action OpenRequested;
        public event Action Moved;

        public MiniForm(ContextMenuStrip menu)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(210, 56);
            DoubleBuffered = true;
            ContextMenuStrip = menu;
            Text = AppInfo.Name;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;  // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry
                return cp;
            }
        }

        public void Place(int? x, int? y)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var wanted = new Rectangle(x ?? area.Right - Width - 16, y ?? area.Bottom - Height - 16, Width, Height);
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(Rectangle.Inflate(wanted, -10, -10))) { Location = wanted.Location; return; }
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }

        public void SetView(string big, Tone tone, string line)
        {
            if (big == this.big && tone == this.tone && line == this.line) return;
            this.big = big; this.tone = tone; this.line = line;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = Theme.Current;
            var g = e.Graphics;
            g.Clear(t.Bg);
            using (var border = new Pen(t.ZeroLine)) g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            int bigH = TextRenderer.MeasureText(g, big, bigFont, Size.Empty, flags).Height;
            int lineH = string.IsNullOrEmpty(line) ? 0 : TextRenderer.MeasureText(g, line, smallFont, Size.Empty, flags).Height;
            int y = (ClientSize.Height - bigH - lineH) / 2;
            TextRenderer.DrawText(g, big, bigFont, new Rectangle(4, y, ClientSize.Width - 8, bigH), t.Of(tone), flags);
            if (lineH > 0)
                TextRenderer.DrawText(g, line, smallFont, new Rectangle(4, y + bigH, ClientSize.Width - 8, lineH), t.Dim, flags);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                ReleaseCapture();  // let Windows move the window as if its caption were dragged
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                Moved?.Invoke();
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCLBUTTONDBLCLK = 0xA3;
            if (m.Msg == WM_NCLBUTTONDBLCLK) { OpenRequested?.Invoke(); return; }
            base.WndProc(ref m);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { bigFont.Dispose(); smallFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

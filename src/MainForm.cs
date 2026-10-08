using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OrclCM
{
    /// <summary>The meter window. Tray-only: it has no taskbar button; closing or minimizing
    /// hides it to the tray, and Exit in the tray menu quits.</summary>
    sealed class MainForm : Form
    {
        const int HistoryLength = 120;  // samples kept in the graph (~4 min)
        static readonly Color GraphBg = ColorTranslator.FromHtml("#1e2128"), ZeroLine = ColorTranslator.FromHtml("#3a3f4a");

        readonly Poller poller;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem topMenuItem;
        readonly CheckBox topBox;
        readonly Timer uiTimer;
        readonly List<double?> history = new List<double?>();  // per attempt; null = failed / unavailable
        readonly Font bigFont = new Font("Segoe UI", 34, FontStyle.Bold);
        readonly Font statusFont = new Font("Segoe UI", 12);
        readonly Font detailFont = new Font("Segoe UI", 9.5f);
        readonly Font axisFont = new Font("Segoe UI", 7.5f);
        View view = Present.Describe(null, null, 0);
        int lastSeq = -1;
        string trayKey;
        Icon trayImage;
        bool exiting;

        public MainForm(Poller poller)
        {
            this.poller = poller;
            Text = AppInfo.Name + " " + AppInfo.Version;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(380, 300);
            MinimumSize = new Size(320, 270);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Present.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            topBox = new CheckBox
            {
                Text = "Always on top", ForeColor = Present.Dim, BackColor = Present.Bg, AutoSize = true,
                FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Bottom,
            };
            topBox.FlatAppearance.BorderColor = Present.Dim;
            topBox.CheckedChanged += (s, e) => SetTopMost(topBox.Checked);
            Controls.Add(topBox);

            topMenuItem = new ToolStripMenuItem("Always on top", null, (s, e) => topBox.Checked = !topBox.Checked);
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Show / hide window", null, (s, e) => ToggleWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(topMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));

            tray = new NotifyIcon { Text = AppInfo.Name, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
            UpdateTray();
            tray.Visible = true;

            uiTimer = new Timer { Interval = 500 };
            uiTimer.Tick += (s, e) => RefreshView();
            poller.Start();
            uiTimer.Start();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (topBox != null)
                topBox.Location = new Point((ClientSize.Width - topBox.Width) / 2, ClientSize.Height - topBox.Height - Px(8));
        }

        int Px(float px) => (int)Math.Round(px * DeviceDpi / 96f);

        // --- window / tray behaviour
        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void ToggleWindow()
        {
            if (Visible && WindowState != FormWindowState.Minimized) Hide();
            else ShowFromTray();
        }

        void SetTopMost(bool on)
        {
            TopMost = on;
            topMenuItem.Checked = on;
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();  // minimize -> tray
                WindowState = FormWindowState.Normal;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
            {
                e.Cancel = true;  // X hides to the tray; Exit is in the tray menu
                Hide();
                return;
            }
            uiTimer.Stop();
            poller.Stop();
            tray.Visible = false;
            tray.Dispose();
            trayImage?.Dispose();
            base.OnFormClosing(e);
        }

        // --- live data
        void RefreshView()
        {
            var st = poller.State;
            view = Present.Describe(st.Attempt, st.Good, Clock.Now());
            if (st.Attempt != null && st.Attempt.Seq != lastSeq)
            {
                lastSeq = st.Attempt.Seq;
                history.Add(st.Attempt.Reading?.Watts);
                if (history.Count > HistoryLength) history.RemoveAt(0);
            }
            UpdateTray();
            if (Visible) Invalidate();
        }

        void UpdateTray()
        {
            int size = SystemInformation.SmallIconSize.Width;
            string key = TrayIconRenderer.Text(view.TrayWatts) + "|" + view.TrayColor.ToArgb() + "|" + size;
            if (key != trayKey)
            {
                var old = trayImage;
                trayImage = TrayIconRenderer.Render(view.TrayWatts, view.TrayColor, size);
                tray.Icon = trayImage;
                old?.Dispose();
                trayKey = key;
            }
            string tip = view.Tooltip ?? AppInfo.Name;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;  // NotifyIcon limit
        }

        // --- drawing
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int w = ClientSize.Width, pad = Px(12);
            int y = Px(10);
            y = DrawCentered(g, view.Big, bigFont, view.BigColor, y, w);
            y = DrawCentered(g, view.Status, statusFont, view.StatusColor, y, w - 2 * pad);
            y = DrawCentered(g, view.Detail, detailFont, Present.Dim, y + Px(2), w - 2 * pad);
            var graph = new Rectangle(pad, y + Px(6), w - 2 * pad, topBox.Top - Px(8) - (y + Px(6)));
            if (graph.Height > Px(20))
                DrawGraph(g, graph);
        }

        int DrawCentered(Graphics g, string text, Font font, Color color, int y, int width)
        {
            if (string.IsNullOrEmpty(text)) return y;
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            var size = TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), flags);
            var rect = new Rectangle((ClientSize.Width - width) / 2, y, width, size.Height);
            TextRenderer.DrawText(g, text, font, rect, color, flags);
            return y + size.Height;
        }

        void DrawGraph(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(GraphBg)) g.FillRectangle(bg, r);
            var known = history.FindAll(v => v.HasValue);
            if (known.Count == 0) return;
            double top = 5, bottom = 0;
            foreach (var v in known) { top = Math.Max(top, v.Value); bottom = Math.Min(bottom, v.Value); }
            double span = top - bottom;
            if (span <= 0) span = 1;
            int pad = Px(8);
            Func<double, float> Y = v => (float)(r.Top + pad + (top - v) / span * (r.Height - 2 * pad));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var dash = new Pen(ZeroLine) { DashPattern = new[] { 3f, 3f } })
                g.DrawLine(dash, r.Left, Y(0), r.Right, Y(0));
            TextRenderer.DrawText(g, top.ToString("0") + " W", axisFont, new Point(r.Left + Px(4), r.Top + Px(3)), Present.Dim);

            float step = r.Width / (float)(HistoryLength - 1);
            float x0 = r.Right - step * (history.Count - 1);
            var color = known[known.Count - 1].Value >= 0 ? Present.Green : Present.Orange;
            using (var pen = new Pen(color, Px(2)) { LineJoin = LineJoin.Round })
            using (var dot = new SolidBrush(color))
            {
                var segment = new List<PointF>();  // gaps (failed / unavailable samples) break the line
                for (int i = 0; i <= history.Count; i++)
                {
                    if (i < history.Count && history[i].HasValue)
                    {
                        segment.Add(new PointF(x0 + i * step, Y(history[i].Value)));
                        continue;
                    }
                    if (segment.Count >= 2) g.DrawLines(pen, segment.ToArray());
                    else if (segment.Count == 1) g.FillEllipse(dot, segment[0].X - 1.5f, segment[0].Y - 1.5f, 3, 3);
                    segment.Clear();
                }
            }
            var last = history[history.Count - 1];
            if (last.HasValue)
            {
                float px = x0 + (history.Count - 1) * step, py = Y(last.Value), d = Px(3);
                using (var fg = new SolidBrush(Present.Fg)) g.FillEllipse(fg, px - d, py - d, 2 * d, 2 * d);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                uiTimer.Dispose();
                bigFont.Dispose(); statusFont.Dispose(); detailFont.Dispose(); axisFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

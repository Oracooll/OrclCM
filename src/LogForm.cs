using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OrclCM
{
    /// <summary>Battery log: past sessions (plugged in / on battery / sleep) and the health trend.</summary>
    sealed class LogForm : Form
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly List<BatteryLog.HealthEntry> health;

        public LogForm(List<SessionRecord> sessions, List<BatteryLog.HealthEntry> health, Icon icon)
        {
            this.health = health;
            Text = L.T("Battery log");
            Icon = icon;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Segoe UI", 9);
            ClientSize = new Size(720, 460);
            MinimumSize = new Size(480, 320);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var sessionsTab = new TabPage(L.T("Sessions"));
            var healthTab = new TabPage(L.T("Health"));
            tabs.TabPages.Add(sessionsTab);
            tabs.TabPages.Add(healthTab);

            var list = new ListView { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            foreach (var (name, width, right) in new[] { (L.T("Start"), 130, false), (L.T("Type"), 100, false), (L.T("Duration"), 125, true),
                                                         (L.T("Battery"), 95, true), (L.T("Energy"), 80, true), (L.T("Average"), 75, true), (L.T("Peak"), 70, true) })
                list.Columns.Add(name, width, right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            foreach (var s in sessions.OrderByDescending(s => s.Start).Take(1000))
            {
                var item = new ListViewItem(s.Start.ToString("yyyy-MM-dd HH:mm", Inv));
                item.SubItems.Add(KindName(s.Kind));
                item.SubItems.Add(Estimates.Duration(s.Seconds));
                item.SubItems.Add(s.StartPercent.HasValue && s.EndPercent.HasValue
                    ? s.StartPercent.Value.ToString("0", Inv) + "% → " + s.EndPercent.Value.ToString("0", Inv) + "%" : "");
                item.SubItems.Add(s.EnergyWh.HasValue ? s.EnergyWh.Value.ToString("+0.0;-0.0;0.0", Inv) + " Wh" : "");
                item.SubItems.Add(s.AverageW.HasValue ? s.AverageW.Value.ToString("+0.0;-0.0;0.0", Inv) + " W" : "");
                item.SubItems.Add(s.PeakW.HasValue ? s.PeakW.Value.ToString("+0.0;-0.0;0.0", Inv) + " W" : "");
                list.Items.Add(item);
            }
            if (sessions.Count == 0)
                sessionsTab.Controls.Add(new Label { Text = L.T("No sessions recorded yet."), Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleCenter });
            sessionsTab.Controls.Add(list);
            list.BringToFront();  // the Fill control docks last, below the optional label

            var chart = new Panel { Dock = DockStyle.Top, Height = 190 };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(chart, true);
            chart.Paint += DrawHealth;
            chart.Resize += (s, e) => chart.Invalidate();
            var healthList = new ListView { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            foreach (var (name, width) in new[] { (L.T("Date"), 110), (L.T("Health"), 80), (L.T("Full charge"), 100), (L.T("When new"), 100), (L.T("Cycles"), 70) })
                healthList.Columns.Add(name, width, name == L.T("Date") ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            foreach (var h in health.OrderByDescending(h => h.Date))
            {
                var item = new ListViewItem(h.Date.ToString("yyyy-MM-dd", Inv));
                item.SubItems.Add(h.Percent.ToString("0.0", Inv) + "%");
                item.SubItems.Add(h.FullWh.ToString("0.0", Inv) + " Wh");
                item.SubItems.Add(h.DesignWh.ToString("0.0", Inv) + " Wh");
                item.SubItems.Add(h.Cycles?.ToString(Inv) ?? "");
                healthList.Items.Add(item);
            }
            healthTab.Controls.Add(healthList);
            healthTab.Controls.Add(chart);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            var close = new Button { Text = L.T("Close"), AutoSize = true, DialogResult = DialogResult.OK };
            var folder = new Button { Text = L.T("Open log folder"), AutoSize = true };
            folder.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(BatteryLog.Folder); Process.Start(new ProcessStartInfo(BatteryLog.Folder) { UseShellExecute = true }); }
                catch (Exception) { }
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(folder);
            CancelButton = AcceptButton = close;
            Controls.Add(tabs);
            Controls.Add(buttons);
        }

        public static string KindName(string kind) =>
            kind == SessionRecord.PluggedKind ? L.T("Plugged in") : kind == SessionRecord.BatteryKind ? L.T("On battery") : kind == SessionRecord.SleepKind ? L.T("Sleep") : kind;

        void DrawHealth(object sender, PaintEventArgs e)
        {
            var panel = (Panel)sender;
            var g = e.Graphics;
            var r = new Rectangle(12, 28, panel.Width - 24, panel.Height - 46);
            g.Clear(SystemColors.Window);
            TextRenderer.DrawText(g, L.T("Battery health over time"), new Font(Font, FontStyle.Bold), new Point(10, 6), SystemColors.ControlText);
            using (var frame = new Pen(SystemColors.ControlDark)) g.DrawRectangle(frame, r);
            if (health.Count == 0)
            {
                TextRenderer.DrawText(g, L.T("Health is recorded once a day while OrclCM runs."), Font, r, SystemColors.GrayText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double lo = Math.Min(70, Math.Floor(health.Min(h => h.Percent) / 5) * 5), hi = Math.Max(105, Math.Ceiling(health.Max(h => h.Percent) / 5) * 5);
            DateTime first = health.Min(h => h.Date), last = health.Max(h => h.Date);
            double days = Math.Max(1, (last - first).TotalDays);
            float X(DateTime d) => (float)(r.Left + 8 + (d - first).TotalDays / days * (r.Width - 16));
            float Y(double p) => (float)(r.Bottom - (p - lo) / (hi - lo) * r.Height);

            using (var grid = new Pen(SystemColors.ControlLight))
                for (double p = Math.Ceiling(lo / 10) * 10; p <= hi; p += 10)
                {
                    g.DrawLine(grid, r.Left + 1, Y(p), r.Right - 1, Y(p));
                    TextRenderer.DrawText(g, p.ToString("0", Inv) + "%", Font, new Point(r.Left + 2, (int)Y(p) - 16), SystemColors.GrayText);
                }
            TextRenderer.DrawText(g, first.ToString("yyyy-MM-dd", Inv), Font, new Point(r.Left, r.Bottom + 1), SystemColors.GrayText);
            if (last > first)
                TextRenderer.DrawText(g, last.ToString("yyyy-MM-dd", Inv), Font, new Rectangle(r.Left, r.Bottom + 1, r.Width, 16), SystemColors.GrayText, TextFormatFlags.Right);

            var pts = health.OrderBy(h => h.Date).Select(h => new PointF(X(h.Date), Y(h.Percent))).ToArray();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Light.Charging, 2))
            using (var dot = new SolidBrush(Theme.Light.Charging))
            {
                if (pts.Length > 1) g.DrawLines(pen, pts);
                foreach (var p in pts) g.FillEllipse(dot, p.X - 3, p.Y - 3, 6, 6);
            }
        }
    }
}

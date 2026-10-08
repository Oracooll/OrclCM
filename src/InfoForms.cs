using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace OrclCM
{
    static class InfoForms
    {
        public const string RepoUrl = "https://github.com/Oracooll/OrclCM";

        static Form Dialog(string title, Icon icon)
        {
            return new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96, 96), Font = new Font("Segoe UI", 9), Icon = icon,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
            };
        }

        public static void ShowAbout(IWin32Window owner, Icon icon)
        {
            using (var f = Dialog("About " + AppInfo.Name, icon))
            {
                var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
                if (icon != null)
                    stack.Controls.Add(new PictureBox { Image = new Icon(icon, 48, 48).ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize });
                stack.Controls.Add(new Label { Text = AppInfo.Name, AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) });
                stack.Controls.Add(new Label { Text = "Version " + AppInfo.Version, AutoSize = true });
                stack.Controls.Add(new Label
                {
                    Text = "Live laptop battery charging / draining wattage in your tray.", AutoSize = true, Margin = new Padding(3, 10, 3, 3),
                });
                stack.Controls.Add(new Label
                {
                    Text = "Copyright © 2026 Oracooll. MIT license.\nBased on the MIT-licensed Charge Meter.", AutoSize = true, ForeColor = SystemColors.GrayText,
                });
                var link = new LinkLabel { Text = RepoUrl, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
                link.LinkClicked += (s, e) => Open(RepoUrl);
                stack.Controls.Add(link);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 14, 3, 3) };
                stack.Controls.Add(ok);
                f.AcceptButton = f.CancelButton = ok;
                f.Controls.Add(stack);
                f.ShowDialog(owner);
            }
        }

        const string HelpText =
@"WHAT THE NUMBER MEANS
The big number is the power going into (+) or out of (−) the battery right now, in watts. While charging, the laptop also uses power from the charger, so this is not the charger's total output. It tapers toward 0 W as the battery gets full.

COLOURS
Green = charging. Orange = discharging (on battery, or plugged in but still draining). Grey = plugged in but not charging.
""-- W"" / ""rate unavailable"" means Windows doesn't report the rate - it is never shown as a fake 0 W.
""Stale"" means the last reading failed or is too old; the time of the last good reading and the reason are shown.

TRAY ICON
OrclCM lives in the system tray (no taskbar button). The icon shows the current wattage. Click it to show or hide the window; right-click for the menu. Closing or minimizing the window keeps OrclCM running in the tray - use Exit to quit. Starting OrclCM again just shows its window.
Tip: if the icon is hidden under the ^ arrow, drag it onto the taskbar to keep it visible.

WINDOW
Full in / Empty in: estimate from the last minute's average rate.
Plugged in for / On battery for: session length, energy added or used, average and peak rate for the current session.
Health: how much the battery holds now compared with when it was new, and its charge cycle count (if the battery reports them).
Graph: click 4 min / 1 h / 24 h to change the time span. Gaps are failed or missing readings.

MENU
Click the icon at the top-left of the window (or right-click the tray icon) for: Always on top, Start with Windows, Theme, Graph range, Settings, Export history, Help, About and Exit.

ALERTS (Settings)
Notifications when the battery is charged to a limit (e.g. 80% - good for battery life), when it drops to a low level, and when it drains for a minute although the charger is connected (charger too weak, or charging paused).

EXPORT
Export history saves up to the last 24 hours of readings as a CSV file (opens in Excel).

Settings are stored in %APPDATA%\OrclCM\settings.ini.";

        public static void ShowHelp(IWin32Window owner, Icon icon)
        {
            using (var f = Dialog(AppInfo.Name + " help", icon))
            {
                f.AutoSize = false;
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.ClientSize = new Size(520, 520);
                f.MinimumSize = new Size(360, 300);
                var text = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                    Text = HelpText.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
                };
                var ok = new Button { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 };
                f.Controls.Add(text);
                f.Controls.Add(ok);
                f.AcceptButton = f.CancelButton = ok;
                f.Shown += (s, e) => { text.SelectionLength = 0; ok.Focus(); };
                f.ShowDialog(owner);
            }
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace OrclCM
{
    sealed class SettingsForm : Form
    {
        readonly CheckBox autostart, high, low, drain, sleep;
        readonly NumericUpDown highValue, lowValue;
        readonly ComboBox theme, language;

        public SettingsForm(Settings s, bool autostartEnabled)
        {
            Text = L.T("OrclCM settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Segoe UI", 9);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            autostart = new CheckBox { Text = L.T("Start with Windows (in the tray)"), Checked = autostartEnabled, AutoSize = true };
            theme = Choice(new[] { L.T("System"), L.T("Dark"), L.T("Light") }, (int)s.Theme);
            language = Choice(new[] { L.T("System language"), "English", "Български" }, (int)s.Language);
            high = new CheckBox { Text = L.T("Alert when charged to"), Checked = s.AlertHighEnabled, AutoSize = true };
            highValue = Percent(s.AlertHigh, 50, 100);
            low = new CheckBox { Text = L.T("Alert when battery drops to"), Checked = s.AlertLowEnabled, AutoSize = true };
            lowValue = Percent(s.AlertLow, 5, 50);
            drain = new CheckBox { Text = L.T("Alert when plugged in but the battery is draining"), Checked = s.AlertDraining, AutoSize = true };
            sleep = new CheckBox { Text = L.T("Report battery use during sleep"), Checked = s.AlertSleep, AutoSize = true };

            int row = 0;
            grid.Controls.Add(autostart, 0, row); grid.SetColumnSpan(autostart, 2); row++;
            grid.Controls.Add(Caption(L.T("Theme")), 0, row); grid.Controls.Add(theme, 1, row); row++;
            grid.Controls.Add(Caption(L.T("Language")), 0, row); grid.Controls.Add(language, 1, row); row++;
            grid.Controls.Add(Heading(L.T("Notifications")), 0, row); row++;
            grid.Controls.Add(high, 0, row); grid.Controls.Add(highValue, 1, row); row++;
            grid.Controls.Add(low, 0, row); grid.Controls.Add(lowValue, 1, row); row++;
            grid.Controls.Add(drain, 0, row); grid.SetColumnSpan(drain, 2); row++;
            grid.Controls.Add(sleep, 0, row); grid.SetColumnSpan(sleep, 2); row++;
            high.CheckedChanged += (o, e) => highValue.Enabled = high.Checked;
            low.CheckedChanged += (o, e) => lowValue.Enabled = low.Checked;
            highValue.Enabled = high.Checked;
            lowValue.Enabled = low.Checked;

            var ok = new Button { Text = L.T("OK"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = L.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons, 0, row); grid.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);
        }

        static ComboBox Choice(string[] items, int selected)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected));
            return c;
        }

        static NumericUpDown Percent(int value, int min, int max) =>
            new NumericUpDown { Minimum = min, Maximum = max, Increment = 5, Value = Math.Max(min, Math.Min(max, value)), Width = 60, Anchor = AnchorStyles.Left };

        static Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

        static Label Heading(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };

        public bool AutostartChecked => autostart.Checked;

        public void ApplyTo(Settings s)
        {
            s.AlertHighEnabled = high.Checked;
            s.AlertHigh = (int)highValue.Value;
            s.AlertLowEnabled = low.Checked;
            s.AlertLow = (int)lowValue.Value;
            s.AlertDraining = drain.Checked;
            s.AlertSleep = sleep.Checked;
            s.Theme = (ThemeMode)theme.SelectedIndex;
            s.Language = (LanguageMode)language.SelectedIndex;
        }
    }
}

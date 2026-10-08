using System;
using System.Drawing;
using System.Windows.Forms;

namespace OrclCM
{
    sealed class SettingsForm : Form
    {
        readonly CheckBox autostart, high, low, drain;
        readonly NumericUpDown highValue, lowValue;
        readonly ComboBox theme;

        public SettingsForm(Settings s, bool autostartEnabled)
        {
            Text = AppInfo.Name + " settings";
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
            autostart = new CheckBox { Text = "Start with Windows (in the tray)", Checked = autostartEnabled, AutoSize = true };
            high = new CheckBox { Text = "Alert when charged to", Checked = s.AlertHighEnabled, AutoSize = true };
            highValue = Percent(s.AlertHigh, 50, 100);
            low = new CheckBox { Text = "Alert when battery drops to", Checked = s.AlertLowEnabled, AutoSize = true };
            lowValue = Percent(s.AlertLow, 5, 50);
            drain = new CheckBox { Text = "Alert when plugged in but the battery is draining", Checked = s.AlertDraining, AutoSize = true };

            theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Left };
            theme.Items.AddRange(new object[] { "System", "Dark", "Light" });
            theme.SelectedIndex = (int)s.Theme;

            grid.Controls.Add(autostart, 0, 0); grid.SetColumnSpan(autostart, 2);
            grid.Controls.Add(new Label { Text = "Theme", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(theme, 1, 1);
            grid.Controls.Add(Heading("Notifications"), 0, 2);
            grid.Controls.Add(high, 0, 3); grid.Controls.Add(highValue, 1, 3);
            grid.Controls.Add(low, 0, 4); grid.Controls.Add(lowValue, 1, 4);
            grid.Controls.Add(drain, 0, 5); grid.SetColumnSpan(drain, 2);
            high.CheckedChanged += (o, e) => highValue.Enabled = high.Checked;
            low.CheckedChanged += (o, e) => lowValue.Enabled = low.Checked;
            highValue.Enabled = high.Checked;
            lowValue.Enabled = low.Checked;

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            grid.Controls.Add(buttons, 0, 6); grid.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(grid);
        }

        static NumericUpDown Percent(int value, int min, int max) =>
            new NumericUpDown { Minimum = min, Maximum = max, Increment = 5, Value = Math.Max(min, Math.Min(max, value)), Width = 60, Anchor = AnchorStyles.Left };

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
            s.Theme = (ThemeMode)theme.SelectedIndex;
        }
    }
}

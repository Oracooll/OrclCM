using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OrclCM
{
    /// <summary>The meter window. Tray-only: it has no taskbar button; closing or minimizing
    /// hides it to the tray, and Exit quits. The title-bar icon (window menu), the tray icon's
    /// right-click menu and the mini widget's right-click menu offer the same commands.</summary>
    sealed class MainForm : Form
    {
        // window-menu command ids (WM_SYSCOMMAND ids must keep the low 4 bits clear)
        const int CmdTopMost = 0x100, CmdAutostart = 0x110, CmdThemeSystem = 0x120, CmdThemeDark = 0x130, CmdThemeLight = 0x140,
                  CmdRange0 = 0x150, CmdRange1 = 0x160, CmdRange2 = 0x170, CmdSettings = 0x180, CmdExport = 0x190,
                  CmdHelp = 0x1A0, CmdAbout = 0x1B0, CmdExit = 0x1C0, CmdMini = 0x1D0, CmdLog = 0x1E0,
                  CmdLangSystem = 0x1F0, CmdLangEnglish = 0x200, CmdLangBulgarian = 0x210;
        static readonly string[] RangeKeys = { "4 min", "1 h", "24 h" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly Poller poller;
        readonly Settings settings;
        readonly string settingsPath;
        readonly Autostart autostart = new Autostart();
        readonly History history = new History();
        readonly Session session = new Session();
        readonly SleepTracker sleep = new SleepTracker();
        readonly Alerts alerts = new Alerts();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Timer uiTimer;
        readonly bool startHidden;
        readonly Font bigFont = new Font("Segoe UI", 34, FontStyle.Bold);
        readonly Font statusFont = new Font("Segoe UI", 12);
        readonly Font detailFont = new Font("Segoe UI", 9.5f);
        readonly Font smallFont = new Font("Segoe UI", 8.25f);
        readonly Rectangle[] rangeHit = new Rectangle[3];
        MiniForm mini;
        View view = Present.Describe(null, null, 0);
        string sessionText, healthText, drawText;
        bool drawHigh;
        int lastSeq = -1;
        DateTime healthDay;
        string trayKey;
        Icon trayImage, appIcon;
        bool exiting, dialogOpen;
        Rectangle graphRect;
        int? hoverX;
        readonly List<Plotted> plotted = new List<Plotted>();

        struct PointD { public double X, Y; public double? Pct; public DateTime At; public bool Averaged; }
        struct Plotted { public float X, Y; public PointD P; }

        public MainForm(Poller poller, Settings settings, string settingsPath, bool startHidden)
        {
            this.poller = poller;
            this.settings = settings;
            this.settingsPath = settingsPath;
            this.startHidden = startHidden;
            Theme.Current = Theme.Resolve(settings.Theme);
            L.Set(settings.Language);

            Text = AppInfo.Name + " " + AppInfo.Version;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(430, 440);
            MinimumSize = new Size(340, 380);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = appIcon; } catch { }
            TopMost = settings.TopMost;

            menu = new ContextMenuStrip();
            menu.Opening += (s, e) => UpdateChecks(menu.Items);
            BuildMenu();
            ContextMenuStrip = menu;  // right-click inside the window too
            tray = new NotifyIcon { Text = AppInfo.Name, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };

            ApplyTheme();
            UpdateTray();
            tray.Visible = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            try { autostart.RefreshPath(Application.ExecutablePath); } catch { }
            if (settings.MiniMode) SetMini(true);

            uiTimer = new Timer { Interval = 500 };
            uiTimer.Tick += (s, e) => RefreshView();
            poller.Start();
            uiTimer.Start();
        }

        int Px(float px) => (int)Math.Round(px * DeviceDpi / 96f);

        // ------------------------------------------------------------------ startup / placement
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated)
            {
                CreateHandle();  // started by Windows: live in the tray only
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (settings.WindowX.HasValue && settings.WindowY.HasValue)
            {
                var saved = new Rectangle(settings.WindowX.Value, settings.WindowY.Value, settings.WindowWidth, settings.WindowHeight);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(saved, -40, -40))))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = saved;
                }
            }
        }

        void RememberBounds()
        {
            if (WindowState != FormWindowState.Normal || !Visible) return;
            settings.WindowX = Left; settings.WindowY = Top;
            settings.WindowWidth = Width; settings.WindowHeight = Height;
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            RememberBounds();
            SaveSettings();
        }

        void SaveSettings() => settings.Save(settingsPath);

        // ------------------------------------------------------------------ window / tray behaviour
        public void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        void HideToTray()
        {
            RememberBounds();
            Hide();
        }

        void ToggleWindow()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
            else ShowFromTray();
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
                e.Cancel = true;  // X hides to the tray; Exit is in the menus
                HideToTray();
                SaveSettings();
                return;
            }
            RememberBounds();
            SaveSettings();
            var open = session.Finish();
            if (open != null) BatteryLog.AppendSession(BatteryLog.SessionsPath, open);
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            uiTimer.Stop();
            poller.Stop();
            mini?.Close();
            tray.Visible = false;
            tray.Dispose();
            trayImage?.Dispose();
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ commands (shared by all menus)
        bool IsChecked(int cmd)
        {
            switch (cmd)
            {
                case CmdTopMost: return TopMost;
                case CmdAutostart: return SafeAutostartEnabled();
                case CmdMini: return settings.MiniMode;
                case CmdThemeSystem: return settings.Theme == ThemeMode.System;
                case CmdThemeDark: return settings.Theme == ThemeMode.Dark;
                case CmdThemeLight: return settings.Theme == ThemeMode.Light;
                case CmdLangSystem: return settings.Language == LanguageMode.System;
                case CmdLangEnglish: return settings.Language == LanguageMode.English;
                case CmdLangBulgarian: return settings.Language == LanguageMode.Bulgarian;
                case CmdRange0: case CmdRange1: case CmdRange2:
                    return settings.GraphRange == Settings.GraphRanges[(cmd - CmdRange0) / 0x10];
                default: return false;
            }
        }

        bool Execute(int cmd)
        {
            switch (cmd)
            {
                case CmdTopMost: SetTopMost(!TopMost); break;
                case CmdAutostart: SetAutostart(!SafeAutostartEnabled()); break;
                case CmdMini: SetMini(!settings.MiniMode); break;
                case CmdThemeSystem: SetTheme(ThemeMode.System); break;
                case CmdThemeDark: SetTheme(ThemeMode.Dark); break;
                case CmdThemeLight: SetTheme(ThemeMode.Light); break;
                case CmdLangSystem: SetLanguage(LanguageMode.System); break;
                case CmdLangEnglish: SetLanguage(LanguageMode.English); break;
                case CmdLangBulgarian: SetLanguage(LanguageMode.Bulgarian); break;
                case CmdRange0: case CmdRange1: case CmdRange2: SetRange((cmd - CmdRange0) / 0x10); break;
                case CmdLog: ShowLog(); break;
                case CmdSettings: ShowSettings(); break;
                case CmdExport: ExportHistory(); break;
                case CmdHelp: RunDialog(() => InfoForms.ShowHelp(DialogOwner, appIcon)); break;
                case CmdAbout: RunDialog(() => InfoForms.ShowAbout(DialogOwner, appIcon)); break;
                case CmdExit: exiting = true; Close(); break;
                default: return false;
            }
            return true;
        }

        IWin32Window DialogOwner => Visible ? this : null;

        void RunDialog(Action show)
        {
            if (dialogOpen) return;
            dialogOpen = true;
            try { show(); } finally { dialogOpen = false; }
        }

        void SetTopMost(bool on)
        {
            TopMost = on;
            settings.TopMost = on;
            SaveSettings();
        }

        bool SafeAutostartEnabled()
        {
            try { return autostart.Enabled; } catch { return false; }
        }

        void SetAutostart(bool on)
        {
            try { autostart.Set(on, Application.ExecutablePath); }
            catch (Exception ex) { MessageBox.Show(DialogOwner, L.F("Could not change Start with Windows:\n{0}", ex.Message), AppInfo.Name); }
        }

        void SetTheme(ThemeMode mode)
        {
            settings.Theme = mode;
            SaveSettings();
            ApplyTheme();
        }

        void SetLanguage(LanguageMode mode)
        {
            settings.Language = mode;
            SaveSettings();
            ApplyLanguage();
        }

        void ApplyLanguage()
        {
            L.Set(settings.Language);
            BuildMenu();
            if (IsHandleCreated) BuildSystemMenu(rebuild: true);
            RefreshView();
            Invalidate();
        }

        void SetRange(int index)
        {
            settings.GraphRange = Settings.GraphRanges[index];
            SaveSettings();
            Invalidate();
        }

        void SetMini(bool on)
        {
            settings.MiniMode = on;
            SaveSettings();
            if (on && mini == null)
            {
                mini = new MiniForm(menu);
                mini.Place(settings.MiniX, settings.MiniY);
                mini.OpenRequested += ShowFromTray;
                mini.Moved += () => { settings.MiniX = mini.Left; settings.MiniY = mini.Top; SaveSettings(); };
                mini.Show();
                UpdateMini();
            }
            else if (!on && mini != null)
            {
                mini.Close();
                mini.Dispose();
                mini = null;
            }
        }

        void ShowSettings()
        {
            RunDialog(() =>
            {
                using (var f = new SettingsForm(settings, SafeAutostartEnabled()) { Icon = appIcon })
                {
                    if (f.ShowDialog(DialogOwner) != DialogResult.OK) return;
                    var language = settings.Language;
                    f.ApplyTo(settings);
                    if (f.AutostartChecked != SafeAutostartEnabled()) SetAutostart(f.AutostartChecked);
                    SaveSettings();
                    ApplyTheme();
                    if (settings.Language != language) ApplyLanguage();
                }
            });
        }

        void ShowLog()
        {
            RunDialog(() =>
            {
                using (var f = new LogForm(BatteryLog.LoadSessions(BatteryLog.SessionsPath), BatteryLog.LoadHealth(BatteryLog.HealthPath), appIcon))
                    f.ShowDialog(DialogOwner);
            });
        }

        void ExportHistory()
        {
            RunDialog(() =>
            {
                if (history.Count == 0)
                {
                    MessageBox.Show(DialogOwner, L.T("No readings recorded yet."), AppInfo.Name);
                    return;
                }
                using (var dlg = new SaveFileDialog
                {
                    Title = L.T("Export battery history"), Filter = L.T("CSV file (*.csv)|*.csv"), DefaultExt = "csv",
                    FileName = AppInfo.Name + "-history-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv) + ".csv",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                })
                {
                    if (dlg.ShowDialog(DialogOwner) != DialogResult.OK) return;
                    try { File.WriteAllText(dlg.FileName, History.ToCsv(history.All), new UTF8Encoding(true)); }
                    catch (Exception ex) { MessageBox.Show(DialogOwner, L.F("Could not save the file:\n{0}", ex.Message), AppInfo.Name); }
                }
            });
        }

        // ------------------------------------------------------------------ tray / mini / window right-click menu
        void BuildMenu()
        {
            menu.Items.Clear();
            ToolStripMenuItem Item(string key, int cmd) =>
                new ToolStripMenuItem(L.T(key), null, (s, e) => Execute(cmd)) { Tag = cmd };

            var show = new ToolStripMenuItem(L.T("Show / hide window"), null, (s, e) => ToggleWindow());
            show.Font = new Font(menu.Font, FontStyle.Bold);
            var theme = new ToolStripMenuItem(L.T("Theme"));
            theme.DropDownItems.AddRange(new ToolStripItem[] { Item("System", CmdThemeSystem), Item("Dark", CmdThemeDark), Item("Light", CmdThemeLight) });
            var language = new ToolStripMenuItem(L.T("Language"));
            language.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("System language", CmdLangSystem),
                new ToolStripMenuItem("English", null, (s, e) => Execute(CmdLangEnglish)) { Tag = CmdLangEnglish },
                new ToolStripMenuItem("Български", null, (s, e) => Execute(CmdLangBulgarian)) { Tag = CmdLangBulgarian },
            });
            var range = new ToolStripMenuItem(L.T("Graph range"));
            for (int i = 0; i < 3; i++) range.DropDownItems.Add(Item(RangeKeys[i], CmdRange0 + i * 0x10));

            menu.Items.AddRange(new ToolStripItem[]
            {
                show, new ToolStripSeparator(),
                Item("Always on top", CmdTopMost), Item("Start with Windows", CmdAutostart), Item("Mini widget", CmdMini),
                theme, language, range,
                new ToolStripSeparator(),
                Item("Battery log…", CmdLog), Item("Settings…", CmdSettings), Item("Export history…", CmdExport),
                new ToolStripSeparator(),
                Item("Help", CmdHelp), Item("About OrclCM", CmdAbout),
                new ToolStripSeparator(),
                Item("Exit", CmdExit),
            });
        }

        void UpdateChecks(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem mi)
                {
                    if (mi.Tag is int cmd) mi.Checked = IsChecked(cmd);
                    UpdateChecks(mi.DropDownItems);
                }
            }
        }

        // ------------------------------------------------------------------ window menu (title-bar icon)
        [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string text);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] static extern int CheckMenuItem(IntPtr menu, int id, int flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int MF_STRING = 0, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_UNCHECKED = 0;
        const int WM_SYSCOMMAND = 0x112, WM_INITMENUPOPUP = 0x117;
        static readonly int[] CheckableCommands =
            { CmdTopMost, CmdAutostart, CmdMini, CmdThemeSystem, CmdThemeDark, CmdThemeLight,
              CmdLangSystem, CmdLangEnglish, CmdLangBulgarian, CmdRange0, CmdRange1, CmdRange2 };
        IntPtr systemMenu;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BuildSystemMenu(rebuild: false);
            ApplyTitleBarTheme();
        }

        void BuildSystemMenu(bool rebuild)
        {
            if (rebuild) GetSystemMenu(Handle, true);  // back to the standard window menu
            systemMenu = GetSystemMenu(Handle, false);
            void Add(IntPtr m, int cmd, string text) => AppendMenu(m, MF_STRING, (IntPtr)cmd, text);
            void Sep() => AppendMenu(systemMenu, MF_SEPARATOR, IntPtr.Zero, null);

            Sep();
            Add(systemMenu, CmdTopMost, L.T("Always on top"));
            Add(systemMenu, CmdAutostart, L.T("Start with Windows"));
            Add(systemMenu, CmdMini, L.T("Mini widget"));
            IntPtr theme = CreatePopupMenu();
            Add(theme, CmdThemeSystem, L.T("System"));
            Add(theme, CmdThemeDark, L.T("Dark"));
            Add(theme, CmdThemeLight, L.T("Light"));
            AppendMenu(systemMenu, MF_POPUP, theme, L.T("Theme"));
            IntPtr language = CreatePopupMenu();
            Add(language, CmdLangSystem, L.T("System language"));
            Add(language, CmdLangEnglish, "English");
            Add(language, CmdLangBulgarian, "Български");
            AppendMenu(systemMenu, MF_POPUP, language, L.T("Language"));
            IntPtr range = CreatePopupMenu();
            for (int i = 0; i < 3; i++) Add(range, CmdRange0 + i * 0x10, L.T(RangeKeys[i]));
            AppendMenu(systemMenu, MF_POPUP, range, L.T("Graph range"));
            Sep();
            Add(systemMenu, CmdLog, L.T("Battery log…"));
            Add(systemMenu, CmdSettings, L.T("Settings…"));
            Add(systemMenu, CmdExport, L.T("Export history…"));
            Sep();
            Add(systemMenu, CmdHelp, L.T("Help"));
            Add(systemMenu, CmdAbout, L.T("About OrclCM"));
            Sep();
            Add(systemMenu, CmdExit, L.T("Exit OrclCM"));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INITMENUPOPUP && systemMenu != IntPtr.Zero)
            {
                foreach (int cmd in CheckableCommands)  // MF_BYCOMMAND also reaches the submenus
                    CheckMenuItem(systemMenu, cmd, IsChecked(cmd) ? MF_CHECKED : MF_UNCHECKED);
            }
            else if (m.Msg == WM_SYSCOMMAND && Execute((int)(m.WParam.ToInt64() & 0xFFF0)))
            {
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ theme
        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (settings.Theme == ThemeMode.System && IsHandleCreated)
                BeginInvoke((Action)ApplyTheme);
        }

        void ApplyTheme()
        {
            Theme.Current = Theme.Resolve(settings.Theme);
            BackColor = Theme.Current.Bg;
            ApplyTitleBarTheme();
            mini?.Invalidate();
            Invalidate(true);
        }

        int titleBarDark = -1;

        void ApplyTitleBarTheme()
        {
            int dark = Theme.Current.IsDark ? 1 : 0;
            if (!IsHandleCreated || dark == titleBarDark) return;
            bool repaint = titleBarDark != -1 && Visible;
            titleBarDark = dark;
            try { DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4); } catch { }
            if (repaint) { Hide(); Show(); }  // the frame only repaints its colour on re-show
        }

        // ------------------------------------------------------------------ live data
        void RefreshView()
        {
            var st = poller.State;
            double now = Clock.Now();
            Reading good = st.Good?.Reading;

            if (st.Attempt != null && st.Attempt.Seq != lastSeq)
            {
                lastSeq = st.Attempt.Seq;
                var r = st.Attempt.Reading;
                var at = DateTime.Now;
                string label = null;
                if (r != null) Present.Classify(r, out _, out label);
                history.Add(new Sample { At = at, Time = st.Attempt.Time, Watts = r?.Watts, Percent = r?.Percent, Plugged = r?.Plugged, State = label ?? L.T("read failed") });

                var slept = sleep.Observe(at, r);
                if (slept != null)
                {
                    BatteryLog.AppendSession(BatteryLog.SessionsPath, slept);
                    if (settings.AlertSleep && slept.StartPercent - slept.EndPercent >= 1)
                        tray.ShowBalloonTip(10000, L.T("Battery use during sleep"), SleepTracker.Text(slept), ToolTipIcon.Info);
                }
                if (r != null)
                {
                    var ended = session.Add(st.Attempt.Time, at, r.Watts, r.Percent, r.Plugged);
                    if (ended != null) BatteryLog.AppendSession(BatteryLog.SessionsPath, ended);
                    if (healthDay != at.Date && r.FullWh.HasValue)
                    {
                        healthDay = at.Date;
                        BatteryLog.RecordHealth(BatteryLog.HealthPath, at, r);
                    }
                    foreach (var a in alerts.Check(r, settings, now))
                        tray.ShowBalloonTip(10000, a.Title, a.Text, ToolTipIcon.Warning);
                }
            }

            string timeLeft = good != null ? Estimates.TimeLeft(good, history.AverageWatts(now, 60)) : null;
            view = Present.Describe(st.Attempt, st.Good, now, timeLeft);
            bool fresh = view.TrayWatts.HasValue;
            drawText = good != null && fresh ? Estimates.SystemDraw(good, history.AverageWatts(now, 30), session, now, out drawHigh) : null;
            sessionText = session.Text(now);
            healthText = good != null ? Estimates.Health(good) : null;
            UpdateTray();
            UpdateMini();
            if (Visible) Invalidate();
        }

        void UpdateMini()
        {
            if (mini == null) return;
            var r = poller.State.Good?.Reading;
            string pct = r?.Percent != null ? r.Percent.Value.ToString("0", Inv) + "% · " : "";
            mini.SetView(view.Big, view.BigTone, pct + view.Status);
        }

        void UpdateTray()
        {
            int size = SystemInformation.SmallIconSize.Width;
            var color = Theme.Dark.Of(view.TrayTone);  // the tray badge is always dark
            string key = TrayIconRenderer.Text(view.TrayWatts) + "|" + color.ToArgb() + "|" + size;
            if (key != trayKey)
            {
                var old = trayImage;
                trayImage = TrayIconRenderer.Render(view.TrayWatts, color, size);
                tray.Icon = trayImage;
                old?.Dispose();
                trayKey = key;
            }
            string tip = view.Tooltip ?? AppInfo.Name;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;  // NotifyIcon limit
        }

        // ------------------------------------------------------------------ drawing
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            var t = Theme.Current;
            int w = ClientSize.Width, pad = Px(12), inner = w - 2 * pad;
            int y = Px(8);
            y = DrawCentered(g, view.Big, bigFont, t.Of(view.BigTone), y, w);
            y = DrawCentered(g, view.Status, statusFont, t.Of(view.StatusTone), y, inner);
            y = DrawCentered(g, view.Detail, detailFont, t.Dim, y + Px(2), inner);
            y = DrawCentered(g, drawText, detailFont, drawHigh ? t.Discharging : t.Dim, y + Px(1), inner);
            y = DrawCentered(g, sessionText, smallFont, t.Dim, y + Px(6), inner);
            y = DrawCentered(g, healthText, smallFont, t.Dim, y + Px(2), inner);
            graphRect = new Rectangle(pad, y + Px(8), inner, ClientSize.Height - pad - (y + Px(8)));
            plotted.Clear();
            if (graphRect.Height > Px(30))
                DrawGraph(g, graphRect);
            else
                for (int i = 0; i < rangeHit.Length; i++) rangeHit[i] = Rectangle.Empty;
        }

        int DrawCentered(Graphics g, string text, Font font, Color color, int y, int width)
        {
            if (string.IsNullOrEmpty(text)) return y;
            text = KeepUnits(text);
            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
            var size = TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), flags);
            TextRenderer.DrawText(g, text, font, new Rectangle((ClientSize.Width - width) / 2, y, width, size.Height), color, flags);
            return y + size.Height;
        }

        /// <summary>Non-breaking space between a number and its unit, so lines wrap at the separators.</summary>
        static string KeepUnits(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"(\d) (Wh|W|min|h|s|мин|ч|с)\b", "$1 $2");

        void DrawGraph(Graphics g, Rectangle r)
        {
            var t = Theme.Current;
            using (var bg = new SolidBrush(t.GraphBg)) g.FillRectangle(bg, r);
            DrawRangeButtons(g, r);

            double now = Clock.Now(), span = settings.GraphRange;
            var pts = Series(now, span, r.Width);
            var known = pts.Where(p => p.HasValue).Select(p => p.Value).ToList();
            if (known.Count == 0) return;
            double top = Math.Max(5, known.Max(p => p.Y)), bottom = Math.Min(0, known.Min(p => p.Y));
            double height = top - bottom;
            if (height <= 0) height = 1;
            int vpad = Px(8), header = Px(16);
            float Y(double v) => (float)(r.Top + header + (top - v) / height * (r.Height - header - vpad));
            float X(double time) => (float)(r.Right - (now - time) / span * r.Width);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var dash = new Pen(t.ZeroLine) { DashPattern = new[] { 3f, 3f } })
                g.DrawLine(dash, r.Left, Y(0), r.Right, Y(0));
            TextRenderer.DrawText(g, top.ToString("0", Inv) + " W", smallFont, new Point(r.Left + Px(4), r.Top + Px(3)), t.Dim);

            var last = known[known.Count - 1];
            var color = last.Y >= 0 ? t.Charging : t.Discharging;
            using (var pen = new Pen(color, Px(2)) { LineJoin = LineJoin.Round })
            using (var dot = new SolidBrush(color))
            {
                var segment = new List<PointF>();  // gaps (failed readings, sleep) break the line
                foreach (var p in pts.Concat(new PointD?[] { null }))
                {
                    if (p.HasValue)
                    {
                        var pf = new PointF(X(p.Value.X), Y(p.Value.Y));
                        segment.Add(pf);
                        plotted.Add(new Plotted { X = pf.X, Y = pf.Y, P = p.Value });
                        continue;
                    }
                    if (segment.Count >= 2) g.DrawLines(pen, segment.ToArray());
                    else if (segment.Count == 1) g.FillEllipse(dot, segment[0].X - 1.5f, segment[0].Y - 1.5f, 3, 3);
                    segment.Clear();
                }
            }
            if (pts.Count > 0 && pts[pts.Count - 1].HasValue)
            {
                float px = X(last.X), py = Y(last.Y), d = Px(3);
                using (var fg = new SolidBrush(t.Fg)) g.FillEllipse(fg, px - d, py - d, 2 * d, 2 * d);
            }
            DrawHover(g, r);
        }

        void DrawHover(Graphics g, Rectangle r)
        {
            if (!hoverX.HasValue || plotted.Count == 0) return;
            var t = Theme.Current;
            var hit = plotted.OrderBy(p => Math.Abs(p.X - hoverX.Value)).First();
            if (Math.Abs(hit.X - hoverX.Value) > Px(24)) return;
            using (var line = new Pen(t.Dim) { DashPattern = new[] { 2f, 2f } })
                g.DrawLine(line, hit.X, r.Top + Px(16), hit.X, r.Bottom - 1);
            float d = Px(4);
            using (var fg = new SolidBrush(t.Fg)) g.FillEllipse(fg, hit.X - d, hit.Y - d, 2 * d, 2 * d);

            string text = (hit.P.Averaged ? "≈ " + hit.P.At.ToString("HH:mm", Inv) : hit.P.At.ToString("HH:mm:ss", Inv))
                        + "   " + Present.FormatWatts(hit.P.Y)
                        + (hit.P.Pct.HasValue ? "   " + hit.P.Pct.Value.ToString("0", Inv) + "%" : "");
            var size = TextRenderer.MeasureText(g, text, smallFont);
            var box = new Rectangle((int)hit.X + Px(8), r.Top + Px(28), size.Width + Px(10), size.Height + Px(6));
            if (box.Right > r.Right - Px(2)) box.X = (int)hit.X - Px(8) - box.Width;
            using (var bg = new SolidBrush(t.Bg)) g.FillRectangle(bg, box);
            using (var border = new Pen(t.ZeroLine)) g.DrawRectangle(border, box);
            TextRenderer.DrawText(g, text, smallFont, new Point(box.X + Px(5), box.Y + Px(3)), t.Fg);
        }

        /// <summary>Points to plot (null = gap). Long ranges are averaged into about one point per 2 px.</summary>
        List<PointD?> Series(double now, double span, int widthPx)
        {
            var samples = history.Since(now - span).ToList();
            var result = new List<PointD?>();
            int buckets = Math.Max(10, widthPx / Px(2));
            const double gap = 10;  // seconds without a sample = a gap in the line
            if (samples.Count <= buckets)
            {
                double prev = double.NaN;
                foreach (var s in samples)
                {
                    if (!double.IsNaN(prev) && s.Time - prev > gap) result.Add(null);
                    result.Add(s.Watts.HasValue ? new PointD { X = s.Time, Y = s.Watts.Value, Pct = s.Percent, At = s.At } : (PointD?)null);
                    prev = s.Time;
                }
                return result;
            }
            double width = span / buckets;
            var sums = new double[buckets];
            var pcts = new double[buckets];
            var counts = new int[buckets];
            var pctCounts = new int[buckets];
            foreach (var s in samples)
            {
                if (!s.Watts.HasValue) continue;
                int i = Math.Min(buckets - 1, Math.Max(0, (int)((s.Time - (now - span)) / width)));
                sums[i] += s.Watts.Value;
                counts[i]++;
                if (s.Percent.HasValue) { pcts[i] += s.Percent.Value; pctCounts[i]++; }
            }
            var wallNow = DateTime.Now;
            for (int i = 0; i < buckets; i++)
            {
                if (counts[i] == 0) { result.Add(null); continue; }
                double x = now - span + (i + 0.5) * width;
                result.Add(new PointD
                {
                    X = x, Y = sums[i] / counts[i], Pct = pctCounts[i] > 0 ? pcts[i] / pctCounts[i] : (double?)null,
                    At = wallNow.AddSeconds(x - now), Averaged = true,
                });
            }
            return result;
        }

        void DrawRangeButtons(Graphics g, Rectangle r)
        {
            var t = Theme.Current;
            int x = r.Right - Px(4);
            for (int i = RangeKeys.Length - 1; i >= 0; i--)
            {
                string name = L.T(RangeKeys[i]);
                var size = TextRenderer.MeasureText(g, name, smallFont);
                x -= size.Width + Px(4);
                rangeHit[i] = new Rectangle(x - Px(2), r.Top + Px(2), size.Width + Px(4), size.Height + Px(2));
                bool active = settings.GraphRange == Settings.GraphRanges[i];
                if (active)
                    using (var b = new SolidBrush(t.ZeroLine)) g.FillRectangle(b, rangeHit[i]);
                TextRenderer.DrawText(g, name, smallFont, new Point(x, r.Top + Px(3)), active ? t.Fg : t.Dim);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < rangeHit.Length; i++)
                if (rangeHit[i].Contains(e.Location)) SetRange(i);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool onButton = rangeHit.Any(h => h.Contains(e.Location));
            Cursor = onButton ? Cursors.Hand : Cursors.Default;
            int? x = !onButton && graphRect.Contains(e.Location) ? e.X : (int?)null;
            if (x != hoverX) { hoverX = x; Invalidate(graphRect); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverX.HasValue) { hoverX = null; Invalidate(graphRect); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                uiTimer.Dispose();
                menu.Dispose();
                mini?.Dispose();
                bigFont.Dispose(); statusFont.Dispose(); detailFont.Dispose(); smallFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

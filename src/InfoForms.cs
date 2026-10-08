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

        public static void ShowAbout(IWin32Window owner, Icon icon, Action checkForUpdates)
        {
            using (var f = Dialog(L.T("About OrclCM"), icon))
            using (var logo = icon != null ? new Icon(icon, 48, 48).ToBitmap() : null)
            {
                var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
                if (logo != null)
                    stack.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.AutoSize });
                stack.Controls.Add(new Label { Text = AppInfo.Name, AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) });
                stack.Controls.Add(new Label { Text = L.F("Version {0}", AppInfo.Version), AutoSize = true });
                stack.Controls.Add(new Label
                {
                    Text = L.T("Live laptop battery charging / draining wattage in your tray."), AutoSize = true, Margin = new Padding(3, 10, 3, 3),
                });
                stack.Controls.Add(new Label
                {
                    Text = L.T("Copyright © 2026 Oracooll. MIT license.\nBased on the MIT-licensed Charge Meter."), AutoSize = true, ForeColor = SystemColors.GrayText,
                });
                var link = new LinkLabel { Text = RepoUrl, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
                link.LinkClicked += (s, e) => Open(RepoUrl);
                stack.Controls.Add(link);
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
                var ok = new Button { Text = L.T("OK"), DialogResult = DialogResult.OK, AutoSize = true };
                var update = new Button { Text = L.T("Check for updates"), AutoSize = true };
                bool wantUpdate = false;
                update.Click += (s, e) => { wantUpdate = true; f.Close(); };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(update);
                stack.Controls.Add(buttons);
                f.AcceptButton = f.CancelButton = ok;
                f.Controls.Add(stack);
                f.ShowDialog(owner);
                if (wantUpdate) checkForUpdates?.Invoke();
            }
        }

        const string HelpEnglish =
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
Laptop using: on battery, the drain is the whole laptop's power use. ""Higher than usual"" means well above this session's average - something is working hard.
Plugged in for / On battery for: session length, energy added or used, average and peak rate.
Health: how much the battery holds now compared with when it was new, and its charge cycle count (if the battery reports them).
Graph: point at it to see the time, rate and battery level at that moment. Click 4 min / 1 h / 24 h to change the time span. Gaps are failed or missing readings.

MENU
Click the icon at the top-left of the window (or right-click the tray icon or the window) for: Always on top, Start with Windows, Mini widget, Theme, Language, Graph range, Battery log, Settings, Export history, Help, About and Exit.

MINI WIDGET
A small always-on-top box with just the wattage. Drag it anywhere, double-click it to open the full window, right-click it for the menu.

SLEEP REPORT
After the laptop wakes up, OrclCM tells you how much battery was used while it was asleep (can be turned off in Settings). Big losses during sleep usually mean something keeps waking the laptop.

BATTERY LOG
Every plugged-in, on-battery and sleep period is saved, together with a daily battery-health record. Battery log shows the sessions and a chart of battery health over time. The files (sessions.csv, health.csv) are in %APPDATA%\OrclCM and open in Excel.

ALERTS (Settings)
Notifications when the battery is charged to a limit (e.g. 80% - good for battery life), when it drops to a low level, and when it drains for a minute although the charger is connected (charger too weak, or charging paused).

EXPORT
Export history saves up to the last 24 hours of readings as a CSV file (opens in Excel).

UPDATES
Check for updates (menu, or the button in About) looks for a newer OrclCM on GitHub and can download and install it - the download is checked against the published checksum, then OrclCM restarts. OrclCM also checks once a day by itself (can be turned off in Settings).

Settings are stored in %APPDATA%\OrclCM\settings.ini.";

        const string HelpBulgarian =
@"КАКВО ОЗНАЧАВА ЧИСЛОТО
Голямото число е мощността, която влиза в батерията (+) или излиза от нея (−) в момента, във ватове. Докато се зарежда, лаптопът също консумира енергия от зарядното, затова това не е общата мощност на зарядното. Числото намалява към 0 W, когато батерията наближи пълен заряд.

ЦВЕТОВЕ
Зелено = зарежда се. Оранжево = разрежда се (на батерия или включено, но батерията пак се разрежда). Сиво = включено, но не се зарежда.
„-- W“ / „мощността не е налична“ означава, че Windows не отчита мощността – тя никога не се показва като фалшиви 0 W.
„Остаряло“ означава, че последното отчитане е неуспешно или твърде старо; показват се часът на последното успешно отчитане и причината.

ИКОНА В ОБЛАСТТА ЗА УВЕДОМЯВАНЕ
OrclCM стои в областта за уведомяване (без бутон в лентата на задачите). Иконата показва текущата мощност. Щракнете върху нея, за да покажете или скриете прозореца; десен бутон отваря менюто. Затварянето или минимизирането на прозореца оставя OrclCM да работи – използвайте „Изход“, за да го спрете. Повторното стартиране на OrclCM само показва прозореца му.
Съвет: ако иконата е скрита под стрелката ^, плъзнете я в лентата на задачите, за да се вижда винаги.

ПРОЗОРЕЦ
Пълна след / Изтощена след: оценка по средната мощност за последната минута.
Лаптопът консумира: на батерия разходът е консумацията на целия лаптоп. „Повече от обичайното“ означава доста над средното за текущата сесия – нещо натоварва лаптопа.
Включено от / На батерия от: продължителност на сесията, добавена или изразходвана енергия, средна и пикова мощност.
Състояние: колко енергия побира батерията сега спрямо когато е била нова, и броят цикли на зареждане (ако батерията ги отчита).
Графика: посочете я, за да видите часа, мощността и заряда в този момент. Щракнете 4 мин / 1 ч / 24 ч, за да смените периода. Прекъсванията са неуспешни или липсващи отчитания.

МЕНЮ
Щракнете иконата горе вляво на прозореца (или десен бутон върху иконата в областта за уведомяване или върху прозореца) за: Винаги отгоре, Стартиране с Windows, Мини прозорче, Тема, Език, Обхват на графиката, Дневник на батерията, Настройки, Експорт на историята, Помощ, За OrclCM и Изход.

МИНИ ПРОЗОРЧЕ
Малко прозорче винаги отгоре, само с мощността. Плъзнете го където искате, щракнете два пъти за пълния прозорец, десен бутон за менюто.

ОТЧЕТ ЗА СЪН
След събуждане на лаптопа OrclCM показва колко батерия е изразходвана по време на сън (може да се изключи в Настройки). Голям разход по време на сън обикновено означава, че нещо събужда лаптопа.

ДНЕВНИК НА БАТЕРИЯТА
Всеки период на зареждане, на батерия и на сън се записва, заедно с ежедневен запис на състоянието на батерията. „Дневник на батерията“ показва сесиите и графика на състоянието във времето. Файловете (sessions.csv, health.csv) са в %APPDATA%\OrclCM и се отварят с Excel.

ИЗВЕСТИЯ (Настройки)
Известия, когато батерията се зареди до зададена граница (напр. 80% – добре за живота на батерията), когато падне до ниско ниво и когато се разрежда една минута, въпреки че зарядното е включено (зарядното е твърде слабо или зареждането е спряно).

ЕКСПОРТ
„Експорт на историята“ записва до последните 24 часа отчитания като CSV файл (отваря се с Excel).

АКТУАЛИЗАЦИИ
„Проверка за актуализации“ (в менюто или бутонът в „За OrclCM“) търси по-нова версия на OrclCM в GitHub и може да я изтегли и инсталира – изтеглянето се проверява спрямо публикуваната контролна сума, след което OrclCM се рестартира. OrclCM проверява и сам веднъж дневно (може да се изключи в Настройки).

Настройките се пазят в %APPDATA%\OrclCM\settings.ini.";

        public static void ShowHelp(IWin32Window owner, Icon icon)
        {
            using (var f = Dialog(L.T("OrclCM help"), icon))
            {
                f.AutoSize = false;
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.ClientSize = new Size(560, 560);
                f.MinimumSize = new Size(360, 300);
                var text = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                    Text = (L.Bulgarian ? HelpBulgarian : HelpEnglish).Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
                };
                var ok = new Button { Text = L.T("Close"), DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 30 };
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

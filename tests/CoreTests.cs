// Regression tests for OrclCM's logic. build.bat compiles and runs them before building the app.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace OrclCM.Tests
{
    static class CoreTests
    {
        static int failures, passed;

        static void Check(bool ok, string name, object detail = null)
        {
            if (ok) { passed++; return; }
            failures++;
            Console.WriteLine("FAIL: " + name + (detail != null ? "  (" + detail + ")" : ""));
        }

        static void Near(double? actual, double expected, string name) =>
            Check(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-6, name, actual);

        static BatteryRecord Rec(uint state = 0, int rate = 0, uint cap = 0, uint full = 50000, uint caps = 0x80000000,
                                 uint design = 0, uint cycles = 0) =>
            new BatteryRecord { PowerState = state, Rate = rate, Capacity = cap, FullChargedCapacity = full, Capabilities = caps,
                                DesignedCapacity = design, CycleCount = cycles };

        static Reading Agg(bool? ac, params BatteryRecord[] r) => Battery.Aggregate(r, ac);

        static int Main()
        {
            Aggregation();
            Classification();
            StaleDisplay();
            Polling();
            Version();
            TrayIcons();
            EnergyAndHealth();
            TimeLeft();
            Sessions();
            AlertRules();
            HistoryAndCsv();
            SettingsFile();
            AutostartEntry();
            Themes();
            Console.WriteLine(failures == 0 ? "All " + passed + " checks passed." : failures + " FAILED, " + passed + " passed.");
            return failures == 0 ? 0 : 1;
        }

        static void Aggregation()
        {
            var r = Agg(false, Rec(Battery.Discharging, -30559, 26580, 51260));
            Near(r.Watts, -30.559, "single battery watts");
            Near(r.Percent, 26580.0 / 51260 * 100, "single battery percent");
            Check(r.Plugged == false && r.Flow == Flow.Discharging, "single battery state");

            r = Agg(true, Rec(Battery.Charging, 20000, 10000, 10000), Rec(Battery.Charging, 5000, 80000, 90000));
            Near(r.Watts, 25, "two batteries: watts summed");
            Near(r.Percent, 90, "two batteries: percent weighted by capacity");
            Check(r.Extra.Contains("2 batteries"), "two batteries noted", r.Extra);

            r = Agg(false, Rec(Battery.Discharging, Battery.UnknownRate, 1000));
            Check(r.Watts == null && r.Flow == Flow.Discharging, "unknown rate is unavailable, not 0");

            r = Agg(true, Rec(Battery.Charging, 0, 1000));
            Check(r.Watts == null, "charging flag with zero rate is unavailable");

            r = Agg(true, Rec(Battery.PowerOnLine, 0, 50000));
            Check(r.Watts == 0.0 && r.Flow == Flow.Idle, "idle with zero rate is 0 W");

            r = Agg(true, Rec(Battery.Charging, 12, 40, 80, 0x80000000 | Battery.CapacityRelative));
            Check(r.Watts == null, "relative units give no watts");
            Near(r.Percent, 50, "relative units still give a ratio");

            r = Agg(true, Rec(caps: 0x80000000 | Battery.CapacityRelative, cap: 40, full: 80), Rec(cap: 100, full: 1000));
            Check(r.Percent == null, "mixed units give no combined percent");

            Near(Agg(false, Rec(Battery.Discharging, -5000, 0, 50000)).Percent, 0, "0% is kept");
            Check(Agg(false, Rec(cap: Battery.UnknownCapacity)).Percent == null, "unknown capacity gives no percent");
            Check(Agg(false, Rec(full: 0)).Percent == null, "zero full capacity gives no percent");
            Near(Agg(false, Rec(Battery.Discharging, 1000000)).Watts ?? -1, -1, "implausible rate rejected");

            r = Agg(null, Rec(Battery.PowerOnLine | Battery.Charging, 9000));
            Check(r.Plugged == true, "plugged falls back to battery flag");

            Check(Throws(() => Agg(true, Rec(caps: 0x80000000 | Battery.ShortTerm))), "UPS ignored -> no battery");
            Check(Throws(() => Agg(true)), "no battery throws");
        }

        static Reading R(double? w, bool? plugged = true, Flow flow = Flow.Unknown) =>
            new Reading { Watts = w, Percent = 50, Plugged = plugged, Flow = flow };

        static string Label(Reading r) { Present.Classify(r, out _, out string l); return l; }

        static void Classification()
        {
            Present.Classify(R(-8), out Tone c, out string label);
            Check(label == "Plugged in · discharging" && c == Tone.Discharging, "draining while plugged in is shown", label);
            Check(Label(R(12)) == "Charging", "charging");
            Check(Label(R(0.1)) == "Plugged in · not charging", "plugged idle");
            Check(Label(R(-5, false)) == "On battery", "on battery");
            Check(Label(R(null)) == "Plugged in · rate unavailable", "unknown rate plugged");
            Check(Label(R(null, null, Flow.Discharging)) == "Discharging · rate unavailable", "unknown rate draining");
            Check(Label(R(null, true, Flow.Charging)) == "Charging · rate unavailable", "charging, rate unavailable");
            Check(Present.FormatWatts(null) == "-- W" && Present.FormatWatts(10) == "+10.0 W"
                  && Present.FormatWatts(-9.46) == "-9.5 W" && Present.FormatWatts(0.01) == "0.0 W", "watt formatting");
        }

        static void StaleDisplay()
        {
            var good = new Snapshot(1, 100, R(10), null);
            var v = Present.Describe(good, good, 101);
            Check(v.Big == "+10.0 W" && v.Status == "Charging" && v.TrayWatts == 10, "fresh reading", v.Status);

            v = Present.Describe(new Snapshot(2, 102, null, "Battery removed"), good, 102.5);
            Check(v.Status.StartsWith("Stale") && v.Detail.Contains("Battery removed") && v.BigTone == Tone.Stale
                  && v.TrayWatts == null, "failed read after success is stale", v.Status);

            v = Present.Describe(good, good, 100 + Present.StaleAfter + 1);
            Check(v.Status.StartsWith("Stale"), "hung reader is stale");

            v = Present.Describe(new Snapshot(1, 1, null, "No battery found"), null, 1);
            Check(v.Status == "No battery found" && v.Big == "--", "error before first success");
        }

        sealed class Flaky
        {
            readonly Queue<object> results;
            public Flaky(params object[] r) { results = new Queue<object>(r); }
            public Reading Read()
            {
                var r = results.Count > 0 ? results.Dequeue() : new Exception("gone");
                if (r is Exception e) throw e;
                return (Reading)r;
            }
        }

        static void Polling()
        {
            var ok = R(5);
            var p = new Poller(new Flaky(ok, new Exception("timeout")).Read, TimeSpan.FromSeconds(2));
            p.PollOnce();
            Check(p.State.Attempt.Reading == ok, "first read published");
            p.PollOnce();
            var st = p.State;
            Check(st.Attempt.Reading == null && st.Attempt.Error == "timeout" && st.Good.Reading == ok
                  && st.Attempt.Seq == 2, "failure published, last good kept");

            Check(new Poller(new Flaky(new TimeoutException("")).Read, TimeSpan.Zero).PollOnce().Error == "TimeoutException",
                  "empty exception message still reported");

            int reads = 0;
            var loop = new Poller(() => { Interlocked.Increment(ref reads); return ok; }, TimeSpan.FromMilliseconds(10));
            loop.Start();
            Thread.Sleep(100);
            loop.Stop();
            int after = reads;
            Thread.Sleep(50);
            Check(after > 1 && reads == after, "poller loops and stops", reads);
        }

        static void Version()
        {
            Check(Regex.IsMatch(AppInfo.Version, @"^1\.\d\.\d{3}$"), "version format 1.X.XXX", AppInfo.Version);
            var parts = AppInfo.Version.Split('.');
            Check(AppInfo.FileVersion == int.Parse(parts[0]) + "." + int.Parse(parts[1]) + "." + int.Parse(parts[2]) + ".0",
                  "FileVersion matches Version", AppInfo.FileVersion);
        }

        [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        static void TrayIcons()
        {
            Check(TrayIconRenderer.Text(null) == "--" && TrayIconRenderer.Text(-4.53) == "4.5"
                  && TrayIconRenderer.Text(23.4) == "23" && TrayIconRenderer.Text(1234) == "99+", "tray digits");
            foreach (int size in new[] { 16, 20, 24, 32 })
                using (var icon = TrayIconRenderer.Render(12.3, Theme.Dark.Charging, size))
                    Check(icon.Width == size, "tray icon size " + size, icon.Width);

            // a tray icon is redrawn whenever the value changes - it must not leak GDI/USER handles
            for (int i = 0; i < 50; i++) TrayIconRenderer.Render(i, Theme.Dark.Discharging, 16).Dispose();
            int gdi = GetGuiResources(GetCurrentProcess(), 0), user = GetGuiResources(GetCurrentProcess(), 1);
            for (int i = 0; i < 2000; i++) TrayIconRenderer.Render(i % 100 - 50, Theme.Dark.Discharging, 16).Dispose();
            int gdiAfter = GetGuiResources(GetCurrentProcess(), 0), userAfter = GetGuiResources(GetCurrentProcess(), 1);
            Check(gdiAfter - gdi < 10 && userAfter - user < 10, "no handle leak", gdi + "->" + gdiAfter + ", " + user + "->" + userAfter);
        }

        static void EnergyAndHealth()
        {
            var r = Agg(false, Rec(Battery.Discharging, -10000, 25000, 50000, design: 60000, cycles: 312));
            Near(r.RemainingWh, 25, "remaining energy");
            Near(r.FullWh, 50, "full energy");
            Near(r.DesignWh, 60, "design energy");
            Check(r.Cycles == 312, "cycle count");
            Check(Estimates.Health(r) == "Health 83%  (50.0 of 60.0 Wh)  ·  312 cycles", "health text", Estimates.Health(r));

            r = Agg(false, Rec(Battery.Discharging, -10000, 25000, 50000, design: 60000, cycles: 0));
            Check(r.Cycles == null && Estimates.Health(r) == "Health 83%  (50.0 of 60.0 Wh)", "no cycles when not reported");
            r = Agg(false, Rec(cap: 40, full: 80, caps: 0x80000000 | Battery.CapacityRelative, design: 100));
            Check(r.RemainingWh == null && r.DesignWh == null && Estimates.Health(r) == null, "relative units: no energy or health");
            r = Agg(false, Rec(cycles: 5), Rec(cycles: 9));
            Check(r.Cycles == null, "cycles only for a single battery");
        }

        static Reading E(double? watts, double remainingWh, double fullWh, double percent) =>
            new Reading { Watts = watts, RemainingWh = remainingWh, FullWh = fullWh, Percent = percent, Plugged = watts > 0 };

        static void TimeLeft()
        {
            Check(Estimates.TimeLeft(E(30, 20, 50, 40), 30) == "Full in 1 h 00 min", "full estimate", Estimates.TimeLeft(E(30, 20, 50, 40), 30));
            Check(Estimates.TimeLeft(E(-10, 25, 50, 50), -10) == "Empty in 2 h 30 min", "empty estimate");
            Check(Estimates.TimeLeft(E(-10, 1, 50, 2), -120) == "Empty in <1 min", "very short estimate", Estimates.TimeLeft(E(-10, 1, 50, 2), -120));
            Check(Estimates.TimeLeft(E(20, 49.9, 50, 99.8), 20) == null, "no estimate when full");
            Check(Estimates.TimeLeft(E(0.1, 20, 50, 40), 0.1) == null, "no estimate when idle");
            Check(Estimates.TimeLeft(E(10, 20, 50, 40), -5) == null, "no estimate right after direction change");
            Check(Estimates.TimeLeft(E(-0.5, 49, 50, 98), -0.5) == null, "no estimate beyond 48 h");
            Check(Estimates.Duration(30) == "<1 min" && Estimates.Duration(47 * 60) == "47 min" && Estimates.Duration(3 * 3600 + 12 * 60) == "3 h 12 min",
                  "duration formatting");
        }

        static void Sessions()
        {
            var s = new Session();
            s.Add(0, 30, true);
            s.Add(2, 30, true);
            s.Add(4, 60, true);
            Near(s.EnergyWh, (30 * 2 + 60 * 2) / 3600.0, "session energy integrated");
            Near(s.Peak, 60, "session peak");
            Near(s.Average, 40, "session average");
            s.Add(100, 30, true);  // after a long gap (sleep): not integrated
            Near(s.EnergyWh, (30 * 2 + 60 * 2) / 3600.0, "gaps are not integrated");
            s.Add(102, -10, false);  // unplugged: new session
            Check(s.Plugged == false && s.Samples == 1 && s.EnergyWh == 0 && s.Start == 102, "unplugging starts a new session");
            s.Add(104, -10, false);
            Check(s.Text(164).StartsWith("On battery for 1 min · ") && s.Text(164).Contains(" -0.01 Wh") && s.Text(164).Contains("peak -10.0 W"),
                  "session text", s.Text(164));
            s.Add(106, null, null);  // failed read: plug state unknown, keeps the session
            Check(s.Plugged == false && s.Samples == 2, "failed read keeps the session");
            Check(new Session().Text(0) == null, "no session text before data");
        }

        static void AlertRules()
        {
            var set = new Settings { AlertHighEnabled = true, AlertHigh = 80, AlertLowEnabled = true, AlertLow = 20, AlertDraining = true };
            var a = new Alerts();
            Reading P(double pct, bool plugged, double w) => new Reading { Percent = pct, Plugged = plugged, Watts = w };
            Check(a.Check(P(79, true, 20), set, 0).Count == 0, "below high limit: quiet");
            var fired = a.Check(P(80, true, 20), set, 2);
            Check(fired.Count == 1 && fired[0].Text.Contains("80%"), "high limit alert");
            Check(a.Check(P(81, true, 20), set, 4).Count == 0, "high alert fires once");
            Check(a.Check(P(78, true, 20), set, 6).Count == 0 && a.Check(P(80, true, 20), set, 8).Count == 0, "hysteresis: no re-alert at 78%");
            a.Check(P(76, true, 20), set, 10);
            Check(a.Check(P(80, true, 20), set, 12).Count == 1, "re-alerts after dropping 3% below");

            Check(a.Check(P(21, false, -10), set, 20).Count == 0, "above low limit: quiet");
            Check(a.Check(P(20, false, -10), set, 22).Count == 1, "low alert");
            Check(a.Check(P(19, false, -10), set, 24).Count == 0, "low alert fires once");
            Check(a.Check(P(19, true, 20), set, 26).Count == 0 && a.Check(P(19, false, -10), set, 28).Count == 1, "re-alerts after plugging in and out");

            var d = new Alerts();
            Check(d.Check(P(50, true, -5), set, 100).Count == 0, "drain: not before a minute");
            Check(d.Check(P(50, true, -5), set, 100 + Alerts.DrainSeconds - 1).Count == 0, "drain: still under a minute");
            var drain = d.Check(P(50, true, -5), set, 100 + Alerts.DrainSeconds);
            Check(drain.Count == 1 && drain[0].Title == "Plugged in but draining", "drain alert after a minute");
            Check(d.Check(P(50, true, -5), set, 300).Count == 0, "drain alert fires once");
            d.Check(P(50, true, 10), set, 302);
            d.Check(P(50, true, -5), set, 304);
            Check(d.Check(P(50, true, -5), set, 304 + Alerts.DrainSeconds).Count == 1, "drain re-arms after charging resumes");

            var off = new Settings { AlertHighEnabled = false, AlertLowEnabled = false, AlertDraining = false };
            var q = new Alerts();
            Check(q.Check(P(95, true, 1), off, 0).Count == 0 && q.Check(P(5, false, -1), off, 1).Count == 0
                  && q.Check(P(50, true, -9), off, 2).Count == 0 && q.Check(P(50, true, -9), off, 200).Count == 0, "disabled alerts stay quiet");
        }

        static void HistoryAndCsv()
        {
            var h = new History();
            var t0 = new DateTime(2026, 10, 8, 9, 30, 0);
            h.Add(new Sample { At = t0, Time = 0, Watts = -4.5, Percent = 79.25, Plugged = true, State = "Plugged in · discharging" });
            h.Add(new Sample { At = t0.AddSeconds(2), Time = 2, Watts = null, State = "read failed" });
            h.Add(new Sample { At = t0.AddSeconds(4), Time = 4, Watts = -5.5, Percent = 79.2, Plugged = true, State = "a, \"b\"" });
            Near(h.AverageWatts(4, 60), -5, "average ignores failed reads");
            Check(h.AverageWatts(1000, 60) == null, "no average without recent data");
            string csv = History.ToCsv(h.All);
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Check(lines[0] == "time,watts,battery_percent,plugged_in,state", "csv header");
            Check(lines[1] == "2026-10-08 09:30:00,-4.500,79.3,yes,Plugged in · discharging", "csv row", lines[1]);
            Check(lines[2] == "2026-10-08 09:30:02,,,,read failed", "csv failed row", lines[2]);
            Check(lines[3].EndsWith(",\"a, \"\"b\"\"\""), "csv quoting", lines[3]);

            var big = new History();
            for (int i = 0; i <= 50000; i++) big.Add(new Sample { Time = i * 2.0, Watts = 1 });
            Check(big.Count <= History.MaxAge / 2 + 1 && big.All[0].Time >= 50000 * 2.0 - History.MaxAge, "history keeps 24 h", big.Count);
        }

        static void SettingsFile()
        {
            var s = new Settings { WindowX = -1200, WindowY = 40, WindowWidth = 410, WindowHeight = 450, TopMost = true, AlertHighEnabled = true,
                                   AlertHigh = 85, AlertLowEnabled = false, AlertLow = 15, AlertDraining = false, GraphRange = 3600, Theme = ThemeMode.Light };
            var r = Settings.Parse(s.Serialize());
            Check(r.WindowX == -1200 && r.WindowY == 40 && r.WindowWidth == 410 && r.WindowHeight == 450 && r.TopMost && r.AlertHighEnabled
                  && r.AlertHigh == 85 && !r.AlertLowEnabled && r.AlertLow == 15 && !r.AlertDraining && r.GraphRange == 3600 && r.Theme == ThemeMode.Light,
                  "settings round trip");
            var bad = Settings.Parse("AlertHigh=500\nAlertLow=abc\nGraphRange=7\nTheme=Purple\nWindowWidth=10\njunk\n=1");
            Check(bad.AlertHigh == 100 && bad.AlertLow == 20 && bad.GraphRange == 240 && bad.Theme == ThemeMode.System && bad.WindowWidth == 300,
                  "bad settings values are clamped or ignored");
            Check(Settings.Parse("Theme=2").Theme == ThemeMode.System, "numeric theme ignored");
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OrclCM-tests-" + Guid.NewGuid().ToString("N"));
            string path = System.IO.Path.Combine(dir, "settings.ini");
            Check(Settings.Load(path).GraphRange == 240, "missing settings file gives defaults");
            s.Save(path);
            Check(Settings.Load(path).AlertHigh == 85, "settings saved and loaded");
            System.IO.Directory.Delete(dir, true);
        }

        static void AutostartEntry()
        {
            const string testKey = @"Software\OrclCM.Tests\Run";
            var a = new Autostart(testKey);
            try
            {
                a.Set(false, @"C:\x\OrclCM.exe");
                Check(!a.Enabled, "autostart off");
                a.Set(true, @"C:\Apps\OrclCM.exe");
                Check(a.Enabled && a.Current == "\"C:\\Apps\\OrclCM.exe\" /tray", "autostart on, starts in tray", a.Current);
                a.RefreshPath(@"D:\Moved\OrclCM.exe");
                Check(a.Current == "\"D:\\Moved\\OrclCM.exe\" /tray", "entry follows a moved exe", a.Current);
                a.Set(false, @"D:\Moved\OrclCM.exe");
                a.RefreshPath(@"E:\Other\OrclCM.exe");
                Check(!a.Enabled, "refresh never re-enables");
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\OrclCM.Tests", false);
            }
        }

        static void Themes()
        {
            Check(Theme.Resolve(ThemeMode.Dark) == Theme.Dark && Theme.Resolve(ThemeMode.Light) == Theme.Light, "explicit themes");
            var sys = Theme.Resolve(ThemeMode.System);
            Check(sys == (Theme.SystemUsesLight() ? Theme.Light : Theme.Dark), "system theme follows Windows");
            Check(Theme.Light.Of(Tone.Charging) != Theme.Dark.Of(Tone.Charging) && Theme.Light.Of(Tone.Fg) == Theme.Light.Fg, "tones map per theme");
        }

        static bool Throws(Action a)
        {
            try { a(); return false; } catch (BatteryException) { return true; }
        }
    }
}

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

        static BatteryRecord Rec(uint state = 0, int rate = 0, uint cap = 0, uint full = 50000, uint caps = 0x80000000) =>
            new BatteryRecord { PowerState = state, Rate = rate, Capacity = cap, FullChargedCapacity = full, Capabilities = caps };

        static Reading Agg(bool? ac, params BatteryRecord[] r) => Battery.Aggregate(r, ac);

        static int Main()
        {
            Aggregation();
            Classification();
            StaleDisplay();
            Polling();
            Version();
            TrayIcons();
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
            Present.Classify(R(-8), out Color c, out string label);
            Check(label == "Plugged in · discharging" && c == Present.Orange, "draining while plugged in is shown", label);
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
            Check(v.Status.StartsWith("Stale") && v.Detail.Contains("Battery removed") && v.BigColor == Present.Stale
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
                using (var icon = TrayIconRenderer.Render(12.3, Present.Green, size))
                    Check(icon.Width == size, "tray icon size " + size, icon.Width);

            // a tray icon is redrawn whenever the value changes - it must not leak GDI/USER handles
            for (int i = 0; i < 50; i++) TrayIconRenderer.Render(i, Present.Orange, 16).Dispose();
            int gdi = GetGuiResources(GetCurrentProcess(), 0), user = GetGuiResources(GetCurrentProcess(), 1);
            for (int i = 0; i < 2000; i++) TrayIconRenderer.Render(i % 100 - 50, Present.Orange, 16).Dispose();
            int gdiAfter = GetGuiResources(GetCurrentProcess(), 0), userAfter = GetGuiResources(GetCurrentProcess(), 1);
            Check(gdiAfter - gdi < 10 && userAfter - user < 10, "no handle leak", gdi + "->" + gdiAfter + ", " + user + "->" + userAfter);
        }

        static bool Throws(Action a)
        {
            try { a(); return false; } catch (BatteryException) { return true; }
        }
    }
}

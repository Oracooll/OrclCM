// History, session statistics, time estimates, health, alerts, settings and autostart.
// No UI code - covered by tests\CoreTests.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace OrclCM
{
    struct Sample
    {
        public DateTime At;     // local wall-clock time, for export
        public double Time;     // Clock.Now(), for maths
        public double? Watts, Percent;
        public bool? Plugged;
        public string State;    // label shown at the time, e.g. "Charging"
    }

    /// <summary>Up to 24 hours of samples (one per read attempt; failed reads have no values).</summary>
    sealed class History
    {
        public const double MaxAge = 24 * 3600;
        readonly List<Sample> samples = new List<Sample>();

        public int Count => samples.Count;
        public IReadOnlyList<Sample> All => samples;

        public void Add(Sample s)
        {
            samples.Add(s);
            int old = 0;
            while (old < samples.Count && s.Time - samples[old].Time > MaxAge) old++;
            if (old > 0) samples.RemoveRange(0, old);
        }

        public IEnumerable<Sample> Since(double time) => samples.Where(s => s.Time >= time);

        /// <summary>Mean of the known wattages over the last <paramref name="seconds"/>, for steadier estimates.</summary>
        public double? AverageWatts(double now, double seconds)
        {
            var w = Since(now - seconds).Where(s => s.Watts.HasValue).Select(s => s.Watts.Value).ToList();
            return w.Count > 0 ? w.Average() : (double?)null;
        }

        public static string ToCsv(IEnumerable<Sample> samples)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("time,watts,battery_percent,plugged_in,state\r\n");
            foreach (var s in samples)
            {
                sb.Append(s.At.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',')
                  .Append(s.Watts.HasValue ? s.Watts.Value.ToString("0.000", inv) : "").Append(',')
                  .Append(s.Percent.HasValue ? s.Percent.Value.ToString("0.0", inv) : "").Append(',')
                  .Append(s.Plugged.HasValue ? (s.Plugged.Value ? "yes" : "no") : "").Append(',')
                  .Append(Quote(s.State)).Append("\r\n");
            }
            return sb.ToString();
        }

        static string Quote(string v) =>
            string.IsNullOrEmpty(v) ? "" : v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Statistics since the charger was last plugged in or unplugged.</summary>
    sealed class Session
    {
        const double MaxGap = 10;  // longer gaps (sleep, failed reads) are not integrated
        public bool? Plugged { get; private set; }
        public double Start { get; private set; }
        public double EnergyWh { get; private set; }  // signed: + into the battery, - out of it
        public double Peak { get; private set; }      // largest |W| seen, with its sign
        double sum, last = -1;
        int count;

        public int Samples => count;
        public double? Average => count > 0 ? sum / count : (double?)null;

        public void Add(double time, double? watts, bool? plugged)
        {
            if (plugged.HasValue && plugged != Plugged)
            {
                Plugged = plugged; Start = time; EnergyWh = 0; Peak = 0; sum = 0; count = 0; last = -1;
            }
            if (!watts.HasValue) { last = -1; return; }
            double w = watts.Value;
            if (last >= 0 && time - last <= MaxGap) EnergyWh += w * (time - last) / 3600;
            last = time;
            sum += w;
            count++;
            if (Math.Abs(w) > Math.Abs(Peak)) Peak = w;
        }

        public string Text(double now)
        {
            if (!Plugged.HasValue || count == 0) return null;
            var inv = CultureInfo.InvariantCulture;
            return (Plugged.Value ? "Plugged in for " : "On battery for ") + Estimates.Duration(now - Start) + " · "
                 + EnergyWh.ToString(Math.Abs(EnergyWh) < 1 ? "+0.00;-0.00;0.00" : "+0.0;-0.0;0.0", inv) + " Wh · avg " + Average.Value.ToString("+0.0;-0.0;0.0", inv)
                 + " W · peak " + Peak.ToString("+0.0;-0.0;0.0", inv) + " W";
        }
    }

    static class Estimates
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Duration(double seconds)
        {
            if (seconds < 60) return "<1 min";
            int minutes = (int)Math.Round(seconds / 60);
            return minutes < 60 ? minutes + " min" : minutes / 60 + " h " + (minutes % 60).ToString("00") + " min";
        }

        /// <summary>"Full in 47 min" / "Empty in 3 h 12 min", from the recent average wattage.</summary>
        public static string TimeLeft(Reading r, double? averageWatts)
        {
            if (!r.RemainingWh.HasValue || !r.FullWh.HasValue || !averageWatts.HasValue || !r.Watts.HasValue) return null;
            double w = averageWatts.Value;
            if (Math.Sign(w) != Math.Sign(r.Watts.Value)) return null;  // direction just changed: no stable estimate yet
            double hours;
            string prefix;
            if (w > Battery.IdleWatts)
            {
                if (r.Percent >= 99.5) return null;
                hours = (r.FullWh.Value - r.RemainingWh.Value) / w;
                prefix = "Full in ";
            }
            else if (w < -Battery.IdleWatts)
            {
                hours = r.RemainingWh.Value / -w;
                prefix = "Empty in ";
            }
            else return null;
            if (hours <= 0 || hours > 48) return null;
            return prefix + Duration(hours * 3600);
        }

        public static string Health(Reading r)
        {
            var parts = new List<string>();
            if (r.FullWh.HasValue && r.DesignWh.HasValue && r.DesignWh > 0)
                parts.Add("Health " + (100 * r.FullWh.Value / r.DesignWh.Value).ToString("0", Inv) + "%  ("
                          + r.FullWh.Value.ToString("0.0", Inv) + " of " + r.DesignWh.Value.ToString("0.0", Inv) + " Wh)");
            if (r.Cycles.HasValue) parts.Add(r.Cycles.Value + " cycles");
            return parts.Count > 0 ? string.Join("  ·  ", parts) : null;
        }
    }

    sealed class Settings
    {
        public int? WindowX, WindowY;
        public int WindowWidth = 430, WindowHeight = 440;
        public bool TopMost;
        public bool AlertHighEnabled = false;
        public int AlertHigh = 80;
        public bool AlertLowEnabled = true;
        public int AlertLow = 20;
        public bool AlertDraining = true;
        public int GraphRange = 240;  // seconds
        public ThemeMode Theme = ThemeMode.System;

        public static readonly int[] GraphRanges = { 240, 3600, 86400 };

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "settings.ini");

        public static Settings Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Settings(); }
            catch (Exception) { return new Settings(); }  // unreadable settings never stop the app
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Serialize());
            }
            catch (Exception) { }
        }

        public static Settings Parse(string text)
        {
            var s = new Settings();
            foreach (var raw in text.Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
                bool isInt = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                bool flag = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                switch (key)
                {
                    case "WindowX": if (isInt) s.WindowX = n; break;
                    case "WindowY": if (isInt) s.WindowY = n; break;
                    case "WindowWidth": if (isInt) s.WindowWidth = Clamp(n, 300, 4000); break;
                    case "WindowHeight": if (isInt) s.WindowHeight = Clamp(n, 300, 4000); break;
                    case "TopMost": s.TopMost = flag; break;
                    case "AlertHighEnabled": s.AlertHighEnabled = flag; break;
                    case "AlertHigh": if (isInt) s.AlertHigh = Clamp(n, 50, 100); break;
                    case "AlertLowEnabled": s.AlertLowEnabled = flag; break;
                    case "AlertLow": if (isInt) s.AlertLow = Clamp(n, 5, 50); break;
                    case "AlertDraining": s.AlertDraining = flag; break;
                    case "GraphRange": if (isInt && Array.IndexOf(GraphRanges, n) >= 0) s.GraphRange = n; break;
                    case "Theme": if (!isInt && Enum.TryParse(value, true, out ThemeMode t)) s.Theme = t; break;
                }
            }
            return s;
        }

        public string Serialize()
        {
            var sb = new StringBuilder("; OrclCM settings\r\n");
            void Put(string k, object v) => sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\r\n");
            if (WindowX.HasValue) Put("WindowX", WindowX.Value);
            if (WindowY.HasValue) Put("WindowY", WindowY.Value);
            Put("WindowWidth", WindowWidth);
            Put("WindowHeight", WindowHeight);
            Put("TopMost", TopMost ? 1 : 0);
            Put("AlertHighEnabled", AlertHighEnabled ? 1 : 0);
            Put("AlertHigh", AlertHigh);
            Put("AlertLowEnabled", AlertLowEnabled ? 1 : 0);
            Put("AlertLow", AlertLow);
            Put("AlertDraining", AlertDraining ? 1 : 0);
            Put("GraphRange", GraphRange);
            Put("Theme", Theme);
            return sb.ToString();
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    }

    sealed class Alert
    {
        public readonly string Title, Text;
        public Alert(string title, string text) { Title = title; Text = text; }
    }

    /// <summary>Charge-level and charger alerts. Each fires once per crossing (with a small
    /// hysteresis), so a battery hovering around the threshold doesn't nag.</summary>
    sealed class Alerts
    {
        public const double DrainSeconds = 60, DrainWatts = 0.5;
        const double Hysteresis = 3;
        bool highFired, lowFired, drainFired;
        double drainSince = -1;

        public List<Alert> Check(Reading r, Settings s, double now)
        {
            var result = new List<Alert>();
            var inv = CultureInfo.InvariantCulture;
            if (r.Percent.HasValue)
            {
                double p = r.Percent.Value;
                if (highFired && (p < s.AlertHigh - Hysteresis || r.Plugged == false)) highFired = false;
                if (s.AlertHighEnabled && !highFired && r.Plugged == true && p >= s.AlertHigh)
                {
                    highFired = true;
                    result.Add(new Alert("Battery at " + p.ToString("0", inv) + "%", "Charged to your " + s.AlertHigh + "% limit - you can unplug the charger."));
                }
                if (lowFired && (p > s.AlertLow + Hysteresis || r.Plugged == true)) lowFired = false;
                if (s.AlertLowEnabled && !lowFired && r.Plugged == false && p <= s.AlertLow)
                {
                    lowFired = true;
                    result.Add(new Alert("Battery at " + p.ToString("0", inv) + "%", "Battery is low - plug in the charger."));
                }
            }

            bool draining = r.Plugged == true && r.Watts.HasValue && r.Watts.Value < -DrainWatts;
            if (!draining) { drainSince = -1; drainFired = false; }
            else
            {
                if (drainSince < 0) drainSince = now;
                if (s.AlertDraining && !drainFired && now - drainSince >= DrainSeconds)
                {
                    drainFired = true;
                    result.Add(new Alert("Plugged in but draining",
                        "The battery is losing " + (-r.Watts.Value).ToString("0.0", inv) + " W although the charger is connected - it may be too weak, or charging may be paused."));
                }
            }
            return result;
        }
    }

    /// <summary>"Start with Windows" through the per-user Run key.</summary>
    sealed class Autostart
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string TrayArgument = "/tray";
        const string ValueName = AppInfo.Name;
        readonly string keyPath;

        public Autostart(string keyPath = RunKey) { this.keyPath = keyPath; }

        public static string Command(string exePath) => "\"" + exePath + "\" " + TrayArgument;

        public string Current
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(keyPath)) return k?.GetValue(ValueName) as string; }
        }

        public bool Enabled => Current != null;

        public void Set(bool enabled, string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (enabled) k.SetValue(ValueName, Command(exePath));
                else k.DeleteValue(ValueName, false);
            }
        }

        /// <summary>If the exe was moved, point the existing entry at the new location.</summary>
        public void RefreshPath(string exePath)
        {
            string cur = Current;
            if (cur != null && cur != Command(exePath)) Set(true, exePath);
        }
    }
}

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

    /// <summary>One finished period for the battery log: plugged in, on battery, or asleep.</summary>
    sealed class SessionRecord
    {
        public const string PluggedKind = "plugged", BatteryKind = "battery", SleepKind = "sleep";
        public DateTime Start, End;  // local time, as shown and logged
        public string Kind;
        public double? StartPercent, EndPercent, EnergyWh, AverageW, PeakW;
        public double? DurationSeconds;  // exact length; Start/End can jump with DST or time-zone changes

        public double Seconds => DurationSeconds ?? (End - Start).TotalSeconds;
    }

    /// <summary>Statistics since the charger was last plugged in or unplugged.</summary>
    sealed class Session
    {
        const double MaxGap = 10;         // longer gaps (sleep, failed reads) are not integrated
        public const double MinLogged = 60;  // shorter sessions are not written to the log
        public bool? Plugged { get; private set; }
        public double Start { get; private set; }
        public double EnergyWh { get; private set; }  // signed: + into the battery, - out of it
        public double Peak { get; private set; }      // largest |W| seen, with its sign
        double sum, last = -1, lastTime;
        DateTime startAt, lastAt;
        double? startPercent, lastPercent;
        int count;
        bool firstSinceStart = true, breakRequested;

        /// <summary>True while the current session began when OrclCM started, not at a plug change.</summary>
        public bool SinceAppStart { get; private set; }

        public int Samples => count;
        public double? Average => count > 0 ? sum / count : (double?)null;

        /// <summary>Adds a successful reading; returns the session that just ended (plug state
        /// changed) if it is long enough to log, otherwise null.</summary>
        public SessionRecord Add(double time, DateTime at, double? watts, double? percent, bool? plugged)
        {
            SessionRecord finished = null;
            if (plugged.HasValue && (plugged != Plugged || breakRequested))
            {
                finished = breakRequested ? null : Finish();
                SinceAppStart = firstSinceStart && !breakRequested;
                firstSinceStart = false;
                breakRequested = false;
                Plugged = plugged; Start = time; startAt = at; startPercent = percent; lastPercent = percent;
                EnergyWh = 0; Peak = 0; sum = 0; count = 0; last = -1;
            }
            lastTime = time; lastAt = at;
            if (percent.HasValue) lastPercent = percent;
            if (!startPercent.HasValue) startPercent = percent;
            if (!watts.HasValue) { last = -1; return finished; }
            double w = watts.Value;
            if (last >= 0 && time - last <= MaxGap) EnergyWh += w * (time - last) / 3600;
            last = time;
            sum += w;
            count++;
            if (Math.Abs(w) > Math.Abs(Peak)) Peak = w;
            return finished;
        }

        /// <summary>The current session as a log record, or null if too short (or already ended by Break).</summary>
        public SessionRecord Finish()
        {
            if (breakRequested || !Plugged.HasValue || count == 0 || lastTime - Start < MinLogged) return null;
            return new SessionRecord
            {
                Kind = Plugged.Value ? SessionRecord.PluggedKind : SessionRecord.BatteryKind,
                Start = startAt, End = lastAt, StartPercent = startPercent, EndPercent = lastPercent,
                EnergyWh = EnergyWh, AverageW = Average, PeakW = Peak, DurationSeconds = lastTime - Start,
            };
        }

        /// <summary>Ends the current session (before sleep, at shutdown); the next reading starts a
        /// new one even if the plug state is unchanged. Returns the ended session if long enough.</summary>
        public SessionRecord Break()
        {
            var ended = Finish();
            if (Plugged.HasValue) breakRequested = true;
            return ended;
        }

        public string Text(double now)
        {
            if (!Plugged.HasValue || count == 0 || breakRequested) return null;
            var inv = CultureInfo.InvariantCulture;
            return L.F(SinceAppStart ? "Since OrclCM started {0} · {1} Wh · avg {2} W · peak {3} W"
                       : Plugged.Value ? "Plugged in for {0} · {1} Wh · avg {2} W · peak {3} W" : "On battery for {0} · {1} Wh · avg {2} W · peak {3} W",
                       Estimates.Duration(now - Start), EnergyWh.ToString(Math.Abs(EnergyWh) < 1 ? "+0.00;-0.00;0.00" : "+0.0;-0.0;0.0", inv),
                       Average.Value.ToString("+0.0;-0.0;0.0", inv), Peak.ToString("+0.0;-0.0;0.0", inv));
        }
    }

    /// <summary>Spots sleep (and hibernation): Windows freezes desktop apps while asleep, so a
    /// long gap between read attempts means the PC was asleep. Failed reads don't count as gaps.</summary>
    sealed class SleepTracker
    {
        public const double MinGapSeconds = 120;
        double? lastAttempt, gapStart;  // system uptime in seconds (Uptime.Seconds)
        DateTime gapStartUtc;
        Reading lastGood;

        /// <summary>Call once per read attempt. <paramref name="uptime"/> is the system uptime,
        /// which keeps counting during sleep and is not affected by clock, DST or time-zone changes;
        /// <paramref name="utcNow"/> is only used for the times shown in the log.
        /// Returns a sleep record at the first good reading after a gap.</summary>
        public SessionRecord Observe(double uptime, DateTime utcNow, Reading reading)
        {
            if (lastAttempt.HasValue && lastGood != null && !gapStart.HasValue && uptime - lastAttempt.Value >= MinGapSeconds)
            {
                gapStart = lastAttempt;  // kept until a good reading arrives, even if the first reads after waking fail
                gapStartUtc = utcNow.AddSeconds(lastAttempt.Value - uptime);
            }
            lastAttempt = uptime;
            if (reading == null) return null;

            SessionRecord sleep = null;
            if (gapStart.HasValue)
            {
                double? wh = reading.RemainingWh.HasValue && lastGood.RemainingWh.HasValue ? reading.RemainingWh - lastGood.RemainingWh : null;
                double seconds = uptime - gapStart.Value;
                sleep = new SessionRecord
                {
                    Kind = SessionRecord.SleepKind, Start = gapStartUtc.ToLocalTime(), End = utcNow.ToLocalTime(), DurationSeconds = seconds,
                    StartPercent = lastGood.Percent, EndPercent = reading.Percent,
                    EnergyWh = wh, AverageW = wh.HasValue && seconds > 0 ? wh / (seconds / 3600) : null,
                };
                gapStart = null;
            }
            lastGood = reading;
            return sleep;
        }

        public static string Text(SessionRecord s)
        {
            var inv = CultureInfo.InvariantCulture;
            string pct = s.StartPercent.HasValue && s.EndPercent.HasValue
                ? (s.EndPercent.Value - s.StartPercent.Value).ToString("+0;-0;0", inv) : "?";
            if (!s.EnergyWh.HasValue)
                return L.F("{0} asleep: {1}%", Estimates.Duration(s.Seconds), pct);
            return L.F("{0} asleep: {1}% ({2} Wh, avg {3} W)", Estimates.Duration(s.Seconds), pct,
                       s.EnergyWh.Value.ToString("+0.0;-0.0;0.0", inv), (s.AverageW ?? 0).ToString("+0.00;-0.00;0.00", inv));
        }
    }

    /// <summary>System uptime in seconds: monotonic, includes time asleep, unaffected by clock changes.</summary>
    static class Uptime
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        public static double Seconds => GetTickCount64() / 1000.0;
    }

    /// <summary>Battery log files in %APPDATA%\OrclCM: sessions.csv and health.csv.</summary>
    static class BatteryLog
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        public const string SessionsHeader = "start,end,kind,start_percent,end_percent,energy_wh,avg_w,peak_w";
        public const string HealthHeader = "date,full_wh,design_wh,cycles";

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        public static string SessionsPath => Path.Combine(Folder, "sessions.csv");
        public static string CheckpointPath => Path.Combine(Folder, "current-session.csv");
        public static string HealthPath => Path.Combine(Folder, "health.csv");

        static string N(double? v, string format) => v.HasValue ? v.Value.ToString(format, Inv) : "";
        static double? P(string s) => double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : (double?)null;

        public static string Line(SessionRecord r) =>
            string.Join(",", r.Start.ToString(TimeFormat, Inv), r.End.ToString(TimeFormat, Inv), r.Kind,
                        N(r.StartPercent, "0.0"), N(r.EndPercent, "0.0"), N(r.EnergyWh, "0.000"), N(r.AverageW, "0.00"), N(r.PeakW, "0.00"));

        public static void AppendSession(string path, SessionRecord r) => Append(path, SessionsHeader, Line(r));

        // Crash safety: the checkpoint file always holds every session line not yet in sessions.csv
        // (queued because the file was locked) plus the session still in progress. It is rewritten
        // whenever either changes and deleted when both are empty.
        static string checkpointFile, checkpointSessions;
        static SessionRecord openSession;

        /// <summary>At startup: turn on the checkpoint and recover what an unclean exit left behind.</summary>
        public static void UseCheckpoint(string checkpointPath, string sessionsPath)
        {
            checkpointFile = checkpointPath;
            checkpointSessions = sessionsPath;
            openSession = null;
            if (checkpointPath == null) return;
            var left = new List<string>();
            foreach (var cols in Rows(checkpointPath)) left.Add(string.Join(",", cols));
            foreach (var line in left) pending.Add(new PendingLine { Path = sessionsPath, Header = SessionsHeader, Line = line });
            FlushPending();  // also rewrites or removes the checkpoint
        }

        /// <summary>The session in progress (null when none), saved in the checkpoint.</summary>
        public static void SetOpenSession(SessionRecord open)
        {
            openSession = open;
            WriteCheckpoint();
        }

        static void WriteCheckpoint()
        {
            if (checkpointFile == null) return;
            try
            {
                var lines = pending.Where(p => p.Path == checkpointSessions).Select(p => p.Line).ToList();
                if (openSession != null) lines.Add(Line(openSession));
                if (lines.Count == 0) { if (File.Exists(checkpointFile)) File.Delete(checkpointFile); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(checkpointFile));
                string tmp = checkpointFile + ".tmp";
                File.WriteAllText(tmp, SessionsHeader + "\r\n" + string.Join("\r\n", lines) + "\r\n");
                if (File.Exists(checkpointFile)) File.Replace(tmp, checkpointFile, null);
                else File.Move(tmp, checkpointFile);
            }
            catch (Exception) { }
        }

        public static List<SessionRecord> LoadSessions(string path)
        {
            var list = new List<SessionRecord>();
            foreach (var cols in Rows(path).Concat(pending.Where(p => p.Path == path).Select(p => p.Line.Split(','))))
            {
                if (cols.Length < 8 || !DateTime.TryParseExact(cols[0], TimeFormat, Inv, DateTimeStyles.None, out DateTime start)
                    || !DateTime.TryParseExact(cols[1], TimeFormat, Inv, DateTimeStyles.None, out DateTime end)) continue;
                list.Add(new SessionRecord { Start = start, End = end, Kind = cols[2], StartPercent = P(cols[3]), EndPercent = P(cols[4]),
                                             EnergyWh = P(cols[5]), AverageW = P(cols[6]), PeakW = P(cols[7]) });
            }
            return list;
        }

        public sealed class HealthEntry
        {
            public DateTime Date;
            public double FullWh, DesignWh;
            public int? Cycles;
            public double Percent => DesignWh > 0 ? 100 * FullWh / DesignWh : 0;
        }

        /// <summary>Writes one health row per day; returns true if a row was added.</summary>
        public static bool RecordHealth(string path, DateTime today, Reading r)
        {
            if (!r.FullWh.HasValue || !r.DesignWh.HasValue || r.DesignWh <= 0) return false;
            var existing = LoadHealth(path);
            if (existing.Count > 0 && existing[existing.Count - 1].Date >= today.Date) return false;
            Append(path, HealthHeader, string.Join(",", today.ToString("yyyy-MM-dd", Inv), N(r.FullWh, "0.000"), N(r.DesignWh, "0.000"),
                                                   r.Cycles.HasValue ? r.Cycles.Value.ToString(Inv) : ""));
            return true;
        }

        public static List<HealthEntry> LoadHealth(string path)
        {
            var list = new List<HealthEntry>();
            foreach (var cols in Rows(path))
            {
                if (cols.Length < 4 || !DateTime.TryParseExact(cols[0], "yyyy-MM-dd", Inv, DateTimeStyles.None, out DateTime d)) continue;
                double? full = P(cols[1]), design = P(cols[2]);
                if (!full.HasValue || !design.HasValue) continue;
                list.Add(new HealthEntry { Date = d, FullWh = full.Value, DesignWh = design.Value,
                                           Cycles = int.TryParse(cols[3], NumberStyles.Integer, Inv, out int c) ? c : (int?)null });
            }
            return list;
        }

        static IEnumerable<string[]> Rows(string path)
        {
            var lines = new List<string>();
            try
            {
                if (File.Exists(path))  // shared read: works while the file is open in Excel
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                        for (string l; (l = reader.ReadLine()) != null;) lines.Add(l);
            }
            catch (Exception) { }
            for (int i = 1; i < lines.Count; i++)  // skip the header
                if (lines[i].Length > 0) yield return lines[i].Split(',');
        }

        sealed class PendingLine { public string Path, Header, Line; }
        static readonly List<PendingLine> pending = new List<PendingLine>();

        /// <summary>Lines that couldn't be written yet (e.g. the file is open in Excel).</summary>
        public static int PendingCount => pending.Count;

        static void Append(string path, string header, string line)
        {
            pending.Add(new PendingLine { Path = path, Header = header, Line = line });
            FlushPending();
        }

        /// <summary>Writes queued lines in order; stops at the first failure and retries later.</summary>
        public static void FlushPending()
        {
            try { Flush(); }
            finally { WriteCheckpoint(); }
        }

        static void Flush()
        {
            while (pending.Count > 0)
            {
                var p = pending[0];
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p.Path));
                    using (var fs = new FileStream(p.Path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var w = new StreamWriter(fs))
                    {
                        if (fs.Length == 0) w.Write(p.Header + "\r\n");
                        w.Write(p.Line + "\r\n");
                    }
                }
                catch (Exception) { return; }  // logging must never break the meter; try again later
                pending.RemoveAt(0);
            }
        }
    }

    static class Estimates
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Duration(double seconds)
        {
            if (seconds < 60) return L.T("<1 min");
            int minutes = (int)Math.Round(seconds / 60);
            return minutes < 60 ? L.F("{0} min", minutes) : L.F("{0} h {1} min", minutes / 60, (minutes % 60).ToString("00"));
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
                prefix = "Full in {0}";
            }
            else if (w < -Battery.IdleWatts)
            {
                hours = r.RemainingWh.Value / -w;
                prefix = "Empty in {0}";
            }
            else return null;
            if (hours <= 0 || hours > 48) return null;
            return L.F(prefix, Duration(hours * 3600));
        }

        /// <summary>On battery the drain is the whole laptop's power use. Flags it when the recent
        /// average is well above this session's average.</summary>
        public static string SystemDraw(Reading r, double? recentAverage, Session session, double now, out bool high)
        {
            high = false;
            if (r.Plugged != false || !r.Watts.HasValue || r.Watts.Value >= -Battery.IdleWatts) return null;
            double draw = -(recentAverage ?? r.Watts.Value);
            if (draw <= 0) draw = -r.Watts.Value;
            if (session.Plugged == false && session.Average.HasValue && now - session.Start >= 300)
            {
                double usual = -session.Average.Value;
                high = usual > 0 && draw >= Math.Max(usual * 1.5, usual + 3);
            }
            return L.F(high ? "Laptop using {0} W — higher than usual" : "Laptop using {0} W", draw.ToString("0.0", Inv));
        }

        public static string Health(Reading r)
        {
            var parts = new List<string>();
            if (r.FullWh.HasValue && r.DesignWh.HasValue && r.DesignWh > 0)
                parts.Add(L.F("Health {0}%  ({1} of {2} Wh)", (100 * r.FullWh.Value / r.DesignWh.Value).ToString("0", Inv),
                              r.FullWh.Value.ToString("0.0", Inv), r.DesignWh.Value.ToString("0.0", Inv)));
            if (r.Cycles.HasValue) parts.Add(L.F("{0} cycles", r.Cycles.Value));
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
        public LanguageMode Language = LanguageMode.System;
        public bool AlertSleep = true;
        public bool MiniMode;
        public int? MiniX, MiniY;
        public bool AutoUpdateCheck = true;
        public DateTime? LastUpdateCheckUtc;

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
                    case "Language": if (!isInt && Enum.TryParse(value, true, out LanguageMode lang)) s.Language = lang; break;
                    case "AlertSleep": s.AlertSleep = flag; break;
                    case "MiniMode": s.MiniMode = flag; break;
                    case "MiniX": if (isInt) s.MiniX = n; break;
                    case "MiniY": if (isInt) s.MiniY = n; break;
                    case "AutoUpdateCheck": s.AutoUpdateCheck = flag; break;
                    case "LastUpdateCheckUtc":
                        if (DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d))
                            s.LastUpdateCheckUtc = d;
                        break;
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
            Put("Language", Language);
            Put("AlertSleep", AlertSleep ? 1 : 0);
            Put("MiniMode", MiniMode ? 1 : 0);
            if (MiniX.HasValue) Put("MiniX", MiniX.Value);
            if (MiniY.HasValue) Put("MiniY", MiniY.Value);
            Put("AutoUpdateCheck", AutoUpdateCheck ? 1 : 0);
            if (LastUpdateCheckUtc.HasValue) Put("LastUpdateCheckUtc", LastUpdateCheckUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
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
        double drainSince = -1, notDrainingSince = -1;

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
                    result.Add(new Alert(L.F("Battery at {0}%", p.ToString("0", inv)), L.F("Charged to your {0}% limit - you can unplug the charger.", s.AlertHigh)));
                }
                if (lowFired && (p > s.AlertLow + Hysteresis || r.Plugged == true)) lowFired = false;
                if (s.AlertLowEnabled && !lowFired && r.Plugged == false && p <= s.AlertLow)
                {
                    lowFired = true;
                    result.Add(new Alert(L.F("Battery at {0}%", p.ToString("0", inv)), L.T("Battery is low - plug in the charger.")));
                }
            }

            if (r.Plugged == false) { drainSince = -1; notDrainingSince = -1; drainFired = false; return result; }
            if (r.Plugged == null || !r.Watts.HasValue) return result;  // unknown: keep the current state
            bool draining = r.Watts.Value < -DrainWatts;
            if (!draining)
            {
                drainSince = -1;
                if (notDrainingSince < 0) notDrainingSince = now;
                if (now - notDrainingSince >= DrainSeconds) drainFired = false;  // re-arm after a minute of not draining
            }
            else
            {
                notDrainingSince = -1;
                if (drainSince < 0) drainSince = now;
                if (s.AlertDraining && !drainFired && now - drainSince >= DrainSeconds)
                {
                    drainFired = true;
                    result.Add(new Alert(L.T("Plugged in but draining"),
                        L.F("The battery is losing {0} W although the charger is connected - it may be too weak, or charging may be paused.", (-r.Watts.Value).ToString("0.0", inv))));
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

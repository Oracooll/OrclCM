// Measurement model, aggregation, presentation and polling - no UI or native code,
// so all of it is covered by tests\CoreTests.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace OrclCM
{
    enum Flow { Unknown, Charging, Discharging, Idle }

    /// <summary>Raw values for one battery, as reported by the Windows battery driver.</summary>
    sealed class BatteryRecord
    {
        public uint Capabilities;         // BATTERY_INFORMATION.Capabilities
        public uint PowerState;           // BATTERY_STATUS.PowerState
        public uint Capacity;             // remaining, mWh (or relative units)
        public uint FullChargedCapacity;  // mWh (or relative units)
        public int Rate;                  // mW, negative = discharging
        public uint DesignedCapacity;     // mWh when new (or relative units)
        public uint CycleCount;           // 0 = not reported
    }

    /// <summary>One measurement. Null means the value is unavailable - never a fake zero.</summary>
    sealed class Reading
    {
        public double? Watts;
        public double? Percent;
        public bool? Plugged;
        public Flow Flow;  // the OS's own direction hint, used when Watts is unavailable
        public string Extra = "";
        public double? RemainingWh, FullWh, DesignWh;  // only when every battery reports real units
        public int? Cycles;                            // single battery only
    }

    sealed class BatteryException : Exception
    {
        public BatteryException(string message) : base(message) { }
    }

    static class Battery
    {
        public const uint CapacityRelative = 0x40000000;  // BATTERY_CAPACITY_RELATIVE
        public const uint ShortTerm = 0x20000000;         // BATTERY_IS_SHORT_TERM (UPS)
        public const uint PowerOnLine = 0x1, Discharging = 0x2, Charging = 0x4;
        public const uint UnknownCapacity = 0xFFFFFFFF;   // BATTERY_UNKNOWN_CAPACITY
        public const int UnknownRate = int.MinValue;      // BATTERY_UNKNOWN_RATE (0x80000000)
        public const double MaxPlausibleWatts = 500, IdleWatts = 0.2;

        public static Reading Aggregate(IList<BatteryRecord> records, bool? acOnline)
        {
            var batteries = records.Where(r => (r.Capabilities & ShortTerm) == 0).ToList();
            if (batteries.Count == 0)
                throw new BatteryException("No battery found");

            double? watts = 0;
            var flows = new List<Flow>();
            var relativeUnits = new HashSet<bool>();
            double remaining = 0, full = 0, design = 0;
            bool percentKnown = true, designKnown = true;
            foreach (var b in batteries)
            {
                bool relative = (b.Capabilities & CapacityRelative) != 0;
                relativeUnits.Add(relative);
                bool charging = (b.PowerState & Charging) != 0, discharging = (b.PowerState & Discharging) != 0;
                flows.Add(charging ? Flow.Charging : discharging ? Flow.Discharging : Flow.Idle);

                double? w = null;  // unit-less, unknown, or a direction flag without a rate
                if (!relative && b.Rate != UnknownRate && !((charging || discharging) && b.Rate == 0)
                    && Math.Abs(b.Rate / 1000.0) <= MaxPlausibleWatts)
                    w = b.Rate / 1000.0;
                watts = watts.HasValue && w.HasValue ? watts + w : null;

                if (b.Capacity == UnknownCapacity || b.FullChargedCapacity == UnknownCapacity || b.FullChargedCapacity == 0)
                    percentKnown = false;
                else
                {
                    remaining += b.Capacity;
                    full += b.FullChargedCapacity;
                }
                if (b.DesignedCapacity == 0 || b.DesignedCapacity == UnknownCapacity) designKnown = false;
                else design += b.DesignedCapacity;
            }
            bool absolute = !relativeUnits.Contains(true);
            bool energyKnown = absolute && percentKnown && full > 0;

            var extra = new List<string>();
            if (batteries.Count > 1) extra.Add(batteries.Count + " batteries");
            if (relativeUnits.Contains(true)) extra.Add("Rate reported in relative units");
            return new Reading
            {
                Watts = watts,
                // capacity weighted; mixing absolute and relative units has no meaningful total
                Percent = percentKnown && relativeUnits.Count == 1 && full > 0 ? Math.Min(100, 100 * remaining / full) : (double?)null,
                Plugged = acOnline ?? batteries.Any(b => (b.PowerState & PowerOnLine) != 0),
                Flow = CombineFlows(flows),
                Extra = string.Join("  ·  ", extra),
                RemainingWh = energyKnown ? remaining / 1000 : (double?)null,
                FullWh = energyKnown ? full / 1000 : (double?)null,
                DesignWh = absolute && designKnown ? design / 1000 : (double?)null,
                Cycles = batteries.Count == 1 && batteries[0].CycleCount > 0 ? (int)batteries[0].CycleCount : (int?)null,
            };
        }

        static Flow CombineFlows(List<Flow> flows)
        {
            var active = flows.Where(f => f != Flow.Idle).Distinct().ToList();
            if (active.Count == 0) return Flow.Idle;
            return active.Count == 1 ? active[0] : Flow.Unknown;
        }
    }

    sealed class Snapshot
    {
        public readonly int Seq;         // increments on every read attempt
        public readonly double Time;     // Clock.Now() when the attempt finished
        public readonly Reading Reading; // null if the attempt failed
        public readonly string Error;

        public Snapshot(int seq, double time, Reading reading, string error)
        {
            Seq = seq; Time = time; Reading = reading; Error = error;
        }
    }

    static class Clock
    {
        public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    /// <summary>Reads the battery on a background thread. State is replaced as one object
    /// (latest attempt, latest success), so the UI always sees a coherent pair.</summary>
    sealed class Poller
    {
        public sealed class PollState
        {
            public readonly Snapshot Attempt, Good;
            public PollState(Snapshot attempt, Snapshot good) { Attempt = attempt; Good = good; }
        }

        readonly Func<Reading> read;
        readonly TimeSpan interval;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        volatile PollState state = new PollState(null, null);
        Thread thread;
        int seq;

        public Poller(Func<Reading> read, TimeSpan interval)
        {
            this.read = read;
            this.interval = interval;
        }

        public PollState State => state;

        public Snapshot PollOnce()
        {
            seq++;
            Reading reading = null;
            string error = null;
            try
            {
                reading = read();
            }
            catch (Exception e)  // keep polling after a bad read, but surface it
            {
                error = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
            }
            var snap = new Snapshot(seq, Clock.Now(), reading, error);
            state = new PollState(snap, reading != null ? snap : state.Good);
            return snap;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "battery-poller" };
            thread.Start();
        }

        void Run()
        {
            do
            {
                var started = Stopwatch.StartNew();
                PollOnce();
                var wait = interval - started.Elapsed;
                if (stop.WaitOne(wait > TimeSpan.Zero ? wait : TimeSpan.Zero)) break;
            } while (true);
        }

        public void Stop()
        {
            stop.Set();
            thread?.Join(3000);
        }
    }

    /// <summary>What a colour means; the active theme decides the actual colour.</summary>
    enum Tone { Fg, Dim, Charging, Discharging, Idle, Stale }

    enum ThemeMode { System, Dark, Light }

    sealed class Theme
    {
        public Color Bg, Fg, Dim, GraphBg, ZeroLine, Stale, Charging, Discharging, Idle;
        public bool IsDark;

        public static readonly Theme Dark = new Theme
        {
            IsDark = true, Bg = Hex("#16181d"), Fg = Hex("#f2f2f2"), Dim = Hex("#8a8f98"), GraphBg = Hex("#1e2128"),
            ZeroLine = Hex("#3a3f4a"), Stale = Hex("#4b505a"), Charging = Hex("#3ddc84"), Discharging = Hex("#ffa24c"), Idle = Hex("#6c7280"),
        };

        public static readonly Theme Light = new Theme
        {
            IsDark = false, Bg = Hex("#f6f7f9"), Fg = Hex("#1b1d22"), Dim = Hex("#5f6670"), GraphBg = Hex("#e9ebef"),
            ZeroLine = Hex("#c2c6cd"), Stale = Hex("#a9aeb6"), Charging = Hex("#16924c"), Discharging = Hex("#c8650a"), Idle = Hex("#6c7280"),
        };

        public static Theme Current = Dark;

        public Color Of(Tone tone)
        {
            switch (tone)
            {
                case Tone.Dim: return Dim;
                case Tone.Charging: return Charging;
                case Tone.Discharging: return Discharging;
                case Tone.Idle: return Idle;
                case Tone.Stale: return Stale;
                default: return Fg;
            }
        }

        /// <summary>Windows' own app theme setting (Settings > Personalization > Colors).</summary>
        public static bool SystemUsesLight()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
            }
            catch (Exception) { return false; }
        }

        public static Theme Resolve(ThemeMode mode) =>
            mode == ThemeMode.Light ? Light : mode == ThemeMode.Dark ? Dark : SystemUsesLight() ? Light : Dark;

        static Color Hex(string s) => ColorTranslator.FromHtml(s);
    }

    struct View
    {
        public string Big, Status, Detail, Tooltip;
        public Tone BigTone, StatusTone, TrayTone;
        public double? TrayWatts;  // null = tray shows "--"
    }

    static class Present
    {
        public const double StaleAfter = 10;  // seconds without a fresh reading before it is marked stale

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Tone and label. Direction comes from the measured wattage when there is one,
        /// so "plugged in but draining" is not hidden behind the external-power flag.</summary>
        public static void Classify(Reading r, out Tone color, out string label)
        {
            Flow flow = r.Watts.HasValue
                ? (r.Watts > Battery.IdleWatts ? Flow.Charging : r.Watts < -Battery.IdleWatts ? Flow.Discharging : Flow.Idle)
                : r.Flow;
            switch (flow)
            {
                case Flow.Charging:
                    color = Tone.Charging; label = "Charging"; break;
                case Flow.Discharging:
                    color = Tone.Discharging;
                    label = r.Plugged == true ? "Plugged in · discharging" : r.Plugged == false ? "On battery" : "Discharging";
                    break;
                case Flow.Idle:
                    if (r.Plugged == false) { color = Tone.Discharging; label = "On battery"; }
                    else { color = Tone.Idle; label = r.Plugged == true ? "Plugged in · not charging" : "Idle"; }
                    break;
                default:
                    if (r.Plugged == false) { color = Tone.Discharging; label = "On battery"; }
                    else { color = Tone.Idle; label = r.Plugged == true ? "Plugged in" : "Status unknown"; }
                    break;
            }
            if (!r.Watts.HasValue) label += " · rate unavailable";
        }

        public static string FormatWatts(double? w)
        {
            if (!w.HasValue) return "-- W";
            return Math.Abs(w.Value) >= 0.05 ? (w.Value > 0 ? "+" : "") + w.Value.ToString("0.0", Inv) + " W" : "0.0 W";
        }

        static string Ago(double seconds) =>
            seconds < 120 ? seconds.ToString("0", Inv) + " s" : (seconds / 60).ToString("0", Inv) + " min";

        public static View Describe(Snapshot attempt, Snapshot good, double now, string timeLeft = null)
        {
            const string App = AppInfo.Name;
            if (attempt == null)
                return new View { Big = "--", BigTone = Tone.Fg, Status = "Reading battery…", StatusTone = Tone.Dim, Detail = "",
                                  TrayTone = Tone.Dim, Tooltip = App + ": reading battery…" };
            if (good == null)
            {
                string msg = attempt.Error ?? "No battery data";
                return new View { Big = "--", BigTone = Tone.Fg, Status = msg, StatusTone = Tone.Dim, Detail = "",
                                  TrayTone = Tone.Dim, Tooltip = App + ": " + msg };
            }

            var r = good.Reading;
            Classify(r, out Tone color, out string label);
            var parts = new List<string>();
            if (r.Percent.HasValue) parts.Add("Battery " + r.Percent.Value.ToString("0", Inv) + "%");
            if (timeLeft != null) parts.Add(timeLeft);
            if (r.Extra.Length > 0) parts.Add(r.Extra);

            double age = now - good.Time;
            if (attempt.Error != null || age > StaleAfter)
            {
                string why = attempt.Error ?? "Waiting for a new reading";
                string status = "Stale · last reading " + Ago(age) + " ago";
                return new View { Big = FormatWatts(r.Watts), BigTone = Tone.Stale, Status = status, StatusTone = Tone.Dim,
                                  Detail = why, TrayTone = Tone.Dim, Tooltip = App + ": " + status + " (" + why + ")" };
            }

            string tip = App + ": " + FormatWatts(r.Watts) + " - " + label;
            if (r.Percent.HasValue) tip += " (" + r.Percent.Value.ToString("0", Inv) + "%)";
            if (timeLeft != null) tip += " · " + timeLeft;
            return new View { Big = FormatWatts(r.Watts), BigTone = color, Status = label, StatusTone = color,
                              Detail = string.Join("  ·  ", parts), TrayWatts = r.Watts, TrayTone = color, Tooltip = tip };
        }
    }
}

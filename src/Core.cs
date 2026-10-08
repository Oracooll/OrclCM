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
    }

    /// <summary>One measurement. Null means the value is unavailable - never a fake zero.</summary>
    sealed class Reading
    {
        public double? Watts;
        public double? Percent;
        public bool? Plugged;
        public Flow Flow;  // the OS's own direction hint, used when Watts is unavailable
        public string Extra = "";
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
            double remaining = 0, full = 0;
            bool percentKnown = true;
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
            }

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

    struct View
    {
        public string Big, Status, Detail, Tooltip;
        public Color BigColor, StatusColor, TrayColor;
        public double? TrayWatts;  // null = tray shows "--"
    }

    static class Present
    {
        public static readonly Color Bg = Hex("#16181d"), Fg = Hex("#f2f2f2"), Dim = Hex("#8a8f98");
        public static readonly Color Green = Hex("#3ddc84"), Orange = Hex("#ffa24c"), Grey = Hex("#6c7280"), Stale = Hex("#4b505a");
        public const double StaleAfter = 10;  // seconds without a fresh reading before it is marked stale

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Color Hex(string s) => ColorTranslator.FromHtml(s);

        /// <summary>Colour and label. Direction comes from the measured wattage when there is one,
        /// so "plugged in but draining" is not hidden behind the external-power flag.</summary>
        public static void Classify(Reading r, out Color color, out string label)
        {
            Flow flow = r.Watts.HasValue
                ? (r.Watts > Battery.IdleWatts ? Flow.Charging : r.Watts < -Battery.IdleWatts ? Flow.Discharging : Flow.Idle)
                : r.Flow;
            switch (flow)
            {
                case Flow.Charging:
                    color = Green; label = "Charging"; break;
                case Flow.Discharging:
                    color = Orange;
                    label = r.Plugged == true ? "Plugged in · discharging" : r.Plugged == false ? "On battery" : "Discharging";
                    break;
                case Flow.Idle:
                    if (r.Plugged == false) { color = Orange; label = "On battery"; }
                    else { color = Grey; label = r.Plugged == true ? "Plugged in · not charging" : "Idle"; }
                    break;
                default:
                    if (r.Plugged == false) { color = Orange; label = "On battery"; }
                    else { color = Grey; label = r.Plugged == true ? "Plugged in" : "Status unknown"; }
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

        public static View Describe(Snapshot attempt, Snapshot good, double now)
        {
            const string App = AppInfo.Name;
            if (attempt == null)
                return new View { Big = "--", BigColor = Fg, Status = "Reading battery…", StatusColor = Dim, Detail = "",
                                  TrayColor = Dim, Tooltip = App + ": reading battery…" };
            if (good == null)
            {
                string msg = attempt.Error ?? "No battery data";
                return new View { Big = "--", BigColor = Fg, Status = msg, StatusColor = Dim, Detail = "",
                                  TrayColor = Dim, Tooltip = App + ": " + msg };
            }

            var r = good.Reading;
            Classify(r, out Color color, out string label);
            var parts = new List<string>();
            if (r.Percent.HasValue) parts.Add("Battery " + r.Percent.Value.ToString("0", Inv) + "%");
            if (r.Extra.Length > 0) parts.Add(r.Extra);

            double age = now - good.Time;
            if (attempt.Error != null || age > StaleAfter)
            {
                string why = attempt.Error ?? "Waiting for a new reading";
                string status = "Stale · last reading " + Ago(age) + " ago";
                return new View { Big = FormatWatts(r.Watts), BigColor = Stale, Status = status, StatusColor = Dim,
                                  Detail = why, TrayColor = Dim, Tooltip = App + ": " + status + " (" + why + ")" };
            }

            string tip = App + ": " + FormatWatts(r.Watts) + " - " + label;
            if (r.Percent.HasValue) tip += " (" + r.Percent.Value.ToString("0", Inv) + "%)";
            return new View { Big = FormatWatts(r.Watts), BigColor = color, Status = label, StatusColor = color,
                              Detail = string.Join("  ·  ", parts), TrayWatts = r.Watts, TrayColor = color, Tooltip = tip };
        }
    }
}

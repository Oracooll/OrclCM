#!/usr/bin/env python3
"""
OrclCM - live view of the wattage your laptop battery is charging (or draining) at.

Works on Windows, macOS and Linux. Needs only Python 3.8+ (standard library, incl. tkinter).
On Windows it lives in the system tray only (no taskbar button); the tray icon shows the
live wattage. Tray support needs:  pip install pystray pillow   (optional - without it the
app is an ordinary window with a taskbar button).

Run:   python orclcm.py             (Windows: you can also rename to .pyw to hide the console)

Positive watts = power going INTO the battery (charging).
Negative watts = power coming OUT of the battery (running on battery).
Note: this is the battery's charge rate, not the charger's total output - while charging,
the laptop itself also consumes power from the charger. On Macs that report it, the app
additionally shows the adapter input power.

If the OS does not report a value it is shown as unavailable ("--"), never as 0 W, and a
reading that could not be refreshed is visibly marked as stale.
"""
import base64
import functools
import glob
import json
import logging
import math
import os
import platform
import plistlib
import queue
import subprocess
import sys
import threading
import time
import tkinter as tk
from collections import deque
from typing import NamedTuple, Optional

try:  # optional: system-tray icon (pip install pystray pillow)
    from PIL import Image, ImageDraw, ImageFont
    import pystray
except Exception:  # app still works without tray support
    pystray = None

APP_NAME = "OrclCM"
VERSION = "1.2.000"  # major.minor.build - shown in the window title bar

POLL_SECONDS = 2
HISTORY = 120  # samples kept in the graph
STALE_AFTER = 3 * POLL_SECONDS + 4  # seconds without a fresh reading before it is marked stale
TRAY_WAIT_MS = 1500  # how long the window waits for the tray icon before showing anyway
IDLE_WATTS = 0.2  # |W| at or below this counts as "no power flow"
MAX_PLAUSIBLE_W = 500.0  # larger rates are treated as bogus firmware values

log = logging.getLogger("orclcm")
log.addHandler(logging.NullHandler())


def _num(x):
    """Finite float, or None for missing / non-numeric / NaN / bool values."""
    if isinstance(x, bool):
        return None
    try:
        v = float(x)
    except (TypeError, ValueError):
        return None
    return v if math.isfinite(v) else None


def make_reading(watts, percent, plugged, flow=None, extra=""):
    """One measurement. Any of watts / percent / plugged / flow may be None = unavailable.
    flow is the OS's own charge-direction hint: "charging", "discharging", "idle" or None."""
    if percent is not None:
        percent = max(0.0, min(100.0, percent))
    return {"watts": watts, "percent": percent, "plugged": plugged, "flow": flow, "extra": extra}


def _combine_flows(flows):
    """Overall direction hint for several batteries (None if it can't be told)."""
    s = set(flows)
    if s == {"idle"}:
        return "idle"
    s.discard("idle")
    return next(iter(s)) if len(s) == 1 and None not in s else None


# ----------------------------------------------------------------------------- Linux
_LINUX_EXTERNAL = ("Mains", "USB", "USB_C", "USB_PD", "USB_PD_DRP", "BrickID", "Wireless")
_LINUX_ATTRS = ("type", "scope", "present", "online", "status", "power_now", "current_now",
                "voltage_now", "capacity", "energy_now", "energy_full", "charge_now",
                "charge_full", "voltage_min_design")


def linux_supplies(base="/sys/class/power_supply"):
    """{supply name: {attribute: raw string}} - attributes a driver omits are simply absent."""
    supplies = {}
    for d in sorted(glob.glob(os.path.join(base, "*"))):
        attrs = {}
        for name in _LINUX_ATTRS:
            try:
                with open(os.path.join(d, name)) as f:
                    attrs[name] = f.read().strip()
            except OSError:
                pass
        supplies[os.path.basename(d)] = attrs
    return supplies


def _linux_energy(a):
    """(now, full) in comparable energy units, or None if the pack doesn't report them."""
    now, full = _num(a.get("energy_now")), _num(a.get("energy_full"))  # µWh
    if now is not None and full:
        return now, full
    now, full = _num(a.get("charge_now")), _num(a.get("charge_full"))  # µAh
    volt = _num(a.get("voltage_min_design"))  # µV - converts charge to energy
    if now is not None and full and volt:
        return now * volt / 1e6, full * volt / 1e6  # µAh × µV / 1e6 = µWh
    return None


def parse_linux(supplies):
    batteries, plugged = [], None
    for _, a in sorted(supplies.items()):
        kind = a.get("type")
        if kind == "Battery":
            if a.get("scope") == "Device" or a.get("present") == "0":
                continue  # peripheral (mouse, ...) or removed battery
            batteries.append(a)
        elif kind in _LINUX_EXTERNAL:
            if a.get("online") == "1":
                plugged = True
            elif a.get("online") == "0" and plugged is None:
                plugged = False
    if not batteries:
        raise RuntimeError("No battery found in /sys/class/power_supply")

    watts, flows = 0.0, []
    for a in batteries:
        p = _num(a.get("power_now"))  # µW
        if p is None:
            i, v = _num(a.get("current_now")), _num(a.get("voltage_now"))  # µA, µV
            p = i * v / 1e6 if i is not None and v is not None else None
        p = abs(p) / 1e6 if p is not None else None  # W, sign comes from status
        st = (a.get("status") or "").lower()
        if st == "charging":
            flow, w = "charging", p
        elif st == "discharging":
            flow, w = "discharging", (-p if p is not None else None)
        elif st in ("full", "not charging"):
            # the driver says nothing flows; trust it unless it reports real power anyway
            flow, w = "idle", (0.0 if p is None or p <= IDLE_WATTS else None)
        else:  # "unknown" or missing: direction can't be told
            flow, w = None, None
        flows.append(flow)
        watts = None if watts is None or w is None else watts + w
        if watts is not None and abs(watts) > MAX_PLAUSIBLE_W:
            watts = None

    # Overall percentage weighted by capacity; an unweighted mean is wrong for unequal packs.
    energies = [_linux_energy(a) for a in batteries]
    extra = ""
    if all(e is not None for e in energies):
        percent = 100.0 * sum(e[0] for e in energies) / sum(e[1] for e in energies)
    elif len(batteries) == 1:
        percent = _num(batteries[0].get("capacity"))
    else:  # can't weight them - show each pack instead of a misleading average
        percent = None
        caps = [_num(a.get("capacity")) for a in batteries]
        extra = "Batteries: " + " / ".join("?" if c is None else f"{c:.0f}%" for c in caps)

    if plugged is None:  # no external supply listed: infer only from a clear battery status
        if "discharging" in flows:
            plugged = False
        elif "charging" in flows:
            plugged = True
    return make_reading(watts, percent, plugged, _combine_flows(flows), extra)


class LinuxReader:
    def read(self):
        return parse_linux(linux_supplies())

    def close(self):
        pass


# ----------------------------------------------------------------------------- Windows
BATTERY_CAPACITY_RELATIVE = 0x40000000
_U32_UNKNOWN = 0xFFFFFFFF  # BATTERY_UNKNOWN_CAPACITY

# Long-running helper: answers one JSON line per request line, so PowerShell is started
# once instead of on every sample. BatteryStaticData (capability flags) often needs admin
# rights; it is tried once per process and is optional.
_WIN_PS = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$static = $null
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($line -eq $null -or $line -eq 'exit') { break }
  try {
    $status = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus |
      Select-Object InstanceName, Charging, Discharging, PowerOnline, ChargeRate, DischargeRate, RemainingCapacity)
    try {
      $full = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity |
        Select-Object InstanceName, FullChargedCapacity)
    } catch { $full = @() }
    if ($static -eq $null) {
      try {
        $static = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData |
          Select-Object InstanceName, Capabilities)
      } catch { $static = @() }
    }
    $json = ConvertTo-Json -Compress -Depth 4 -InputObject @{ status = $status; full = $full; static = $static }
  } catch {
    $json = ConvertTo-Json -Compress -InputObject @{ error = $_.Exception.Message }
  }
  [Console]::Out.WriteLine($json)
  [Console]::Out.Flush()
}
"""


def _as_list(x):
    if x is None:
        return []
    if isinstance(x, dict):
        if isinstance(x.get("value"), list):  # Windows PowerShell 5.1 array-wrapping quirk
            return x["value"]
        return [x]
    return list(x)


def _win_rate(v):
    """mW rate, or None for missing / BATTERY_UNKNOWN_RATE (0x80000000) / implausible values."""
    v = _num(v)
    return v if v is not None and 0 <= v <= MAX_PLAUSIBLE_W * 1000 else None


def _win_capacity(v):
    v = _num(v)
    return v if v is not None and 0 <= v < _U32_UNKNOWN else None


def parse_windows(data):
    if data.get("error"):
        raise RuntimeError("Battery query failed: " + str(data["error"]))
    status = [r for r in _as_list(data.get("status")) if isinstance(r, dict)]
    if not status:
        raise RuntimeError("Windows did not report battery data (no battery?)")

    def by_name(key):
        return {r.get("InstanceName"): r for r in _as_list(data.get(key)) if isinstance(r, dict)}

    full, static = by_name("full"), by_name("static")
    watts, flows, units = 0.0, [], set()
    rem_sum = full_sum = 0.0
    percent_ok = True
    for b in status:
        name = b.get("InstanceName")  # match the per-battery records by identity, not order
        fcc = _win_capacity((full.get(name) or {}).get("FullChargedCapacity"))
        rem = _win_capacity(b.get("RemainingCapacity"))
        caps = _num((static.get(name) or {}).get("Capabilities"))
        if caps is not None:
            relative = bool(int(caps) & BATTERY_CAPACITY_RELATIVE)
        else:  # flags unreadable: no real pack holds <= 0.1 Wh, so tiny values are percentages
            relative = fcc is not None and fcc <= 100
        units.add(relative)

        charging, discharging = bool(b.get("Charging")), bool(b.get("Discharging"))
        flows.append("charging" if charging else "discharging" if discharging else "idle")
        c, d = _win_rate(b.get("ChargeRate")), _win_rate(b.get("DischargeRate"))
        if relative or (c is None and d is None) or (charging and not c) or (discharging and not d):
            w = None  # unit-less, unknown, or a direction flag with no matching rate
        else:
            w = ((c or 0.0) - (d or 0.0)) / 1000.0
        watts = None if watts is None or w is None else watts + w

        if rem is None or not fcc:
            percent_ok = False
        else:
            rem_sum += rem
            full_sum += fcc

    percent = 100.0 * rem_sum / full_sum if percent_ok and len(units) == 1 and full_sum > 0 else None
    online = [b.get("PowerOnline") for b in status if isinstance(b.get("PowerOnline"), bool)]
    plugged = any(online) if online else None
    extra = []
    if len(status) > 1:
        extra.append(f"{len(status)} batteries")
    if True in units:
        extra.append("Rate reported in relative units")
    return make_reading(watts, percent, plugged, _combine_flows(flows), "  ·  ".join(extra))


class WindowsReader:
    TIMEOUT = 20  # seconds to wait for one answer

    def __init__(self):
        root = os.environ.get("SystemRoot") or r"C:\Windows"
        self._exe = os.path.join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        self._lock = threading.Lock()
        self._proc = self._lines = None
        self._closed = False

    def _start(self):
        enc = base64.b64encode(_WIN_PS.encode("utf-16-le")).decode("ascii")
        proc = subprocess.Popen(
            [self._exe, "-NoProfile", "-NonInteractive", "-EncodedCommand", enc],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
            creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
        lines = queue.Queue()
        threading.Thread(target=self._pump, args=(proc.stdout, lines), daemon=True).start()
        self._proc, self._lines = proc, lines

    @staticmethod
    def _pump(stream, lines):
        try:
            for line in stream:
                lines.put(line)
        except (OSError, ValueError):
            pass
        finally:
            lines.put(None)  # EOF

    def _discard(self, proc):
        with self._lock:
            if self._proc is proc:
                self._proc = self._lines = None
        _kill(proc)

    def read(self):
        with self._lock:
            if self._closed:
                raise RuntimeError("Reader closed")
            if self._proc is None or self._proc.poll() is not None:
                self._start()
            proc, lines = self._proc, self._lines
        deadline = time.monotonic() + self.TIMEOUT
        try:
            proc.stdin.write("\n")
            proc.stdin.flush()
            while True:
                line = lines.get(timeout=max(0.0, deadline - time.monotonic()))
                if line is None:
                    raise RuntimeError("PowerShell helper exited unexpectedly")
                line = line.strip().lstrip("\ufeff")
                if line.startswith("{"):
                    break
        except queue.Empty:
            self._discard(proc)
            raise RuntimeError(f"Battery query timed out after {self.TIMEOUT} s")
        except (OSError, ValueError, RuntimeError) as e:
            self._discard(proc)
            raise RuntimeError(str(e) or "PowerShell helper failed")
        return parse_windows(json.loads(line))

    def close(self):
        with self._lock:
            self._closed = True
            proc, self._proc = self._proc, None
        if proc:
            try:
                proc.stdin.close()  # helper loop ends on EOF
                proc.wait(1)
            except Exception:
                pass
            _kill(proc)


def _kill(proc):
    if proc.poll() is None:
        try:
            proc.kill()
            proc.wait(2)
        except Exception:
            pass


# ----------------------------------------------------------------------------- macOS
def _s64(v):
    v = int(v)
    return v - 2**64 if v >= 2**63 else v


def parse_macos(data):
    if not data:
        raise RuntimeError("No battery found")
    b = data[0]
    ext = b.get("ExternalConnected")
    plugged = ext if isinstance(ext, bool) else None

    volt = _num(b.get("Voltage"))  # mV
    amp = b.get("InstantAmperage", b.get("Amperage"))  # mA, signed (may arrive as unsigned 64-bit)
    amp = _s64(amp) if isinstance(amp, int) and not isinstance(amp, bool) else None
    watts = None
    if volt is not None and 2000 <= volt <= 30000 and amp is not None:
        watts = volt * amp / 1e6
        if abs(watts) > MAX_PLAUSIBLE_W:
            watts = None

    cur = _num(b.get("AppleRawCurrentCapacity", b.get("CurrentCapacity")))
    mx = _num(b.get("AppleRawMaxCapacity", b.get("MaxCapacity")))
    percent = 100.0 * cur / mx if cur is not None and mx else None

    if b.get("IsCharging") is True:
        flow = "charging"
    elif plugged is False:
        flow = "discharging"
    elif plugged and b.get("FullyCharged") is True:
        flow = "idle"
    else:
        flow = None

    extra = []
    if plugged:
        tel = b.get("PowerTelemetryData")
        sys_in = _num(tel.get("SystemPowerIn")) if isinstance(tel, dict) else None  # mW
        if sys_in is not None and 0 < sys_in <= MAX_PLAUSIBLE_W * 1000:
            extra.append(f"Adapter input: {sys_in / 1000:.1f} W")
        details = b.get("AdapterDetails")
        rated = _num(details.get("Watts")) if isinstance(details, dict) else None
        if rated is not None and 0 < rated <= MAX_PLAUSIBLE_W:
            extra.append(f"Charger rated {rated:.0f} W")
    return make_reading(watts, percent, plugged, flow, "  ·  ".join(extra))


class MacReader:
    TIMEOUT = 10

    def __init__(self):
        self._lock = threading.Lock()
        self._proc = None
        self._closed = False

    def read(self):
        with self._lock:
            if self._closed:
                raise RuntimeError("Reader closed")
            self._proc = proc = subprocess.Popen(
                ["/usr/sbin/ioreg", "-rw0", "-c", "AppleSmartBattery", "-a"],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            )
        try:
            out, _ = proc.communicate(timeout=self.TIMEOUT)
        except subprocess.TimeoutExpired:
            _kill(proc)
            raise RuntimeError(f"ioreg timed out after {self.TIMEOUT} s")
        finally:
            with self._lock:
                self._proc = None
        return parse_macos(plistlib.loads(out) if out else [])

    def close(self):
        with self._lock:
            self._closed = True
            proc = self._proc
        if proc:
            _kill(proc)


READERS = {"Windows": WindowsReader, "Darwin": MacReader, "Linux": LinuxReader}


def make_reader():
    cls = READERS.get(platform.system())
    return cls() if cls else None


# ----------------------------------------------------------------------------- polling
class Snapshot(NamedTuple):
    seq: int  # increments on every read attempt
    time: float  # time.monotonic() when the attempt finished
    reading: Optional[dict]  # None if the attempt failed
    error: Optional[str]


def _error_text(e):
    return str(e) or type(e).__name__


class Poller:
    """Reads the battery on a background thread. `state` is replaced as one tuple
    (latest attempt, latest successful attempt), so readers always see a coherent pair."""

    def __init__(self, reader, interval=POLL_SECONDS):
        self.reader = reader
        self.interval = interval
        self.state = (None, None)
        self.stop_event = threading.Event()
        self._seq = 0
        self._last_error = None
        self._thread = None

    def start(self):
        self._thread = threading.Thread(target=self._run, name="battery-poller", daemon=True)
        self._thread.start()

    def poll_once(self):
        self._seq += 1
        try:
            reading, error = self.reader.read(), None
        except Exception as e:  # keep polling after a bad read, but surface it
            reading, error = None, _error_text(e)
            if error != self._last_error and not self.stop_event.is_set():
                log.warning("Battery read failed: %s", error, exc_info=True)
        else:
            if self._last_error is not None:
                log.info("Battery reads recovered")
        self._last_error = error
        snap = Snapshot(self._seq, time.monotonic(), reading, error)
        self.state = (snap, snap if reading is not None else self.state[1])
        return snap

    def _run(self):
        while not self.stop_event.is_set():
            started = time.monotonic()
            self.poll_once()
            self.stop_event.wait(max(0.0, self.interval - (time.monotonic() - started)))

    def stop(self, timeout=3.0):
        self.stop_event.set()
        self.reader.close()  # also aborts a read in progress
        if self._thread:
            self._thread.join(timeout)


# ----------------------------------------------------------------------------- presentation
BG, FG, DIM = "#16181d", "#f2f2f2", "#8a8f98"
GREEN, ORANGE, GREY, STALE = "#3ddc84", "#ffa24c", "#6c7280", "#4b505a"


def classify(r):
    """(colour, label) for a reading. Direction comes from the measured wattage when there
    is one, so 'plugged in but draining' is not hidden behind the external-power flag."""
    w, plugged = r["watts"], r["plugged"]
    if w is not None:
        flow = "charging" if w > IDLE_WATTS else "discharging" if w < -IDLE_WATTS else "idle"
    else:
        flow = r.get("flow")
    if flow == "charging":
        color, label = GREEN, "Charging"
    elif flow == "discharging":
        color = ORANGE
        label = {True: "Plugged in · discharging", False: "On battery"}.get(plugged, "Discharging")
    elif flow == "idle":
        color, label = {True: (GREY, "Plugged in · not charging"),
                        False: (ORANGE, "On battery")}.get(plugged, (GREY, "Idle"))
    else:
        color, label = {True: (GREY, "Plugged in"),
                        False: (ORANGE, "On battery")}.get(plugged, (GREY, "Status unknown"))
    if w is None:
        label += " · rate unavailable"
    return color, label


def format_watts(w):
    if w is None:
        return "-- W"
    return f"{w:+.1f} W" if abs(w) >= 0.05 else "0.0 W"


def _ago(seconds):
    return f"{seconds:.0f} s" if seconds < 120 else f"{seconds / 60:.0f} min"


class View(NamedTuple):
    big: str
    big_color: str
    status: str
    status_color: str
    detail: str
    tray_watts: Optional[float]
    tray_color: str
    tooltip: str


def describe(attempt, good, now):
    """What to display, given the latest attempt, the latest good snapshot and the time."""
    if attempt is None:
        return View("--", FG, "Reading battery…", DIM, "", None, DIM, "OrclCM: reading battery…")
    if good is None:
        msg = attempt.error or "No battery data"
        return View("--", FG, msg, DIM, "", None, DIM, "OrclCM: " + msg)

    r = good.reading
    color, label = classify(r)
    parts = []
    if r["percent"] is not None:
        parts.append(f"Battery {r['percent']:.0f}%")
    if r["extra"]:
        parts.append(r["extra"])

    age = now - good.time
    if attempt.error is not None or age > STALE_AFTER:
        why = attempt.error or "Waiting for a new reading"
        status = f"Stale · last reading {_ago(age)} ago"
        return View(format_watts(r["watts"]), STALE, status, DIM, why, None, DIM,
                    f"OrclCM: {status} ({why})")

    tip = f"OrclCM: {format_watts(r['watts'])} - {label}"
    if r["percent"] is not None:
        tip += f" ({r['percent']:.0f}%)"
    return View(format_watts(r["watts"]), color, label, color, "  ·  ".join(parts),
                r["watts"], color, tip)


# ----------------------------------------------------------------------------- tray icon
@functools.lru_cache(maxsize=None)
def _load_font(px):
    for name in ("segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf", "Arial Bold.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except Exception:
            pass
    try:
        return ImageFont.load_default(size=px)
    except Exception:
        return ImageFont.load_default()


def render_tray_icon(watts, color):
    """Draw the wattage as small digits on a 64x64 icon (Windows scales it to tray size)."""
    S = 64
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, S - 1, S - 1), radius=12, fill=BG)
    if watts is None:
        text = "--"
    else:
        w = abs(watts)
        text = f"{w:.1f}" if w < 9.95 else f"{w:.0f}"
        if len(text) > 3:
            text = "99+"
    # pick the largest font that still fits with a little margin
    for px in range(46, 12, -2):
        font = _load_font(px)
        l, t, r, b = d.textbbox((0, 0), text, font=font)
        if r - l <= S - 6 and b - t <= S - 22:
            break
    tw, th = r - l, b - t
    d.text(((S - tw) / 2 - l, (S - 14 - th) / 2 - t), text, font=font, fill=color)
    # status-coloured bar under the digits
    d.rectangle((6, S - 10, S - 7, S - 6), fill=color)
    return img


class Tray:
    """System-tray icon. pystray runs on its own thread; actions are passed back
    to the Tk thread through a queue so Tk is only touched from its own thread.

    On Windows, once the icon reports that it is up, the app becomes tray-only: the window
    has no taskbar button and closing it hides it to the tray. Until then, or if the icon
    fails, the window behaves normally, so it can never become unreachable. Elsewhere the
    tray icon is shown but the window keeps its taskbar entry. macOS is skipped: pystray
    needs the main thread there, which Tk already owns."""

    def __init__(self, actions):
        self.actions = actions
        self.icon = None
        self.topmost = False
        self.ready = threading.Event()
        self._failed = False
        self._stopping = False
        self._last = None
        if pystray is None or platform.system() == "Darwin":
            return
        menu = pystray.Menu(
            pystray.MenuItem("Show / hide window", lambda: actions.put("toggle"), default=True),
            pystray.MenuItem("Always on top", lambda: actions.put("top"),
                             checked=lambda item: self.topmost),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Exit", lambda: actions.put("quit")),
        )
        try:
            self.icon = pystray.Icon(APP_NAME, render_tray_icon(None, DIM), APP_NAME, menu)
        except Exception:
            log.warning("Tray icon unavailable", exc_info=True)
            return
        threading.Thread(target=self._run, name="tray", daemon=True).start()

    def _run(self):
        try:
            self.icon.run(setup=self._setup)
        except Exception:
            log.warning("Tray icon failed", exc_info=True)
        finally:
            if not self._stopping:  # the icon went away on its own: bring the window back
                self._failed = True
                self.ready.clear()
                self.actions.put("tray_lost")

    def _setup(self, icon):
        icon.visible = True
        self.ready.set()
        self.actions.put("tray_ready")

    @property
    def available(self):
        return self.icon is not None and self.ready.is_set() and not self._failed

    @property
    def can_hide_window(self):
        return self.available and platform.system() == "Windows"

    def update(self, watts, color, tooltip):
        if not self.available:
            return
        key = (None if watts is None else round(abs(watts), 1), color)
        try:
            if key != self._last:
                self.icon.icon = render_tray_icon(watts, color)
                self._last = key
            self.icon.title = tooltip[:127]
        except Exception:
            log.debug("Tray update failed", exc_info=True)

    def refresh_menu(self):
        if self.available:
            try:
                self.icon.update_menu()
            except Exception:
                pass

    def stop(self):
        self._stopping = True
        if self.icon:
            try:
                self.icon.stop()
            except Exception:
                pass


# ----------------------------------------------------------------------------- GUI
class App:
    def __init__(self, root, reader=None):
        self.root = root
        self.history = deque(maxlen=HISTORY)  # watts per attempt, None = failed / unavailable
        self._last_seq = None
        self._quitting = False
        self._shown_once = False

        root.title(f"{APP_NAME} {VERSION}")
        root.configure(bg=BG)
        root.geometry("380x300")
        root.minsize(300, 240)

        self.big = tk.Label(root, text="--", font=("Helvetica", 48, "bold"), bg=BG, fg=FG)
        self.big.pack(pady=(14, 0))
        self.status = tk.Label(root, text="Reading battery…", font=("Helvetica", 13), bg=BG, fg=DIM,
                               wraplength=350)
        self.status.pack()
        self.detail = tk.Label(root, text="", font=("Helvetica", 11), bg=BG, fg=DIM, wraplength=350)
        self.detail.pack(pady=(2, 6))
        self.canvas = tk.Canvas(root, bg="#1e2128", highlightthickness=0, height=100)
        self.canvas.pack(fill="both", expand=True, padx=12, pady=(0, 8))

        self.topmost = tk.BooleanVar(value=False)
        tk.Checkbutton(
            root, text="Always on top", variable=self.topmost, command=self._toggle_top,
            bg=BG, fg=DIM, selectcolor=BG, activebackground=BG, activeforeground=FG,
            highlightthickness=0, bd=0,
        ).pack(pady=(0, 8))

        self.actions = queue.Queue()
        self.tray = Tray(self.actions)
        self._tray_only = False
        root.bind("<Unmap>", self._on_unmap)
        root.protocol("WM_DELETE_WINDOW", self._on_close)
        if self.tray.icon is not None and platform.system() == "Windows":
            # Start hidden and appear once the tray icon is up, already without a taskbar
            # button; if the tray doesn't come up in time, appear as a normal window.
            root.withdraw()
            root.after(TRAY_WAIT_MS, self._tray_timeout)
        self._poll_actions()

        reader = reader or make_reader()
        self.poller = Poller(reader) if reader else None
        if self.poller is None:
            self.status.config(text=f"Unsupported OS: {platform.system()}")
            return
        self.poller.start()
        self._refresh()

    def _toggle_top(self):
        self.root.attributes("-topmost", self.topmost.get())
        self.tray.topmost = self.topmost.get()
        self.tray.refresh_menu()

    # --- tray / window handling
    def _set_tray_only(self, on):
        """Tool windows get no taskbar button (and no Alt+Tab entry); the tray icon replaces it."""
        if on == self._tray_only:
            return
        self._tray_only = on
        visible = self.root.state() != "withdrawn"
        if visible:
            self.root.withdraw()  # the style change only reaches the taskbar on re-map
        self.root.attributes("-toolwindow", on)
        if visible:
            self._show()

    def _tray_ready(self):
        if self.tray.can_hide_window:
            self._set_tray_only(True)
            if self.root.state() == "withdrawn" and not self._shown_once:
                self._show()

    def _tray_timeout(self):
        if not self._shown_once:
            self._show()

    def _on_close(self):
        if self.tray.can_hide_window:
            self.root.withdraw()  # keep running in the tray; Exit is in the tray menu
        else:
            self._quit()

    def _toggle_window(self):
        if self.root.state() == "withdrawn":
            self._show()
        else:
            self.root.withdraw()

    def _on_unmap(self, event):
        if event.widget is self.root and self.tray.can_hide_window and self.root.state() == "iconic":
            self.root.after(10, self._hide_to_tray)

    def _hide_to_tray(self):
        if self.tray.can_hide_window and self.root.state() == "iconic":
            self.root.withdraw()  # hide from taskbar -> tray only

    def _show(self):
        self._shown_once = True
        self.root.deiconify()
        self.root.state("normal")
        self.root.lift()
        self.root.focus_force()

    def _quit(self):
        if self._quitting:
            return
        self._quitting = True
        if self.poller:
            self.poller.stop()
        self.tray.stop()
        self.root.destroy()

    def _poll_actions(self):
        try:
            while True:
                a = self.actions.get_nowait()
                if a == "show":
                    self._show()
                elif a == "toggle":
                    self._toggle_window()
                elif a == "tray_ready":
                    self._tray_ready()
                elif a == "top":
                    self.topmost.set(not self.topmost.get())
                    self._toggle_top()
                elif a == "tray_lost":  # back to a normal window with a taskbar button
                    self._set_tray_only(False)
                    if self.root.state() == "withdrawn":
                        self._show()
                elif a == "quit":
                    self._quit()
                    return
        except queue.Empty:
            pass
        self.root.after(150, self._poll_actions)

    def _refresh(self):
        if self._quitting:
            return
        attempt, good = self.poller.state
        v = describe(attempt, good, time.monotonic())
        self.big.config(text=v.big, fg=v.big_color)
        self.status.config(text=v.status, fg=v.status_color)
        self.detail.config(text=v.detail)
        self.tray.update(v.tray_watts, v.tray_color, v.tooltip)
        if attempt is not None and attempt.seq != self._last_seq:
            self._last_seq = attempt.seq
            self.history.append(attempt.reading["watts"] if attempt.reading else None)
        self._draw()
        self.root.after(500, self._refresh)

    def _draw(self):
        c = self.canvas
        c.delete("all")
        W, H = c.winfo_width(), c.winfo_height()
        vals = list(self.history)
        known = [v for v in vals if v is not None]
        if W < 10 or not known:
            return
        top = max(max(known), 5.0)
        bottom = min(min(known), 0.0)
        span = (top - bottom) or 1.0
        pad = 8

        def y(v):
            return pad + (top - v) / span * (H - 2 * pad)

        zero = y(0)
        c.create_line(0, zero, W, zero, fill="#3a3f4a", dash=(3, 3))
        c.create_text(4, pad, anchor="nw", text=f"{top:.0f} W", fill=DIM, font=("Helvetica", 9))
        step = W / max(HISTORY - 1, 1)
        x0 = W - step * (len(vals) - 1)
        color = GREEN if known[-1] >= 0 else ORANGE
        segment = []  # gaps (failed / unavailable samples) break the line
        for i, v in enumerate(vals + [None]):
            if v is not None:
                segment += [x0 + i * step, y(v)]
            elif segment:
                if len(segment) >= 4:
                    c.create_line(*segment, fill=color, width=2, smooth=True)
                else:
                    sx, sy = segment
                    c.create_oval(sx - 1.5, sy - 1.5, sx + 1.5, sy + 1.5, fill=color, outline="")
                segment = []
        if vals[-1] is not None:
            px, py = x0 + (len(vals) - 1) * step, y(vals[-1])
            c.create_oval(px - 3, py - 3, px + 3, py + 3, fill=FG, outline="")


if __name__ == "__main__":
    if sys.stderr is not None:  # pythonw / windowed exe have no console to log to
        logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    root = tk.Tk()
    App(root)
    root.mainloop()

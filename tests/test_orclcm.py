"""Regression tests for OrclCM.  Run:  python -m unittest discover -s tests"""
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
sys.path.insert(0, PROJECT)

import orclcm as cm  # noqa: E402


def bat(**attrs):
    return dict(type="Battery", **{k: str(v) for k, v in attrs.items()})


class LinuxParsing(unittest.TestCase):
    def test_missing_power_is_unavailable_not_zero(self):
        r = cm.parse_linux({"BAT0": bat(status="Discharging", capacity=50)})
        self.assertIsNone(r["watts"])
        self.assertEqual(r["flow"], "discharging")
        self.assertEqual(cm.format_watts(r["watts"]), "-- W")

    def test_current_times_voltage(self):
        r = cm.parse_linux({"BAT0": bat(status="Charging", current_now=2_000_000, voltage_now=12_000_000)})
        self.assertAlmostEqual(r["watts"], 24.0)

    def test_unknown_status_has_no_direction(self):
        r = cm.parse_linux({"BAT0": bat(status="Unknown", power_now=5_000_000)})
        self.assertIsNone(r["watts"])

    def test_full_status_without_power_is_idle(self):
        r = cm.parse_linux({"BAT0": bat(status="Full", capacity=100),
                            "AC": {"type": "Mains", "online": "1"}})
        self.assertEqual(r["watts"], 0.0)
        self.assertTrue(r["plugged"])

    def test_percentage_weighted_by_energy(self):
        # full 10 Wh pack + empty 90 Wh pack is 10 %, not the unweighted 50 %
        r = cm.parse_linux({
            "BAT0": bat(status="Full", energy_now=10e6, energy_full=10e6, capacity=100),
            "BAT1": bat(status="Discharging", power_now=1e6, energy_now=0, energy_full=90e6, capacity=0),
        })
        self.assertAlmostEqual(r["percent"], 10.0)

    def test_percentage_from_charge_and_design_voltage(self):
        r = cm.parse_linux({
            "BAT0": bat(status="Full", charge_now=1e6, charge_full=1e6, voltage_min_design=10e6),
            "BAT1": bat(status="Full", energy_now=0, energy_full=30e6),
        })
        self.assertAlmostEqual(r["percent"], 25.0)

    def test_unweightable_batteries_shown_individually(self):
        r = cm.parse_linux({"BAT0": bat(status="Full", capacity=100),
                            "BAT1": bat(status="Full", capacity=0)})
        self.assertIsNone(r["percent"])
        self.assertIn("100%", r["extra"])
        self.assertIn("0%", r["extra"])

    def test_removed_and_peripheral_batteries_ignored(self):
        r = cm.parse_linux({
            "BAT0": bat(status="Discharging", power_now=7e6, capacity=40),
            "BAT1": bat(present=0, status="Unknown"),
            "hid-mouse": bat(scope="Device", status="Discharging", capacity=5),
        })
        self.assertAlmostEqual(r["watts"], -7.0)
        self.assertEqual(r["percent"], 40)

    def test_no_battery(self):
        with self.assertRaises(RuntimeError):
            cm.parse_linux({"AC": {"type": "Mains", "online": "1"}})


def win_status(name, **kw):
    rec = dict(InstanceName=name, Charging=False, Discharging=False, PowerOnline=True,
               ChargeRate=0, DischargeRate=0, RemainingCapacity=0)
    rec.update(kw)
    return rec


class WindowsParsing(unittest.TestCase):
    def test_single_battery(self):
        r = cm.parse_windows({
            "status": [win_status("A", Discharging=True, PowerOnline=False, DischargeRate=30559,
                                  RemainingCapacity=26580)],
            "full": [{"InstanceName": "A", "FullChargedCapacity": 51260}],
        })
        self.assertAlmostEqual(r["watts"], -30.559)
        self.assertAlmostEqual(r["percent"], 26580 / 51260 * 100)
        self.assertFalse(r["plugged"])

    def test_records_matched_by_identity_and_aggregated(self):
        r = cm.parse_windows({
            "status": [win_status("A", Charging=True, ChargeRate=20000, RemainingCapacity=10000),
                       win_status("B", Charging=True, ChargeRate=5000, RemainingCapacity=80000)],
            # listed in the opposite order on purpose
            "full": [{"InstanceName": "B", "FullChargedCapacity": 90000},
                     {"InstanceName": "A", "FullChargedCapacity": 10000}],
        })
        self.assertAlmostEqual(r["watts"], 25.0)
        self.assertAlmostEqual(r["percent"], 90.0)
        self.assertIn("2 batteries", r["extra"])

    def test_unmatched_capacity_gives_no_percentage(self):
        r = cm.parse_windows({
            "status": [win_status("A", RemainingCapacity=100)],
            "full": [{"InstanceName": "other", "FullChargedCapacity": 50000}],
        })
        self.assertIsNone(r["percent"])

    def test_zero_percent_is_kept(self):
        r = cm.parse_windows({
            "status": [win_status("A", RemainingCapacity=0)],
            "full": [{"InstanceName": "A", "FullChargedCapacity": 50000}],
        })
        self.assertEqual(r["percent"], 0.0)

    def test_unknown_rate_is_unavailable(self):
        r = cm.parse_windows({"status": [win_status("A", Discharging=True, DischargeRate=-2147483648)]})
        self.assertIsNone(r["watts"])
        self.assertEqual(r["flow"], "discharging")

    def test_charging_flag_without_rate_is_unavailable(self):
        r = cm.parse_windows({"status": [win_status("A", Charging=True, ChargeRate=0)]})
        self.assertIsNone(r["watts"])
        self.assertEqual(cm.classify(r)[1], "Charging · rate unavailable")

    def test_idle_with_zero_rates_is_zero(self):
        r = cm.parse_windows({"status": [win_status("A")]})
        self.assertEqual(r["watts"], 0.0)

    def test_relative_units_flag(self):
        r = cm.parse_windows({
            "status": [win_status("A", Charging=True, ChargeRate=12, RemainingCapacity=40)],
            "full": [{"InstanceName": "A", "FullChargedCapacity": 80}],
            "static": [{"InstanceName": "A", "Capabilities": cm.BATTERY_CAPACITY_RELATIVE | 0x80000000}],
        })
        self.assertIsNone(r["watts"])
        self.assertAlmostEqual(r["percent"], 50.0)  # a ratio is still meaningful

    def test_relative_units_heuristic_without_static_data(self):
        r = cm.parse_windows({
            "status": [win_status("A", Charging=True, ChargeRate=12, RemainingCapacity=40)],
            "full": [{"InstanceName": "A", "FullChargedCapacity": 100}],
        })
        self.assertIsNone(r["watts"])

    def test_unknown_capacity_value(self):
        r = cm.parse_windows({
            "status": [win_status("A", RemainingCapacity=0xFFFFFFFF)],
            "full": [{"InstanceName": "A", "FullChargedCapacity": 50000}],
        })
        self.assertIsNone(r["percent"])

    def test_powershell_51_shapes(self):
        r = cm.parse_windows({
            "status": win_status("A", RemainingCapacity=500),  # single object, not a list
            "full": {"value": [{"InstanceName": "A", "FullChargedCapacity": 1000}], "Count": 1},
        })
        self.assertAlmostEqual(r["percent"], 50.0)

    def test_no_battery_and_query_error(self):
        with self.assertRaises(RuntimeError):
            cm.parse_windows({"status": [], "full": []})
        with self.assertRaisesRegex(RuntimeError, "WMI broke"):
            cm.parse_windows({"error": "WMI broke"})


class MacParsing(unittest.TestCase):
    def test_signed_amperage_and_adapter(self):
        r = cm.parse_macos([{
            "Voltage": 12000, "InstantAmperage": 2**64 - 1500, "ExternalConnected": True,
            "AppleRawCurrentCapacity": 3000, "AppleRawMaxCapacity": 6000,
            "PowerTelemetryData": {"SystemPowerIn": 45000}, "AdapterDetails": {"Watts": 67},
        }])
        self.assertAlmostEqual(r["watts"], -18.0)
        self.assertAlmostEqual(r["percent"], 50.0)
        self.assertIn("Adapter input: 45.0 W", r["extra"])
        self.assertIn("Charger rated 67 W", r["extra"])
        self.assertEqual(cm.classify(r)[1], "Plugged in · discharging")

    def test_missing_voltage_or_bad_types_are_unavailable(self):
        r = cm.parse_macos([{"InstantAmperage": 1000, "ExternalConnected": True,
                             "PowerTelemetryData": "junk", "AdapterDetails": {"Watts": "junk"}}])
        self.assertIsNone(r["watts"])
        self.assertEqual(r["extra"], "")


def reading(watts, plugged=True, flow=None, percent=50.0):
    return cm.make_reading(watts, percent, plugged, flow)


class Classification(unittest.TestCase):
    def test_discharging_while_plugged_in_is_not_hidden(self):
        color, label = cm.classify(reading(-8.0, plugged=True))
        self.assertEqual(label, "Plugged in · discharging")
        self.assertEqual(color, cm.ORANGE)

    def test_states(self):
        self.assertEqual(cm.classify(reading(12.0))[1], "Charging")
        self.assertEqual(cm.classify(reading(0.1, plugged=True))[1], "Plugged in · not charging")
        self.assertEqual(cm.classify(reading(-5.0, plugged=False))[1], "On battery")
        self.assertEqual(cm.classify(reading(None, plugged=True))[1], "Plugged in · rate unavailable")
        self.assertEqual(cm.classify(reading(None, plugged=None, flow="discharging"))[1],
                         "Discharging · rate unavailable")


def snap(seq, t, r=None, err=None):
    return cm.Snapshot(seq, t, r, err)


class StaleDisplay(unittest.TestCase):
    def test_fresh(self):
        good = snap(1, 100.0, reading(10.0))
        v = cm.describe(good, good, 101.0)
        self.assertEqual(v.big, "+10.0 W")
        self.assertEqual(v.status, "Charging")
        self.assertEqual(v.tray_watts, 10.0)

    def test_failed_read_after_success_is_marked_stale(self):
        good = snap(1, 100.0, reading(10.0))
        v = cm.describe(snap(2, 102.0, err="Battery removed"), good, 102.5)
        self.assertTrue(v.status.startswith("Stale"))
        self.assertIn("Battery removed", v.detail)
        self.assertEqual(v.big_color, cm.STALE)
        self.assertIsNone(v.tray_watts)  # tray shows "--" instead of the old value

    def test_hung_reader_is_marked_stale(self):
        good = snap(1, 100.0, reading(10.0))
        v = cm.describe(good, good, 100.0 + cm.STALE_AFTER + 1)
        self.assertTrue(v.status.startswith("Stale"))

    def test_error_before_first_success(self):
        v = cm.describe(snap(1, 1.0, err="No battery found"), None, 1.0)
        self.assertEqual(v.status, "No battery found")
        self.assertEqual(v.big, "--")


class FlakyReader:
    def __init__(self, results):
        self.results = list(results)
        self.closed = threading.Event()

    def read(self):
        r = self.results.pop(0) if self.results else RuntimeError("gone")
        if isinstance(r, Exception):
            raise r
        return r

    def close(self):
        self.closed.set()


class Polling(unittest.TestCase):
    def test_failure_is_published_and_last_good_kept(self):
        good = reading(5.0)
        p = cm.Poller(FlakyReader([good, RuntimeError("timeout")]))
        p.poll_once()
        attempt, last_good = p.state
        self.assertIs(attempt.reading, good)
        p.poll_once()
        attempt, last_good = p.state
        self.assertIsNone(attempt.reading)
        self.assertEqual(attempt.error, "timeout")
        self.assertIs(last_good.reading, good)
        self.assertEqual(attempt.seq, 2)

    def test_empty_exception_message_still_reported(self):
        p = cm.Poller(FlakyReader([TimeoutError()]))
        self.assertEqual(p.poll_once().error, "TimeoutError")

    def test_stop_closes_reader_and_joins(self):
        reader = FlakyReader([reading(1.0)] * 1000)
        p = cm.Poller(reader, interval=0.01)
        p.start()
        time.sleep(0.05)
        p.stop()
        self.assertTrue(reader.closed.is_set())
        self.assertFalse(p._thread.is_alive())


@unittest.skipUnless(sys.platform == "win32", "Windows batch build script")
class BuildScript(unittest.TestCase):
    """Runs build_exe.bat in a scratch copy with a real (pip-less) venv whose `pip` and
    `PyInstaller` modules are fakes, so failures can be simulated offline."""

    FAKE_PIP = "import os, sys\nsys.exit(1 if os.environ.get('FAKE_PIP') == 'fail' else 0)\n"
    FAKE_PYINSTALLER = (
        "import os, sys\n"
        "mode = os.environ.get('FAKE_PYINSTALLER', 'ok')\n"
        "if mode == 'fail': sys.exit(1)\n"
        "if mode == 'ok':\n"
        "    name = sys.argv[sys.argv.index('--name') + 1]\n"
        "    os.makedirs('dist', exist_ok=True)\n"
        "    open(os.path.join('dist', name + '.exe'), 'wb').write(b'new')\n"
    )

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="cm-build-")
        cls.venv = os.path.join(cls.tmp, "venv")
        subprocess.run([sys.executable, "-m", "venv", "--without-pip", cls.venv], check=True)
        site = os.path.join(cls.venv, "Lib", "site-packages")
        for pkg, code in (("pip", cls.FAKE_PIP), ("PyInstaller", cls.FAKE_PYINSTALLER)):
            os.makedirs(os.path.join(site, pkg))
            open(os.path.join(site, pkg, "__init__.py"), "w").close()
            with open(os.path.join(site, pkg, "__main__.py"), "w") as f:
                f.write(code)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def setUp(self):
        self.proj = tempfile.mkdtemp(dir=self.tmp)
        shutil.copy(os.path.join(PROJECT, "build_exe.bat"), self.proj)
        for name in ("requirements-build.txt", "build_version.py", "orclcm.py"):
            shutil.copy(os.path.join(PROJECT, name), self.proj)
        self.out = os.path.join(self.proj, "dist", f"OrclCM-{cm.VERSION}.exe")

    def build(self, venv=None, **env):
        e = dict(os.environ, NOPAUSE="1", BUILD_VENV=venv or self.venv, **env)
        p = subprocess.run(["cmd", "/c", os.path.join(self.proj, "build_exe.bat")], env=e,
                           stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120)
        return p.returncode, p.stdout + p.stderr

    def stale_exe(self):
        os.makedirs(os.path.dirname(self.out))
        with open(self.out, "wb") as f:
            f.write(b"old")

    def assertFailed(self, code, out):
        self.assertNotEqual(code, 0, out)
        self.assertIn("Build FAILED", out)
        self.assertNotIn("Done:", out)

    def test_success(self):
        code, out = self.build()
        self.assertEqual(code, 0, out)
        self.assertIn("Done:", out)
        self.assertTrue(os.path.exists(self.out), out)
        with open(os.path.join(self.proj, "build", "version_info.txt")) as f:
            self.assertIn(f"'FileVersion','{cm.VERSION}'", f.read())

    def test_dependency_install_failure_stops(self):
        self.assertFailed(*self.build(FAKE_PIP="fail"))

    def test_packaging_failure_stops_and_removes_old_exe(self):
        self.stale_exe()
        self.assertFailed(*self.build(FAKE_PYINSTALLER="fail"))
        self.assertFalse(os.path.exists(self.out))

    def test_missing_output_is_failure(self):
        self.stale_exe()
        self.assertFailed(*self.build(FAKE_PYINSTALLER="noop"))
        self.assertFalse(os.path.exists(self.out))

    def test_missing_python_stops(self):
        missing = os.path.join(self.proj, "no-venv")
        self.assertFailed(*self.build(venv=missing, PYTHON="python-does-not-exist-xyz"))


class Version(unittest.TestCase):
    def test_format(self):
        self.assertRegex(cm.VERSION, r"^1\.\d\.\d{3}$")


class FakeTray:
    """Stands in for a Windows tray icon that has come up."""
    icon = object()
    can_hide_window = available = True

    def update(self, *a):
        pass

    def refresh_menu(self):
        pass

    def stop(self):
        pass


@unittest.skipUnless(sys.platform == "win32", "tray-only mode is Windows-specific")
class TrayOnlyWindow(unittest.TestCase):
    def setUp(self):
        try:
            self.root = cm.tk.Tk()
        except cm.tk.TclError:
            self.skipTest("no display")
        self.app = cm.App(self.root, reader=FlakyReader([reading(5.0)] * 100))
        self.app.tray.stop()
        self.app.tray = FakeTray()

    def tearDown(self):
        self.app._quit()

    def test_tray_ready_removes_taskbar_button_and_shows_window(self):
        self.app.actions.put("tray_ready")
        self.app._poll_actions()
        self.root.update()
        self.assertTrue(self.root.attributes("-toolwindow"))
        self.assertNotEqual(self.root.state(), "withdrawn")
        self.assertIn(cm.VERSION, self.root.title())

    def test_close_hides_to_tray_instead_of_quitting(self):
        self.app._tray_ready()
        self.app._on_close()
        self.assertEqual(self.root.state(), "withdrawn")
        self.assertFalse(self.app._quitting)
        self.app._toggle_window()
        self.assertNotEqual(self.root.state(), "withdrawn")

    def test_tray_lost_restores_normal_window(self):
        self.app._tray_ready()
        self.root.withdraw()
        self.app.tray.can_hide_window = False
        self.app.actions.put("tray_lost")
        self.app._poll_actions()
        self.assertFalse(self.root.attributes("-toolwindow"))
        self.assertNotEqual(self.root.state(), "withdrawn")


class GuiSmoke(unittest.TestCase):
    def test_window_marks_stale_after_failure(self):
        try:
            root = cm.tk.Tk()
        except cm.tk.TclError:
            self.skipTest("no display")
        root.withdraw()
        reader = FlakyReader([reading(-9.5, plugged=True), RuntimeError("Battery removed")])
        app = cm.App(root, reader=reader)
        app.tray.stop()
        app.poller.stop_event.set()  # drive polling by hand from here on
        app.poller._thread.join(3)
        try:
            if app.poller.state[0] is None:
                app.poller.poll_once()
            app._refresh()
            self.assertEqual(app.big.cget("text"), "-9.5 W")
            self.assertEqual(app.status.cget("text"), "Plugged in · discharging")
            app.poller.poll_once()
            app._refresh()
            self.assertTrue(app.status.cget("text").startswith("Stale"))
            self.assertIn("Battery removed", app.detail.cget("text"))
            self.assertEqual(list(app.history)[-1], None)
        finally:
            app._quit()
        self.assertTrue(reader.closed.is_set())


if __name__ == "__main__":
    unittest.main()

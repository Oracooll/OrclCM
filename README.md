# OrclCM

A tiny cross-platform desktop app that shows, live, how many **watts your laptop battery is charging (or draining) at**, with a rolling history graph.

- Windows, macOS and Linux
- Pure Python standard library (tkinter) — no dependencies
- Refreshes every 2 seconds, keeps ~4 minutes of history
- Green = charging, orange = discharging (on battery, or plugged in but still draining), grey = plugged in but not charging
- Values the OS doesn't report are shown as `--` / "rate unavailable", never as a fake 0 W
- If a reading fails or stops updating, the display is marked **Stale** with the time of the last good reading and the error, and the tray icon switches to `--`
- "Always on top" toggle
- **Tray-only on Windows:** the app has no taskbar button — it lives in the system tray, where the icon shows the live wattage in small digits. Click the icon to show / hide the window, hover for details, right-click for *Show / hide window / Always on top / Exit*. Closing the window (X) hides it to the tray; quit with **Exit** in the tray menu. If the tray icon is unavailable (pystray not installed) or disappears, the window falls back to a normal taskbar window so it can't get lost. On Linux the tray icon is shown but the window keeps its taskbar entry; macOS has no tray icon
- On Macs that report it, also shows the adapter input power and the charger's rated wattage

## Download (Windows, portable)

Grab **`OrclCM.exe`** from the [Releases](../../releases) page and run it — no install, no Python needed. Put it anywhere (USB stick, OneDrive, Desktop).

> Windows SmartScreen may warn about an unsigned app the first time: click **More info → Run anyway**.

## Run from source

### Requirements

Python 3.8+ with tkinter (included in the standard python.org installers).
For the tray icon: `pip install pystray pillow` (optional - without them the app runs without tray support).

### Usage

```bash
python orclcm.py
```

**Windows:** double-click `run.bat`, or `run_silent.pyw` to start it without a console window.

## Build the exe yourself

On Windows with Python installed, double-click `build_exe.bat`. It creates an isolated build environment (`.build-venv`) with the exact versions in `requirements-build.txt` — your global Python packages are not touched — and stops with **Build FAILED** at the first step that fails. The portable exe lands in `dist\OrclCM.exe`. The version — `VERSION` in `orclcm.py`, format `1.X.XXX`, bump it there before a release — is shown in the window title bar (e.g. *OrclCM 1.2.000*) and in the exe's file properties; any previous exe there is deleted first, so a failed build never leaves an old exe looking new.

## Tests

```bash
python -m unittest discover -s tests
```

## How it reads the data

| OS | Source |
|----|--------|
| Windows | WMI `root\wmi` → `BatteryStatus` + `BatteryFullChargedCapacity`, matched per battery by `InstanceName` and summed, via one long-running PowerShell helper |
| macOS | `ioreg -c AppleSmartBattery` (Voltage × InstantAmperage, PowerTelemetryData) |
| Linux | `/sys/class/power_supply/*/power_now` (or `current_now × voltage_now`); multi-battery percentage weighted by `energy_*` / `charge_*` capacity |

## Notes

- The value is the **battery's** charge rate, not the charger's total output — the laptop itself also consumes power while charging. Expect the number to taper toward 0 W as the battery approaches full.
- Some Windows laptops' firmware doesn't report `ChargeRate`, or reports it in relative units instead of mW; the app then shows the charging state with "rate unavailable" instead of a wattage.
- Problems reading the battery are logged to the console when run with `python orclcm.py`.

## License

MIT — see [LICENSE](LICENSE). OrclCM is based on the MIT-licensed *Charge Meter*.

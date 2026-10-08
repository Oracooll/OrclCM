# OrclCM

A tiny native Windows tray app that shows, live, how many **watts your laptop battery is charging (or draining) at**, with a rolling history graph.

- **~40 KB portable exe**, nothing to install — runs on the .NET Framework 4.8 built into Windows 10 and 11
- **Tray-only:** no taskbar button. The tray icon shows the live wattage in small digits. Click it to show / hide the window, hover for details, right-click for *Show / hide window / Always on top / Exit*. Closing or minimizing the window hides it to the tray; quit with **Exit**
- Starting OrclCM again while it's running just brings up its window
- Refreshes every 2 seconds, keeps ~4 minutes of history
- Green = charging, orange = discharging (on battery, *or plugged in but still draining*), grey = plugged in but not charging
- Values Windows doesn't report are shown as `--` / "rate unavailable", never as a fake 0 W
- If a reading fails or stops updating, the display is marked **Stale** with the time of the last good reading and the error, and the tray icon switches to `--`
- Laptops with several batteries: rates are summed and the percentage is weighted by capacity
- The version is shown in the window title bar

## Download

Grab **`OrclCM.exe`** from the [Releases](../../releases) page and run it. Put it anywhere (USB stick, OneDrive, Desktop).

> Windows SmartScreen may warn about an unsigned app the first time: click **More info → Run anyway**.

## How it reads the data

OrclCM asks the Windows battery driver directly (SetupAPI + `IOCTL_BATTERY_QUERY_STATUS` / `IOCTL_BATTERY_QUERY_INFORMATION`) — the same source Windows' own battery meter uses. No admin rights, no WMI, no background processes. AC status comes from `GetSystemPowerStatus`.

The value is the **battery's** charge rate, not the charger's total output — the laptop itself also consumes power while charging. Expect it to taper toward 0 W as the battery approaches full. If firmware reports the rate in relative units instead of milliwatts, OrclCM shows the charging state with "rate unavailable".

## Build it yourself

Needs the C# compiler from Visual Studio or the free *Build Tools for Visual Studio* (workload: .NET desktop build tools). Double-click `build.bat`: it compiles and runs the tests first, then builds `dist\OrclCM.exe`, and stops with **Build FAILED** at the first problem.

The version lives in `src\AppInfo.cs` (`Version`, format `1.X.XXX`, plus the matching `FileVersion`); bump both before a release.

| Path | What |
|------|------|
| `src\Core.cs` | measurement model, battery aggregation, display logic, polling |
| `src\NativeBattery.cs` | Windows battery driver access |
| `src\MainForm.cs` | window, graph and tray icon |
| `src\TrayIconRenderer.cs` | draws the wattage digits on the tray icon |
| `tests\CoreTests.cs` | regression tests (run by `build.bat`) |

Earlier versions (up to 1.2.000) were written in Python; they are in the git history.

## License

MIT — see [LICENSE](LICENSE). OrclCM is based on the MIT-licensed *Charge Meter*.

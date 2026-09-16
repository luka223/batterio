# Batterio

Batterio is a Windows 11 x64 .NET 10 / .NET MAUI tray battery monitor. The app ships with Logitech G Pro X2 Superstrike and Logitech G Pro X2 Lightspeed devices. Their adapters query the paired mouse/headset through the receiver and publish one of three states: `Charging`, `NotCharging`, or `Offline`, with an optional percentage for the first two.

## Build and run

The repository pins the .NET SDK in `global.json` and uses the installed .NET MAUI Windows workload.

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
dotnet build Batterio.sln -c Release --no-restore
dotnet test tests/Batterio.Core.Tests/Batterio.Core.Tests.csproj -c Release --no-restore
dotnet test tests/Batterio.Windows.Tests/Batterio.Windows.Tests.csproj -c Release
dotnet run --project src/Batterio.App/Batterio.App.csproj -c Debug
```

The application runs entirely from the system tray, with no separate main window or taskbar/Alt+Tab entry. Single-click the icon to toggle the compact popup, double-click to show it, and right-click for the application menu. Clicking outside the popup hides it; choose Settings to manage startup and connected-device visibility, or Close to stop the process.

## Devices and refresh

Devices are defined in code in `src/Batterio.Core/Adapters/BuiltInDevices.cs`. A device is connected when its supported receiver HID collection is discovered; discovery never sends battery requests. Batterio scans at startup, when Settings or the popup opens, after resume, and every five seconds while running. The app does not create or load device configuration files.

Device visibility choices are stored at `%LocalAppData%\Batterio\settings.json`. Missing device IDs are selected by default, and choices for disconnected receivers are retained. Settings checkbox changes are saved immediately; there is no Apply button. Only selected, currently discovered receivers are polled, and removing a selection immediately removes its popup row and stops new queries.

It polls each active device at its configured interval (five minutes for the built-in devices). The popup refresh icon polls active devices immediately. Polling continues while the popup is hidden.

When a successful device reading is below 30%, Batterio sends one native Windows notification for that device during the current application run: `{Device name} battery level is below 30%`. Notifications are per device ID, so refreshes, reconnects, and a recovery followed by another drop do not repeat the alert until Batterio restarts. Windows notification permissions and Do Not Disturb control whether Windows presents the submitted notification. Clicking it opens the existing Batterio popup.

The popup sizes itself to the device content and grows with the device list up to a scrollable maximum height. Each device is shown on one row with a 52px photo, its name, a compact battery icon, and an optional percentage. Battery colors are green above 40%, yellow from 20% through 40%, and red below 20%. `Charging` uses a full green silhouette with a centered bolt; `NotCharging` uses ten segments; and `Offline` uses an empty gray battery and gray device presentation. An Offline result clears any prior percentage immediately, including while retries are in progress.

## Diagnostics

Logs are written to `%LocalAppData%\Batterio\Logs\batterio.log` with up to two rotated files, each bounded to approximately 512 KiB. Logs include device and adapter IDs where relevant and never serialize complete adapter settings. Set `BATTERIO_TRACE_HID=1` before launching Batterio to opt into hexadecimal HID TX/RX packet traces in the same bounded log. Leave it unset for routine use.

“Start automatically when Windows starts” controls the current-user `Software\Microsoft\Windows\CurrentVersion\Run` value named `Batterio`. Windows runs that entry at user sign-in and may delay it; Startup Apps policy remains authoritative. The registration uses the published executable path and does not use the single-file extraction directory.

## Adding an adapter

1. Implement `IBatteryAdapterFactory` with a stable `AdapterId`.
2. Use `ValidateSettings` only if your adapter needs settings supplied in code.
3. Create one adapter instance for each `DeviceConfiguration` in `CreateAdapter`.
4. Implement `IBatteryAdapter.GetBatteryStatus`, returning a `BatteryReading` with an optional percentage and one of the three `BatteryChargeState` values: `Charging`, `NotCharging`, or `Offline`. Offline readings must not contain a percentage.
5. Honor cancellation, release transport resources in `DisposeAsync`, and add contract tests for success, errors, timeout, cancellation, invalid values, and disposal.
6. Register the factory explicitly in `MauiProgram` and add its device to `BuiltInDevices.Create()`. No JSON file is needed.

Adapters must use cooperative cancellation. If a device ignores cancellation, Batterio marks the current reading Offline immediately but retains the adapter call's concurrency slot until it actually finishes; late results are discarded when the active device list is replaced. The built-in Logitech adapters accept an optional nonempty `devicePath` setting when more than one matching receiver collection is present. An explicit missing path is never replaced with another collection.

## Publish

The intended delivery is one unpackaged, self-contained `win-x64` executable. The Release defaults in `Batterio.App.csproj` bundle both the .NET runtime and Windows App Runtime 2.4.0; the native Windows App SDK components are extracted locally when the executable starts.

```powershell
dotnet publish src/Batterio.App/Batterio.App.csproj -c Release -o publish
```

Copy `publish/Batterio.App.exe` to a Windows 11 x64 machine. No .NET SDK, separately installed .NET runtime, or separately installed Windows App Runtime is required. Republish Batterio when the bundled Windows App SDK runtime needs to be updated.

For troubleshooting runtime activation separately from single-file extraction, publish the same self-contained application as a folder:

```powershell
dotnet publish src/Batterio.App/Batterio.App.csproj -c Release -p:PublishSingleFile=false -o artifacts/publish-win-x64-folder
```

The executable uses `src/Batterio.App/Resources/AppIcon/batterio.ico` as its icon. Keep the complete folder together when using the troubleshooting publish; the distributed single-file deliverable only requires `Batterio.App.exe`.

On notification initialization, Batterio creates or repairs its current-user Start Menu shortcut with `com.batterio.tray` and a fixed toast activator CLSID. Its COM activation registration points to the distributed executable, and notification clicks wait until the popup is ready. These registrations intentionally remain after exit. When permanently removing the portable app, remove `%AppData%\Microsoft\Windows\Start Menu\Programs\Batterio.lnk` and `HKCU\Software\Classes\CLSID\{B68D18D7-6B67-4C24-993C-B597D789E895}`. An obsolete `HKCU\Software\Classes\batterio` key from earlier builds can also be removed.

The previous `0xC000027B` startup failure did not reproduce in the 2026-09-10 notification-review smoke run: the fresh single-file executable registered its COM activator and began battery polling. Both output formats published successfully. Automated tests cover notification policy, payload escaping, registration repair and cleanup; the separate Windows suite exercises actual COM activation and shortcut property persistence. Clean-machine Windows 11 acceptance is still outstanding, including notification settings and Do Not Disturb behavior.

Local acceptance confirmed live COM activation for each published format without a duplicate process, and the single-file popup was visually inspected. The scripted adapter submitted exactly one native toast after repeated 29% readings; Windows notification history retained the exact message after the sender and app exited. Direct cold COM activation started the installed `D:\Batterio` copy and displayed its popup. Cold activation did not select the review executable despite the registration observed by the probe, so relocation through cold COM activation is not yet qualified. Actual banner-click behavior and Do Not Disturb still require manual acceptance; a successful COM probe is not a substitute for those checks. The final automated run passed 167 Core tests and 2 native Windows tests, with 3 opt-in hardware tests skipped; the Release build had no warnings or errors.

For manual notification acceptance, run the published app once to register its executable, then use the test-only probe. It preserves the app's registration and uses a scripted 29% adapter without adding a production simulation setting:

```powershell
dotnet run --project tests/Batterio.NotificationProbe -c Release -- notify
dotnet run --project tests/Batterio.NotificationProbe -c Release -- activate
dotnet run --project tests/Batterio.NotificationProbe -c Release -- history
```

`notify` submits one native test toast despite repeated low readings. Inspect the exact text, Batterio identity, Notification Center persistence, and click behavior while the app runs and after exiting it. `activate` directly exercises the COM callback and requests cold activation when needed; verify the process path as well as the return result. `history` reads back only the probe's test notifications from Windows notification history. Use one Batterio copy at a time during acceptance, and repeat on a clean Windows 11 machine without a separately installed Windows App Runtime.

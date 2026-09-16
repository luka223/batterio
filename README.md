# Batterio

Batterio is a Windows 11 x64 .NET 10 / .NET MAUI tray battery monitor. The app ships with Logitech G Pro X2 Superstrike and Logitech G Pro X2 Lightspeed devices. Their adapters query the paired mouse/headset through the receiver and publish one of three states: `Charging`, `NotCharging`, or `Offline`, with an optional percentage for the first two.

## Build and run

The repository pins the .NET SDK in `global.json` and uses the installed .NET MAUI Windows workload.

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
dotnet build Batterio.sln -c Release --no-restore
dotnet test Batterio.sln -c Release --no-build
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

“Start automatically when Windows starts” controls the current-user `Software\Microsoft\Windows\CurrentVersion\Run` value named `Batterio`. The entry points to `Batterio.App.exe` in the installed folder. Windows runs that entry at user sign-in and may delay it; Startup Apps policy remains authoritative.

## Adding an adapter

1. Implement `IBatteryAdapterFactory` with a stable `AdapterId`.
2. Use `ValidateSettings` only if your adapter needs settings supplied in code.
3. Create one adapter instance for each `DeviceConfiguration` in `CreateAdapter`.
4. Implement `IBatteryAdapter.GetBatteryStatus`, returning a `BatteryReading` with an optional percentage and one of the three `BatteryChargeState` values: `Charging`, `NotCharging`, or `Offline`. Offline readings must not contain a percentage.
5. Honor cancellation, release transport resources in `DisposeAsync`, and add contract tests for success, errors, timeout, cancellation, invalid values, and disposal.
6. Register the factory explicitly in `MauiProgram` and add its device to `BuiltInDevices.Create()`. No JSON file is needed.

Adapters must use cooperative cancellation. If a device ignores cancellation, Batterio marks the current reading Offline immediately but retains the adapter call's concurrency slot until it actually finishes; late results are discarded when the active device list is replaced. The built-in Logitech adapters accept an optional nonempty `devicePath` setting when more than one matching receiver collection is present. An explicit missing path is never replaced with another collection.

## Publish

The supported delivery is one unpackaged, self-contained `win-x64` folder. It contains the public NativeAOT launcher `Batterio.App.exe`, the private WinUI process `Batterio.Runtime.exe`, the Windows App SDK 2.4.0 files, and the app icon. Deploy the complete folder; the launcher and runtime are designed to stay together.

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
.\eng\Publish-Batterio.ps1
& .\publish\Batterio.App.exe
```

The publish script produces the verified Release build and atomically replaces the `publish` folder. The last command runs its public launcher. To install or copy the application elsewhere, copy the complete `publish` folder and run `Batterio.App.exe` from that folder; do not run `Batterio.Runtime.exe` directly or copy either executable by itself.

`Batterio.App.exe` is the stable entry point for Start, the Run key, and cold notification activation. It starts `Batterio.Runtime.exe` in the installation folder, passes through activation arguments, and waits for the runtime readiness event. The runtime arbitrates the existing `Batterio.SingleInstance` identity before starting WinUI. Warm notification activations are redirected to the existing runtime; cold activations start the launcher and then the runtime.

The launcher makes at most five runtime attempts, after delays of 0, 1, 3, 10, and 30 seconds. An attempt succeeds when runtime startup signals readiness after the tray is available, notifications have initialized or safely degraded, and selection and polling have started. Each attempt has 60 seconds to signal readiness. A runtime that exits unsuccessfully at readiness or remains alive without becoming ready is recorded as a failed attempt; a timed-out child process tree is terminated before the launcher starts the next attempt. The launcher also waits up to 60 seconds for the Windows shell before launching anyway.

The tray keeps retrying Explorer-dependent icon registration indefinitely. Retry delays are 1, 2, 5, and 10 seconds, then 30 seconds for subsequent attempts. When Explorer restarts, Batterio removes stale tray state and attempts to add the icon immediately.

The publish script builds both executables in a staging directory, verifies their names and sizes, checks the NativeAOT launcher imports, rejects an unexpected launcher runtime configuration file, verifies the icon and Windows App SDK files, and replaces `publish` only after the staged bundle passes. Its verification summary prints product versions, sizes, and SHA-256 hashes for both executables. Build prerequisites are the .NET 10 SDK and Visual Studio C++ x64 build tools; the distributed folder does not require a separate .NET or Windows App Runtime installation.

The launcher writes bounded startup diagnostics to `%LocalAppData%\Batterio\Logs\startup.log`. Records include its product version, attempt, child PID, exit code, readiness result, and final failure, without storing user arguments or event names.

On notification initialization, Batterio creates or repairs the current-user Start Menu shortcut with App ID `com.batterio.tray` and the existing toast activator CLSID. Its COM activation command targets `Batterio.App.exe` with `-ToastActivated`; notification clicks wait until the popup dispatcher is ready. The Run value name, App ID, tray GUID, and toast CLSID remain compatible with existing installations. These registrations intentionally remain after exit. When permanently removing the portable app, remove `%AppData%\Microsoft\Windows\Start Menu\Programs\Batterio.lnk` and `HKCU\Software\Classes\CLSID\{B68D18D7-6B67-4C24-993C-B597D789E895}`.

Run automated tests with `dotnet test Batterio.sln -c Debug` and `dotnet test Batterio.sln -c Release`. Tests that communicate with real HID hardware remain opt-in. Destructive launcher, Explorer, sign-in, reboot, and Run-key upgrade scenarios belong in a disposable Windows 11 x64 VM. `tests/Batterio.StartupProbe` provides a self-contained runtime substitute for those launcher-attempt scenarios; it is excluded from the production publish.

The `publish` folder should be tested as a complete installation before distribution. The current Windows acceptance matrix and commands are recorded in [CHANGELOG.md](CHANGELOG.md).

## Startup acceptance probe

`tests/Batterio.StartupProbe` is a test-only NativeAOT process for launcher retry scenarios. Build it on a Windows build machine:

```powershell
$probePublishFolder = Join-Path $env:TEMP 'Batterio.StartupProbe'
dotnet publish tests/Batterio.StartupProbe/Batterio.StartupProbe.csproj -c Release -r win-x64 --self-contained true -o $probePublishFolder
```

Copy the verified `publish` folder and `Batterio.StartupProbe.exe` into a disposable Windows 11 x64 VM. In that VM, replace only the copied runtime apphost and run the public launcher:

```powershell
$acceptanceFolder = Join-Path $env:TEMP 'Batterio.Acceptance'
$probeExecutable = Join-Path $env:TEMP 'Batterio.StartupProbe.exe'
$attemptStateFile = Join-Path $env:TEMP 'Batterio.StartupProbe.attempts'
Copy-Item -LiteralPath $probeExecutable -Destination (Join-Path $acceptanceFolder 'Batterio.Runtime.exe') -Force
& (Join-Path $acceptanceFolder 'Batterio.App.exe') --state-file $attemptStateFile --fail-before-ready 4
```

The probe records its launch count in the supplied state file, exits before readiness for the configured number of launches, and then signals readiness and stays alive. Use a fresh state file for each scenario. Set `--fail-before-ready 5` to verify five failures produce no sixth launch. Add `--ready-then-exit-ms 1000` to signal readiness and exit normally after one second. The probe does not implement the app's single-instance or tray behavior, so use the real runtime for those acceptance scenarios. Never replace the runtime in a normal installation with this probe.

## Notification developer probe

For manual toast checks on a Windows developer machine, run the published app once to create its current-user activation registration, then run:

```powershell
dotnet run --project tests/Batterio.NotificationProbe -c Release -- notify
dotnet run --project tests/Batterio.NotificationProbe -c Release -- activate
dotnet run --project tests/Batterio.NotificationProbe -c Release -- history
```

`notify` submits one native test toast despite repeated low readings. Inspect the exact text, Batterio identity, Notification Center persistence, and click behavior while the app runs and after exiting it. `activate` directly exercises the COM callback and requests cold activation when needed; verify the process path as well as the return result. `history` reads back only the probe's test notifications from Windows notification history. These commands are manual diagnostics and do not replace clean-machine acceptance in the disposable VM.

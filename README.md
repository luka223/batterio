# Batterio

Batterio is a Windows 11 x64 .NET 10 / .NET MAUI tray battery monitor. The app ships with Logitech G Pro X2 Superstrike, Keychron Q1 HE, and Logitech G Pro X2 Lightspeed devices, using their built-in adapters that report 64%, 35%, and 15% respectively.

## Build and run

The repository pins the .NET SDK in `global.json` and uses the installed .NET MAUI Windows workload.

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
dotnet build Batterio.sln -c Release --no-restore
dotnet test tests/Batterio.Core.Tests/Batterio.Core.Tests.csproj -c Release --no-restore
dotnet run --project src/Batterio.App/Batterio.App.csproj -c Debug
```

The application runs entirely from the system tray, with no separate main window or taskbar/Alt+Tab entry. Single-click the icon to toggle the compact popup, double-click to show it, and right-click for the application menu. Clicking outside the popup hides it; choose Close from the tray menu to stop the process.

## Devices and refresh

Devices are defined in code in `src/Batterio.Core/Adapters/BuiltInDevices.cs`. The app does not create or load device configuration files. It polls at startup and every five minutes after a reading completes. The popup refresh icon polls immediately. Manual refresh restarts the five-minute wait. Polling continues while the popup is hidden.

The popup sizes itself to the device content and grows with the device list up to a scrollable maximum height. Each device is shown on one row with a 52px photo, its name, a compact 10-segment battery icon, and percentage. One segment represents each 10% of charge. Battery colors are green above 40%, yellow from 20% through 40%, and red below 20%. Hover over a device for its last update or error details.

## Diagnostics

Logs are written to `%LocalAppData%\Batterio\Logs\batterio.log` with up to two rotated files, each bounded to approximately 512 KiB. Logs include device and adapter IDs where relevant and never serialize complete adapter settings.

## Adding an adapter

1. Implement `IBatteryAdapterFactory` with a stable `AdapterId`.
2. Use `ValidateSettings` only if your adapter needs settings supplied in code.
3. Create one adapter instance for each `DeviceConfiguration` in `CreateAdapter`.
4. Implement `IBatteryAdapter.GetBatteryStatus`, returning only an integer from 0 through 100.
5. Honor cancellation, release transport resources in `DisposeAsync`, and add contract tests for success, errors, timeout, cancellation, invalid values, and disposal.
6. Register the factory explicitly in `MauiProgram` and add its device to `BuiltInDevices.Create()`. No JSON file is needed.

Adapters must use cooperative cancellation. If a device ignores cancellation, Batterio marks the reading stale/unavailable immediately but retains the adapter call's concurrency slot until it actually finishes; late results are discarded when the active device list is replaced.

## Publish

The supported delivery is an unpackaged self-contained `win-x64` folder:

```powershell
dotnet publish src/Batterio.App/Batterio.App.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -o publish
```

The publish folder contains the .NET runtime and Windows App SDK runtime and does not require the .NET SDK on the target machine.


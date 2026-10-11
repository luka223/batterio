# Batterio

A Windows 11 x64 system tray app that shows the battery level of wireless gaming peripherals.

Supported devices:

- **Logitech G Pro X2 Superstrike**: over USB-C cable or Lightspeed receiver
- **Logitech G Pro X2 Lightspeed**: through its receiver
- **SteelSeries Arctis Nova Pro Omni**: headset and reserve battery through the GameHub (hub must be in **USB-1** mode)

Left-click the tray icon to open the popup, right-click it for the menu (Settings, Close). Batterio sends a Windows notification once per run when a device drops below 30% and when it is fully charged.

## Screenshots

| Popup | Tray menu | Settings |
| --- | --- | --- |
| ![Popup](docs/screenshots/popup2.png) | ![Tray menu](docs/screenshots/tray-menu.png) | ![Settings](docs/screenshots/settings.png) |

## Technologies

- .NET 10 (SDK pinned in `global.json`) and .NET MAUI on WinUI / Windows App SDK
- Raw HID communication (Logitech HID++, SteelSeries vendor protocol)
- NativeAOT launcher that starts and supervises the WinUI runtime process

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Batterio.Core` | Device adapters, polling, notifications logic, settings |
| `src/Batterio.App` | MAUI/WinUI tray app and popup (`Batterio.Runtime.exe`) |
| `src/Batterio.Startup` | NativeAOT launcher (`Batterio.App.exe` / `Batterio.exe`) |
| `tests/` | Unit tests plus manual startup and notification probes |
| `eng/` | Publish scripts |

New devices are added by implementing `IBatteryAdapterFactory` / `IBatteryAdapter` and registering them in `MauiProgram` and `BuiltInDevices.Create()`.

## Build and run

Requires the .NET 10 SDK, the .NET MAUI Windows workload and Visual Studio C++ x64 build tools.

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
dotnet build Batterio.sln -c Release --no-restore
dotnet test Batterio.sln -c Release --no-build
dotnet run --project src/Batterio.App/Batterio.App.csproj -c Debug
```

Hardware tests are opt-in: set `BATTERIO_HARDWARE_TESTS=1` with the devices connected.

## Publish

Both scripts produce a self-contained `win-x64` build; no .NET or Windows App Runtime install is needed on the target machine.

**Folder build**: outputs the `publish` folder. Copy the whole folder and run `Batterio.App.exe` from it.

```powershell
.\eng\Publish-Batterio.ps1
& .\publish\Batterio.App.exe
```

**Single-file build**: outputs a single `publish-self-contained\Batterio.exe`. On first start it unpacks itself to `%LocalAppData%\Batterio\Runtime`.

```powershell
.\eng\Publish-Batterio-self-contained.ps1
& .\publish-self-contained\Batterio.exe
```

## Data and diagnostics

- Settings: `%LocalAppData%\Batterio\settings.json`
- Logs: `%LocalAppData%\Batterio\Logs\` (`batterio.log`, `startup.log`)
- Set `BATTERIO_TRACE_HID=1` to log raw HID packets.
- To fully remove the app, also delete `%AppData%\Microsoft\Windows\Start Menu\Programs\Batterio.lnk` and `HKCU\Software\Classes\CLSID\{B68D18D7-6B67-4C24-993C-B597D789E895}`.

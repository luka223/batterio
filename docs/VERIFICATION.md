# Verification record

The automated core suite is in `tests/Batterio.Core.Tests` and currently contains 17 passing tests covering configuration defaults/rejection, adapter registration, click arbitration, polling results, timeout behavior, global concurrency, retired-adapter late-result rejection, and bounded diagnostics.

Run:

```powershell
dotnet restore Batterio.sln --configfile NuGet.Config --runtime win-x64
dotnet test tests/Batterio.Core.Tests/Batterio.Core.Tests.csproj -c Release --no-restore
dotnet build src/Batterio.App/Batterio.App.csproj -c Debug --no-restore
dotnet publish src/Batterio.App/Batterio.App.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -o publish
```

The final delivery review should additionally run the published executable on a clean Windows 11 x64 machine and check tray registration/recovery after Explorer restart, single/double/right clicks, close-to-tray, sleep/resume, configuration reload retention, and Exit while an adapter is unresponsive. Production registration contains the built-in device adapters.

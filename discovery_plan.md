# Implement Settings, startup registration, and device selection

All implementation steps below start unchecked. During implementation, change each `[ ]` to `[x]` only after its code and associated verification are complete. Record any blocked work beneath its step; do not mark partially finished steps complete.

## 1. Establish settings storage and ownership

- [x] **Add the preference model and storage interface in Core.**

  Create `UserSettings.cs` under [Core/Settings](D:/projects/batterio/src/Batterio.Core/Settings) and `IUserSettingsStore.cs` under [Core/Abstractions](D:/projects/batterio/src/Batterio.Core/Abstractions).

  Store a version number and a dictionary of device IDs to selection booleans:

  ```json
  {
    "version": 1,
    "deviceSelections": {
      "logitech-g-pro-x2-superstrike": false,
      "logitech-g-pro-x2-lightspeed": true
    }
  }
  ```

  A missing device ID means selected by default. Preserve entries for disconnected devices so reconnecting never resets a choice. Use case-insensitive device-ID lookup.

  Expose asynchronous load/save operations with cancellation support. Keep this separate from existing `DeviceConfiguration`: that class describes adapters and polling parameters; these settings represent user preferences.

- [x] **Implement atomic preference storage.**

  Add `JsonUserSettingsStore.cs` under [Core/Settings](D:/projects/batterio/src/Batterio.Core/Settings). Accept the destination path through its constructor so tests can use temporary directories.

  The app supplies `%LocalAppData%\Batterio\settings.json`. Write to a temporary file in the same directory, then atomically replace the destination. Serialize writes to prevent competing checkbox operations from losing preferences.

  A missing file loads defaults. For malformed or unsupported settings, report the problem, retain the original file, and use defaults in memory. Preserve a backup before a subsequent explicit change replaces that file. If preserving or saving fails, leave the original file untouched and report failure.

  **Why:** per-user storage requires no administrator privileges, and atomic replacement prevents an interrupted write from leaving half-written JSON.

## 2. Discover supported receivers without battery polling

- [x] **Share receiver-matching definitions between adapters and discovery.**

  In [Core/Adapters](D:/projects/batterio/src/Batterio.Core/Adapters), extract the existing `HidCollectionQuery` construction from both Logitech adapters into reusable definitions.

  Preserve their current vendor/product IDs, interface numbers, usage constraints, report IDs, and report lengths. Make each adapter use its extracted query.

  Add discovery metadata keyed by the existing stable built-in device IDs. Only expose catalog entries whose adapters are registered in `AdapterRegistry`.

  **Why:** discovery must recognize precisely the receivers the adapters support. Separate copies of these constants could drift and show devices that polling cannot use.

- [x] **Add a discovery service and snapshot type in Core.**

  Add `IDeviceDiscoveryService.cs` under [Core/Abstractions](D:/projects/batterio/src/Batterio.Core/Abstractions) and its implementation under [Core/Discovery](D:/projects/batterio/src/Batterio.Core/Discovery).

  Discover using `IHidCollectionProvider.Enumerate`; do not instantiate battery adapters, open report transports, or send battery requests.

  Return supported device IDs plus discovery errors. A successful empty result means disconnected. An enumeration failure means unknown: retain that model’s last successful presence result and expose the error.

  Keep one settings row per supported model, in built-in catalog order. Multiple matching receiver collections must not create duplicate rows or change the adapters’ existing ambiguity handling.

- [x] **Implement discovery scheduling outside the UI thread.**

  Scan at startup, on Settings/popup opening, after resume, and every five seconds while the app runs. Use one cancellable background loop and a refresh signal; coalesce requests and prohibit overlapping scans.

  Publish changes only when presence or error state changes. Cancel and await the loop during shutdown.

  **Why:** background enumeration discovers unchecked devices without battery polling. The five-second scan also handles receiver insertion/removal while both windows are closed.

## 3. Make the polling device set change safely

- [x] **Add incremental reconciliation to [PollingCoordinator.cs](D:/projects/batterio/src/Batterio.Core/Polling/PollingCoordinator.cs).**

  Add `SetActiveDevicesAsync(...)` accepting the complete desired active list.

  Reconcile by stable ID:

  | Change | Required behavior |
  |---|---|
  | Unchanged device/configuration | Keep its worker, reading, and polling schedule |
  | Added device | Add an initial state and request its first poll immediately |
  | Removed device | Remove its state immediately, cancel its worker, retire its adapter |
  | Changed adapter/configuration | Retire the old worker before starting its replacement |

  Preserve existing timeout, global concurrency, and refresh behavior. Manual refresh operates only on active workers.

  Keep `StartDevicesAsync` and `ApplyConfigurationAsync` available for existing callers and tests, retaining their explicit replacement semantics while sharing the safe retirement machinery.

  **Why:** restarting every adapter on each checkbox click would clear unrelated readings and trigger unnecessary hardware queries.

- [x] **Replace the global-generation-only freshness check with worker identity checks.**

  Incremental changes cannot invalidate every worker whenever one device changes. Associate each worker with a unique identity and accept its result only while that exact worker remains active for its device ID.

  Track retirement tasks by device ID. If the user quickly re-enables a device, defer starting its replacement until the previous worker finishes and its adapter is disposed. Recheck the desired state before starting the deferred replacement.

  Unchecking immediately prevents new queries and hides the row. An already-running query is canceled cooperatively; if cancellation is ignored, discard its eventual result.

  Include retired and pending workers in shutdown cleanup.

- [x] **Publish complete, revisioned polling snapshots.**

  Add a snapshot type containing a monotonically increasing revision and an ordered immutable copy of current device states. Publish after membership changes—including removal of the last device—and after reading changes.

  Publish events outside locks. Preserve the existing individual `StateChanged` event for compatibility, but move the popup to the complete snapshot event.

  **Why:** the current notification only adds or updates rows. A complete snapshot represents removals, while its revision lets the UI reject out-of-order queued updates.

## 4. Coordinate saved choices, discovery, and polling

- [x] **Add `DeviceSelectionCoordinator` under [Core/Settings](D:/projects/batterio/src/Batterio.Core/Settings).**

  Inject the built-in catalog, settings store, discovery service, polling coordinator, and logger.

  Expose:

  - A current snapshot of discovered devices, their selection states, and errors.
  - A snapshot-changed event.
  - `SetDeviceSelectedAsync(deviceId, selected, cancellationToken)`.
  - Lifecycle and discovery-refresh methods.

  Compute the active polling list as:

  ```text
  built-in devices with registered adapters
      ∩ discovered receiver IDs
      ∩ selected device IDs
  ```

  Load preferences before starting device workers. Serialize preference changes and discovery-result application through one asynchronous gate.

- [x] **Implement immediate, durable checkbox behavior.**

  For a device checkbox change:

  1. Build an updated preference snapshot.
  2. Persist it.
  3. Commit it in memory.
  4. Reconcile polling and publish the updated selection snapshot.

  On save failure, retain the previous preference and active set and return an error to the UI. If a receiver disappears during saving, preserve the choice but exclude the device when reconciling against current discovery.

  Adapter creation/query failures leave the selected device visible with its existing Offline behavior and diagnostics; they do not silently clear the user’s selection.

  **Why:** one coordinator prevents the settings page, popup, and polling service from maintaining conflicting device lists.

## 5. Implement Windows startup registration

- [x] **Add a testable startup service.**

  Add `IStartupRegistrationService.cs` under [Core/Abstractions](D:/projects/batterio/src/Batterio.Core/Abstractions) and `WindowsStartupRegistrationService.cs` under [App/Windows](D:/projects/batterio/src/Batterio.App/Windows).

  Provide read and set operations. Keep registry access behind a small injectable wrapper so automated tests never modify the real startup key.

  Use these exact values:

  ```csharp
  const string AppName = "Batterio";
  const string RunKeyPath =
      @"Software\Microsoft\Windows\CurrentVersion\Run";
  ```

  Enabling creates/opens the current-user key and stores:

  ```csharp
  key.SetValue(AppName, $"\"{exePath}\"", RegistryValueKind.String);
  ```

  Disabling opens the key if present and calls:

  ```csharp
  key?.DeleteValue(AppName, throwOnMissingValue: false);
  ```

  Resolve and validate the executable using `Environment.ProcessPath`; reject a missing path or a generic host executable. Do not derive it from the extraction directory used by single-file publishing.

- [x] **Define startup checkbox initialization and failure behavior.**

  Read the registration when Settings opens or regains focus. No entry means unchecked. Display the registration state without writing anything during initialization.

  Await changes and read back their result. On failure, restore the actual state where readable and show an inline error; if reading fails, show an unavailable state rather than pretending startup is disabled.

  Use the registry as the registration source of truth; do not store a duplicate boolean in JSON or modify unrelated registry entries.

  The feature registers execution at user sign-in. Windows controls scheduling and may delay Run entries; it cannot promise execution immediately at boot. External Windows Startup Apps policy remains authoritative. [Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

## 6. Build and connect the regular Settings window

- [x] **Create the page and view model under [Batterio.App](D:/projects/batterio/src/Batterio.App).**

  Add `SettingsPage.xaml`, its code-behind, and `SettingsViewModel.cs`.

  Use a scrollable layout with:

  - **General:** “Start automatically when Windows starts.”
  - **Devices:** “Choose which connected devices appear in the tray popup.”
  - One labeled checkbox per discovered supported model.
  - Loading, empty, and inline error states.

  Use existing theme resources, keyboard-accessible controls, and readable labels. No Save or Apply button.

  Route user changes through explicit asynchronous view-model methods. Programmatic initialization and rollback must not trigger writes. Disable the affected checkbox while its operation is pending and restore it afterward. Unsubscribe events when disposing the page/view model.

- [x] **Extend [WindowCoordinator.cs](D:/projects/batterio/src/Batterio.App/Windows/WindowCoordinator.cs).**

  Add `ShowSettings` to `TrayMenuAction` and `ShowSettingsAsync` to the window coordinator interface.

  Add **Settings** above the existing **Close** native menu item, using distinct named command IDs. Preserve menu cleanup and outside-click dismissal.

  Track a separate settings window. Create it lazily with title **Batterio Settings**, initial size approximately 560 × 440 logical pixels, and a normal resizable overlapped presenter. Keep standard title-bar controls, taskbar visibility, and Alt+Tab presence.

  Repeated requests restore and activate the same window. Closing Settings destroys only that window and clears its reference so it can be recreated. Do not attach popup deactivation or hide-on-close handlers to Settings.

  Close Settings during application shutdown before closing the hidden popup host.

- [x] **Wire services into application startup and menu handling.**

  Register services in [MauiProgram.cs](D:/projects/batterio/src/Batterio.App/MauiProgram.cs), using singleton lifetimes for application state and a fresh settings view model per settings-window lifetime.

  Update [BatteryApplication.cs](D:/projects/batterio/src/Batterio.App/BatteryApplication.cs) to start the tray promptly, initialize preferences/discovery, and delegate active-device management to the selection coordinator instead of polling both built-ins unconditionally.

  Route the Settings menu action to window creation on the UI thread. Route resume through discovery refresh before refreshing active batteries. Stop discovery before polling during exit.

  Preserve tray-only launch and the existing single-instance mechanism.

## 7. Synchronize the popup

- [x] **Update [BatteryViewModel.cs](D:/projects/batterio/src/Batterio.App/BatteryViewModel.cs) to consume polling snapshots.**

  On the UI thread, ignore older revisions and reconcile rows by ID: remove absent rows, update existing rows, insert new rows, and preserve catalog ordering.

  Keep unchanged row objects to avoid unnecessary visual replacement. Notify `HasNoDevices` after membership changes. Unsubscribe on disposal.

- [x] **Update popup layout and empty behavior.**

  Change the empty message in [PopupPage.xaml](D:/projects/batterio/src/Batterio.App/PopupPage.xaml) to “No devices selected or connected.”

  Notify the window coordinator when row membership changes so an already-visible popup resizes and stays anchored to the tray. Request discovery when opening it without blocking display.

  **Why:** filtering only at presentation time would leave hidden devices polling; this popup instead reflects the actual active worker set.

## 8. Verify behavior and finish documentation

- [x] **Add automated coverage under [Batterio.Core.Tests](D:/projects/batterio/tests/Batterio.Core.Tests).**

  Cover:

  - Missing preferences, default selection, persisted opt-out, reconnect, corrupt files, and failed saves.
  - Supported/unsupported receivers, descriptor matching, duplicate collections, discovery failures, and zero battery queries during discovery.
  - Active-set intersection and preference changes during discovery.
  - Removal during polling, rapid re-enable, stale results, unchanged-worker preservation, and empty snapshots.
  - Startup naming, quoting, missing values, read/write errors, and executable-path validation.

  Update the existing replacement-worker regression test to expect replacement polling only after the retired worker releases the device. Still verify that its late reading cannot become current.

- [x] **Build and run existing regression tests.**

  Run the documented Release build and core test commands. Resolve failures and warnings introduced by this work; the repository treats warnings as errors.

  Verification: `dotnet build Batterio.sln --configuration Release --no-restore` passed with 0 warnings and 0 errors. `dotnet test tests/Batterio.Core.Tests/Batterio.Core.Tests.csproj --configuration Release --no-build` passed 113 tests; 3 opt-in hardware tests were skipped.

- [ ] **Perform Windows acceptance checks.**

  Verify Settings open/focus/minimize/close behavior, keyboard interaction, light/dark themes, and tray-only startup. Test both receivers independently, unchecked-device reconnect, receiver removal while windows are open, and all devices unchecked.

  Using the published executable, verify startup registration appears in Windows Startup Apps, its path is correctly quoted, sign-in launches the tray app, and disabling removes the app’s Run value. Include single-file publishing validation.

  Not run: physical receiver, visual UI, Windows sign-in, Startup Apps, and published single-file acceptance checks require an interactive Windows session and hardware.

- [x] **Update documentation and completion status.**

  Update README and relevant configuration/verification documentation with the preference file location, receiver-based connection definition, five-second discovery interval, immediate checkbox behavior, and startup timing limitations.

  Preserve unrelated existing working-tree changes. Mark completed checklist items `[x]` and record verification results; leave any unverified hardware or sign-in checks explicitly unchecked.

  Verification: README, configuration, adapter, and verification documentation now describe `%LocalAppData%\Batterio\settings.json`, receiver-based connection state, five-second discovery, immediate checkbox persistence, and Windows sign-in timing limitations.

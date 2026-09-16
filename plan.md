# Simplify battery state and tray presentation

## Summary

Use exactly three states throughout the battery model: `Charging`, `NotCharging`, and `Offline`. Remove historical readings, stale-state handling, and update timestamps.

Keep the current successful state visible while refreshing. A failed read immediately clears it and shows Offline; retries remain Offline until usable data arrives.

Every implementation item below starts unchecked. During implementation, save this checklist as `docs/BATTERY-STATE-PLAN.md` and mark each item `[x]` only after its changes and relevant verification are complete. Record verification evidence alongside completed items; leave unperformed visual checks unchecked.

## State and adapter changes

- [ ] **Simplify the shared contract.** In `BatteryReading.cs`, replace the charge enum values with `Charging`, `NotCharging`, and `Offline`, making Offline the default value. Keep percentage nullable and validated within 0–100. Offline must carry no percentage. This provides one vocabulary across adapters, polling, and UI.

- [ ] **Replace historical device state.** In [DeviceBatteryState.cs](D:/projects/batterio/src/Batterio.Core/State/DeviceBatteryState.cs), store configuration and a current `BatteryReading`, initialized to Offline. Expose derived `State` and `Percentage`; do not store duplicate mutable values. Remove `BatteryRetrievalState`, all `Last*` properties, stale flags, timestamps, and formatted UI strings. Retain the existing state-change event contract.

- [ ] **Normalize protocol results at the adapter boundary.** Update Logitech decoding and adapter callers using the following mapping. Keep raw status codes and failure reasons in diagnostic logs, without additional battery-state enum values.

  | Adapter result | Published state | Percentage |
  |---|---|---|
  | Active charging | Charging | Reported value, or null |
  | Not charging or fully charged | NotCharging | Reported value, or null |
  | Valid percentage with missing/unrecognized charge status | NotCharging | Reported value |
  | Explicit battery error | Offline | null |
  | Neither usable percentage nor recognized status | Offline | null |
  | Communication failure or malformed response | Offline through polling failure handling | null |

  `NotCharging` is the agreed display fallback for percentage-only readings; it does not establish that discharge was measured. Never infer 100% from fully charged or reuse a previous percentage.

- [ ] **Update affected interfaces and consumers together.** Adjust adapter fixtures, enum references, constructor validation, and documentation. Remove `Discharging`, `Unknown`, `FullyCharged`, and `Error` from the shared charge enum. Keep structured adapter exceptions for diagnostics.

## Polling changes

- [ ] **Publish only current usable data.** In `PollingCoordinator`, atomically replace the reading after each completed attempt. Exceptions, timeouts, null results, and explicit Offline readings must clear the percentage immediately. Successful partial readings replace the entire previous reading.

- [ ] **Keep refresh transitions stable.** Starting an attempt does not change the displayed reading or introduce a fourth state. Startup, disabled devices, and adapter-configuration failures appear Offline. Keep the existing refresh animation independent of battery state.

- [ ] **Preserve concurrency protections.** Retain cancellation, per-device serialization, global concurrency limits, and configuration-generation checks. A timed-out adapter’s late success must not restore the battery display; hold its concurrency slot until the call finishes.

- [ ] **Remove obsolete timing dependencies.** Remove the polling clock parameter and field used solely for successful-update timestamps, update callers, and delete the now-unused clock abstraction. Preserve timestamped diagnostic logging.

## Tray presentation

- [ ] **Centralize presentation rules.** Update [BatteryViewModel.cs](D:/projects/batterio/src/Batterio.App/BatteryViewModel.cs) to derive icon visibility, percentage text, colors, and accessibility descriptions from the three-state model.

  | State | Battery appearance | Device appearance | Percentage |
  |---|---|---|---|
  | Charging | Full green battery with centered lightning bolt | Normal | Visible when provided |
  | NotCharging | Segmented battery using percentage color | Normal | Visible when provided |
  | Offline | Empty gray battery, no bolt | Grayscale device image and gray name | Hidden |

- [ ] **Preserve existing percentage rules.** For NotCharging, use red below 20%, yellow from 20–40%, and green above 40%. Fill `floor(percentage / 10)` of ten segments. A known NotCharging state without a percentage uses an empty gray battery but retains the normal device image and name.

- [ ] **Update row bindings.** In [PopupPage.xaml](D:/projects/batterio/src/Batterio.App/PopupPage.xaml), keep the battery container visible for all devices. Bind the percentage label’s visibility explicitly. Remove status labels such as Loading, Updating, Stale, and Discharging.

- [ ] **Gray unavailable device images.** Provide cached grayscale image variants through an app-side image helper, preserving transparency and supporting configured photos and fallback images. Restore the original image on recovery. Gray the name using theme-aware colors.

- [ ] **Remove time information from every presentation surface.** Remove the detail tooltip and timestamp formatter, plus the “Refreshes every 5 minutes” footer. Accessibility descriptions should announce name, Charging/Not charging/Offline, and percentage only when available.

- [ ] **Adjust popup sizing.** Update `WindowCoordinator` to measure the new percentage text instead of removed `StatusText`, and remove the footer’s height allowance. Verify rows remain aligned when percentages are hidden.

## Verification and documentation

- [ ] **Update state and protocol tests.** Cover all three enum values, invalid percentages, Offline without percentage, fully charged mapping, explicit errors, unknown-status fallback, and partial readings without historical value reuse.

- [ ] **Replace stale-reading regression tests.** Verify success → failure → retry → recovery, charging → Offline, first-read failure, timeout with late completion, and refresh preserving a successful display. Retain concurrency, cancellation, and configuration-replacement coverage.

- [ ] **Verify presentation rules.** Test state-to-presentation mapping and percentage boundaries at 0, 9, 10, 19, 20, 40, 41, and 100. Use a small framework-independent presentation helper for these assertions, with MAUI bindings consuming its results.

- [ ] **Build and run the existing suite.** Run the Release solution build and Core tests using the repository’s documented commands. Record results and any blockers in the checklist.

- [ ] **Perform Windows visual acceptance.** Check all three states, partial readings, configured photos, fallback images, light/dark themes, and display scaling. Confirm the bolt is centered, Offline is empty and gray, percentage disappears correctly, and no update time appears in rows, tooltips, or accessibility descriptions.

- [ ] **Update current documentation.** Revise README and adapter/configuration documentation for the three-state contract and removal of stale data. Add a supersession note to earlier plans where they describe conflicting behavior; preserve their historical completion records.

The scope is the device rows in the tray popup. The application’s notification-area icon and hardware communication sequences remain unchanged.

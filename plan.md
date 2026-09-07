# Logitech battery adapters and charging icons

## Summary

Implement real battery and charging status for **Logitech PRO X2 Superstrike** and **Logitech PRO X 2 LIGHTSPEED headphones**, communicating through their USB dongles with stock firmware.

Remove Keychron support from the application. No further Keychron protocol investigation or firmware work is included.

Each device row will show a **full green battery with a centered lightning bolt while actively charging**, alongside its actual percentage. Preserve the headset's Microsoft audio driver and Dolby Atmos configuration.

Checklist convention: `[x]` completed; `[ ]` outstanding. Implementation and hardware battery-query verification have not been performed.

- [x] Inspect adapter contracts, polling, configuration, tests, and battery UI.
- [x] Identify connected Logitech receivers: mouse `046D:C54D`, headset `046D:0AF7`.
- [x] Research HID++ and Centurion battery/charging protocols.
- [x] Locate Keychron registrations, implementation, display assets, and test references.

## 1. Remove Keychron and establish shared infrastructure

- [ ] **S1 — Remove Keychron support.** Delete its adapter and factory, remove its built-in device entry and dependency-injection registration, and remove its image mapping and unused keyboard image. Update tests to expect two built-in devices and revise documentation to remove Keychron support and mock-reading claims. Preserve the existing unknown-adapter error behavior for any manually configured obsolete Keychron entry; do not silently rewrite user configuration.

- [x] **S2 — Save and maintain this checklist.** Save the approved plan in `plan.md`, as requested. Maintain this file with upstream reference revisions and verification evidence. Mark each subsequent step complete only after its acceptance criteria pass.

- [ ] **S3 — Introduce a structured battery result.** Update the adapter interface and all callers/test doubles together:

  ```csharp
  Task<BatteryReading> GetBatteryStatus(
      CancellationToken cancellationToken = default);

  public sealed record BatteryReading(
      int? Percentage,
      BatteryChargeState ChargeState);

  public enum BatteryChargeState
  {
      Unknown,
      Discharging,
      Charging,
      FullyCharged,
      Error
  }
  ```

  Nullable percentage supports a valid status-only response without fabricating a number. Missing charging information means `Unknown`. Reject percentages outside `0–100`; a result with neither percentage nor known state is unavailable.

- [ ] **S4 — Store readings atomically.** Update polling and device state to retain the complete last successful reading and its timestamp. A new partial reading must not inherit an old percentage or charging state. Preserve the previous reading during refresh; after failure, mark it stale and suppress the charging bolt.

- [ ] **S5 — Add injectable HID transport.** Keep protocol logic in Core and native Windows access in `Batterio.App.Windows`, using the existing CsWin32 dependency. Provide collection enumeration, descriptor information, asynchronous report reads/writes, and disposal. Select collections by VID/PID, interface, usage, and supported reports—not enumeration order. This permits deterministic protocol tests without hardware. [Microsoft HID access](https://learn.microsoft.com/da-dk/windows-hardware/drivers/hid/opening-hid-collections)

- [ ] **S6 — Implement bounded, cancellable transactions.** Use overlapped I/O, safe handles, cancellation of pending operations, and one transaction reader per collection. Serialize transactions across adapter instances during reload. Validate report lengths and correlate responses, ignoring unrelated notifications. Closing an invalid session must invalidate its discovered feature indices.

- [ ] **S7 — Preserve configuration and polling defaults.** Keep the Logitech adapter IDs, automatic discovery with empty settings, the 300-second polling interval, and 10-second overall attempt timeout. Support an optional nonempty `devicePath` for ambiguous receivers; never substitute another device when that explicit path is missing. Open hardware during polling rather than factory construction.

- [ ] **S8 — Add useful diagnostics.** Distinguish missing receiver, ambiguity, access failure, offline device, unsupported capability, malformed response, and battery-reported error. Log protocol identity, feature indices, raw status codes, and query duration. Make packet traces opt-in and capture verified responses as test fixtures.

## 2. LogitechGProX2SuperstrikeAdapter

**Approach:** identify the paired mouse, dynamically resolve HID++ Unified Battery, and retrieve percentage and charging state in one query.

- [ ] **M1 — Identify the receiver collection and paired mouse.** Match `046D:C54D` vendor HID collections on interface 2. Probe slot `0x01` first, then other receiver slots within the attempt deadline if necessary. Confirm the mouse identity, including wireless model ID `0x40BD`; distinguish the mouse from its receiver. [Superstrike hardware investigation](https://github.com/mclol0/linux-superstrike/blob/main/REVERSE_ENGINEERING.md)

- [ ] **M2 — Implement minimal HID++.** Support short `0x10` and long `0x11` reports, protocol ping, ROOT lookup, and protocol errors. Correlate device index, feature index, function, and nonzero software ID. Resolve battery feature `0x1004` dynamically so firmware-dependent feature-table indices are not hardcoded.

- [ ] **M3 — Read battery status.** Query Unified Battery capabilities and function 1 (`0x10` before software-ID bits). Decode the first response parameter as percentage when supported and the third as charging status. Do not convert coarse battery categories into percentages. [Solaar battery implementation](https://raw.githubusercontent.com/pwr-Solaar/Solaar/master/lib/logitech_receiver/hidpp20.py)

- [ ] **M4 — Map statuses explicitly.** Map `0x00` to `Discharging`; `0x01`, `0x02`, and `0x04` to `Charging`; `0x03` to `FullyCharged`; and `0x05`/`0x06` to `Error`. Log unrecognized codes and return `Unknown`. [HID++ status definitions](https://raw.githubusercontent.com/pwr-Solaar/Solaar/master/lib/logitech_receiver/common.py)

- [ ] **M5 — Validate the dongle path while charging.** Test charging, charge completion, cable removal, sleep/wake, receiver reconnection, and G HUB running/exited. Confirm that a charging cable creating a wired device does not redirect the adapter away from its dongle. Queries must leave DPI, haptics, polling rate, pairing, and profiles unchanged.

## 3. LogitechGProX2LightspeedAdapter

**Approach:** retrieve battery information through the receiver's Centurion HID bridge, independently of its audio interface.

- [ ] **H1 — Select the battery collection.** Match `046D:0AF7`, interface 3, usage page `0xFFA0`, usage `0x0001`. Use report ID `0x51` and 64-byte Centurion framing. Avoid selecting the audio interface, another vendor collection, or the additional observed `046D:0AF8` device. [HeadsetControl device definition](https://raw.githubusercontent.com/Sapd/HeadsetControl/master/lib/devices/logitech_gpro_x2_lightspeed.hpp)

- [ ] **H2 — Discover the bridge and battery feature.** Resolve FeatureSet, receiver bridge `0x0003`, and headset battery feature `0x0104`. Implement validated frame lengths, acknowledgements, subdevice responses, errors, and request correlation. Cache indices only for the valid session. Use small individual feature queries and reject unexpected fragmentation safely. [Centurion transport implementation](https://raw.githubusercontent.com/Sapd/HeadsetControl/master/lib/devices/protocols/logitech_centurion_protocol.hpp)

- [ ] **H3 — Decode the modern response.** Query battery function 0. After stripping transport headers, read percentage from payload byte 0 and status from byte 2 when present. Map `0` to `Discharging`, `1`/`2` to `Charging`, and `3` to `FullyCharged`; missing or unrecognized codes become `Unknown`. [Centurion battery decoding](https://raw.githubusercontent.com/pwr-Solaar/Solaar/master/lib/logitech_receiver/centurion.py)

- [ ] **H4 — Implement the narrow legacy fallback.** Only when modern battery support is positively identified as unsupported, send the zero-padded request beginning `51 08 00 03 1A 00 03 00 04 0A`. Validate the response signature, read percentage at full-report offset 10, and recognize charging value `0x02` at offset 12. Other legacy statuses remain unknown unless independently verified. Skip acknowledgements, recognize offline responses, and never fall back after a timeout or malformed modern reply. [Legacy battery implementation](https://raw.githubusercontent.com/Sapd/HeadsetControl/master/lib/devices/logitech_gpro_x2_lightspeed.hpp)

- [ ] **H5 — Verify Microsoft-driver and Dolby Atmos operation.** Query battery and charging status while playing audio with Dolby Atmos enabled. Confirm uninterrupted playback, microphone operation, and unchanged spatial-sound selection. Repeat with G HUB running/exited and after power cycling or reconnecting. Windows' separate composite-device function stacks support this design, but the actual setup must pass hardware testing. [Microsoft USB architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-3-0-driver-stack-architecture)

## 4. Charging icon and state presentation

- [ ] **U1 — Apply one presentation rule to both devices.** Show the charging visual when the latest successful reading reports `Charging` and is ready, or is temporarily retained during an ongoing refresh. Apply it to each popup device row; retain the application's existing Windows tray icon.

- [ ] **U2 — Draw the requested icon.** Preserve the existing battery silhouette and approximately `42 × 18` footprint. When charging, render a solid full interior, outline, and terminal in green `#22C55E`, with a centered dark lightning bolt `#14532D`. Use a XAML vector path for consistent scaling.

- [ ] **U3 — Preserve the actual percentage.** A device charging at `15%` shows the full green charging icon alongside `15%`. The full shape represents charging, not a fabricated `100%`. If only charging status is available, show `Charging` as the adjacent text.

- [ ] **U4 — Define noncharging and stale visuals.** Retain percentage-based fill and colors for discharging or unknown status. `FullyCharged` does not show a bolt; preserve the reported percentage and describe the state in the tooltip. Errors never show the charging icon. After a failed refresh, remove the bolt, retain any historical percentage with stale text, and describe charging only as “last reported.”

- [ ] **U5 — Update accessibility and property notifications.** Provide descriptions such as “Charging, 15 percent” and “Charging; percentage unavailable.” Update icon, percentage, tooltip, and accessible description together to prevent conflicting states.

**Default timing:** charging changes appear on the next successful scheduled or manual refresh. Immediate cable-event detection and faster background polling are outside this version.

## 5. Tests and completion criteria

- [ ] **T1 — Update existing tests.** Expect two built-in devices, remove Keychron assertions, and replace mock-production percentages with injected transport fixtures. Retain startup, scheduled polling, and manual-refresh coverage.

- [ ] **T2 — Test protocol responses.** Cover `0`, intermediate, and `100` percentages; every supported charging status; missing status fields; invalid lengths/values; unrelated responses; protocol errors; and legacy-fallback eligibility.

- [ ] **T3 — Test lifecycle and freshness.** Cover charging → discharging, fully charged, unknown, and stale. Verify partial readings never inherit obsolete fields. Test cancellation, timeout cleanup, disposal, receiver reconnection, serialized reload access, and late-result rejection.

- [ ] **T4 — Verify the icon visually.** Confirm full green fill and a centered bolt at a low actual percentage. Check charging-only readings, stale suppression, and absence of a bolt for fully charged/error/unknown states. Inspect light/dark themes and Windows scaling at 100%, 150%, and 200%.

- [ ] **T5 — Run automated checks.** Run Core tests and the Release solution build using the repository's configured .NET environment. Ordinary tests must pass without attached receivers; hardware checks remain opt-in.

- [ ] **T6 — Complete hardware acceptance.** Demonstrate repeated fresh battery readings and charging transitions for both devices through their dongles. Verify recovery without restarting Batterio and pass the headset's Microsoft-driver/Dolby Atmos tests.

- [ ] **T7 — Finish documentation and checklist.** Document the structured adapter contract, status mappings, receiver selection, polling latency, and verified firmware combinations. Correct outdated mock/support claims and the documented polling default. Mark implementation and verification steps done only when supported by evidence.

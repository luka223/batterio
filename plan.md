# Keychron Q1 HE battery adapter (via Keychron Link dongle)

## Context

The user's custom Q1 HE firmware (`D:\projects\qmk_firmware`, see its README) makes the keyboard **push** a
32-byte raw HID report every ~2 s through the Keychron Link 2.4 GHz dongle. This was verified on hardware on
2026-10-15: the dongle's raw HID interface (VID `0x3434`, PID `0xD030`, interface 3, usage page `0xFF60`,
usage `0x61`, 33-byte reports including report ID 0) delivered `A4 64 26 10 02 04 02 …` every 2.00 s.

The keyboard stays on 2.4 GHz while charging over the cable, so the adapter **only listens on the dongle**
(no USB-cable request path). The adapter must behave like the existing ones: polled every 300 s by
`PollingCoordinator`, and on demand by the popup's refresh button. Q1 HE must be the **first** device in
the list, using the image the user added.

Report layout after the Windows report-ID byte (`buffer[0] == 0x00`):

| Payload byte | Meaning |
|---|---|
| 0 | `0xA4` |
| 1 | battery % (0–100) |
| 2–3 | voltage mV, little-endian (log only) |
| 4 | 0 = on battery, 1 = charging, 2 = full |
| 5 | connection (`0x04` = 2.4 GHz) |
| 6 | model id, `2` = Q1 HE |

How a poll works: open the dongle collection, read frames for up to **4 s** (two push periods), and return
the first frame with payload `A4 … model 2`. Nothing to write. If none arrives, the keyboard is asleep or off,
or it runs stock firmware → throw `DeviceOffline` (the same way the Bolt adapter reports a sleeping device).
4 s fits inside the default `TimeoutSeconds = 10`.

Charge mapping (matches `LogitechBatteryDecoding.DecodeSuperstrikeStatus`, where "complete" → NotCharging):
0 → `NotCharging`, 1 → `Charging`, 2 → `NotCharging`, unknown → `NotCharging` + warning log.

## Steps

Mark each step `[x]` in this file as soon as it is completed.

- [x] **1. Rename the image to a valid MAUI resource name.**
  `git mv src/Batterio.App/Resources/Images/keychron-q1-he.png src/Batterio.App/Resources/Images/keychron_q1_he.png`.
  *Why:* `<MauiImage Include="Resources\Images\*" />` (`Batterio.App.csproj:45`) runs the MAUI resizetizer,
  which only accepts lowercase letters, digits and underscores. A hyphen fails the build. The existing
  images follow this rule (`mx_master_3s.webp`).

- [x] **2. Add `KeychronDefinitions` (new `src/Batterio.Core/Adapters/KeychronDefinitions.cs`).**
  Constants `VendorId = 0x3434`, `LinkProductId = 0xD030`, `BatteryReportCommand = 0xA4`,
  `Q1HeModelId = 0x02`, and `LinkRawHidQuery` (`InterfaceNumber = 3`, `UsagePage = 0xFF60`, `Usage = 0x0061`,
  `MinimumReportLength = 33`). Add an XML comment saying the values were measured on a real dongle.
  *Why:* this follows `SteelSeriesDefinitions.cs`. The query is shared by the adapter and the catalog's
  presence detection, so it lives in one place. No `RequiredReportId`: the raw HID collection uses report ID 0,
  and usage page/usage already identify it uniquely.

- [x] **3. Add the pure decoder `KeychronBatteryReportDecoding` (new `src/Batterio.Core/Adapters/KeychronBatteryReportDecoding.cs`).**
  `bool IsQ1HeBatteryReport(ReadOnlySpan<byte> frame)`: length ≥ 8, `frame[1] == 0xA4`, `frame[7] == 2`
  (offsets include the report-ID byte). `BatteryReading Decode(ReadOnlySpan<byte> frame)`: throws
  `MalformedResponse` when % > 100, then maps the charge byte as above. Also expose
  `ushort DecodeMillivolts(...)` for logging.
  *Why:* this mirrors `SteelSeriesNovaProOmniStatusDecoding`. Byte-level logic stays testable without any HID
  fakes, and the model-id filter ignores other Keychron boards running a similar mod (the K10 HE uses model 1).

- [x] **4. Implement `KeychronQ1HeAdapter` + `KeychronQ1HeAdapterFactory` (new `src/Batterio.Core/Adapters/KeychronQ1HeAdapter.cs`).**
  Use `SteelSeriesArctisNovaProOmniAdapter.cs` as the template: `_lifetime` CTS, `_disposed`, optional `devicePath`,
  `ReceiverMissing` / `ReceiverAmbiguous` selection, `TracingHidTransaction`, and log lines with the elapsed time.
  The difference: **no write**. A read loop with `listenTimeout` (default 4 s, injectable for tests) skips
  non-matching frames (other raw HID traffic), returns the first matching report, and logs `%`, mV, charge byte
  and connection. If the deadline expires, throw `DeviceOffline`: "Keychron Q1 HE sent no battery report through
  the Keychron Link receiver (keyboard asleep/off, or not running the battery-monitor firmware)."
  Cap the loop at a maximum frame count, like `MaximumFramesPerQuery`, and throw `ProtocolError` when it's exceeded.
  Factory: `AdapterId = "keychron-q1-he"`, and validate settings via `HidAdapterSettings.ReadDevicePath`.
  *Why:* the keyboard ignores PC→keyboard requests over 2.4 GHz, so passive listening is the only way. Opening per
  poll (instead of a long-lived listener) keeps the adapter stateless like the others, and gives fresh values on
  both the 5-minute timer and the refresh button.

- [x] **5. Register the device first in the catalog (`src/Batterio.Core/Adapters/BuiltInDevices.cs`).**
  Insert at index 0: `Id = "keychron-q1-he"`, `Name = "Keychron Q1 HE"`, `AdapterId = "keychron-q1-he"`,
  `PresenceQueries = [KeychronDefinitions.LinkRawHidQuery]`.
  *Why:* `DeviceDiscoveryService.OrderedStates()` (line 316) and the popup order follow `Catalog` order, so
  index 0 makes it the first device shown.

- [x] **6. Register the factory in DI (`src/Batterio.App/MauiProgram.cs`, around line 24).**
  `builder.Services.AddSingleton<IBatteryAdapterFactory, KeychronQ1HeAdapterFactory>();`. Also check that the
  factory's constructor matches how the other factories receive `IHidCollectionProvider`/`IBatterioLog` there.
  *Why:* `AdapterRegistry` only knows factories that are injected. Without this, the catalog entry fails with
  "Adapter 'keychron-q1-he' is not registered".

- [x] **7. Map the image (`src/Batterio.App/BatteryImageHelper.cs`, `GetFallbackImageSource`).**
  Add `"keychron-q1-he" => "keychron_q1_he.png"`.
  *Why:* the built-in devices resolve their picture by device id here. Otherwise it falls back to the placeholder
  (the disabled/greyscale variant is derived from this automatically).

- [x] **8. Decoder unit tests (new `tests/Batterio.Core.Tests/KeychronBatteryReportDecodingTests.cs`).**
  Use the real captured frame `00 A4 64 26 10 02 04 02` → 100 %, NotCharging, 4134 mV. Also cover charge byte 1 →
  Charging; 0 → NotCharging; wrong command byte / model id 1 / short frame → not a report; % = 101 →
  `MalformedResponse`.
  *Why:* this pins the byte offsets, especially the report-ID shift, against hardware-captured data.

- [x] **9. Adapter unit tests (new `tests/Batterio.Core.Tests/KeychronQ1HeAdapterTests.cs`).**
  Use a fake provider modelled on `OmniProvider` in `SteelSeriesOmniAdapterTests.cs`. Cases: unrelated frames
  before a report are skipped; **no writes happen**; silence → `DeviceOffline` (short injected timeout); no
  dongle → `ReceiverMissing`; two dongles without `devicePath` → `ReceiverAmbiguous`; `devicePath` selects one;
  pre-cancelled token → `OperationCanceledException`.
  *Why:* this covers the adapter contract the poller relies on (offline vs missing vs error).

- [x] **10. Update the existing catalog tests (`tests/Batterio.Core.Tests/BuiltInDeviceTests.cs`).**
  `BuiltInDevicesContainTheShippedAdapters`: count 5→6, `keychron-q1-he` first in the expected id list, add a
  `FixedFactory`, readings 5→6 and the final total 11→13 (6 initial + 6 refresh-all + 1 single refresh; check
  this against the test's arithmetic). `RefreshDeviceAsync(devices[0].Id)` now refreshes Keychron, which is fine.
  Add the adapter to `TestAdaptersHonorCancellation`. Add a small test asserting the Link query values
  (like `MxDevicesShareTheBoltReceiverPresenceQuery`).
  *Why:* these tests hard-code the catalog and would otherwise fail. The new assertion guards the measured
  descriptor values.

- [x] **11. Hardware smoke test (`tests/Batterio.Core.Tests/HardwareSmokeTests.cs`).**
  `[HardwareFact] KeychronQ1HeReportsThroughLinkReceiver`: real `WindowsHidCollectionProvider`, 10 s timeout,
  print the reading, and assert not offline.
  *Why:* this matches the opt-in hardware tests for the other devices (`BATTERIO_HARDWARE_TESTS=1`).

- [x] **12. Update README (`README.md`, "Supported devices").**
  Add **Keychron Q1 HE** (first): battery through the Keychron Link 2.4 GHz receiver, **requires the
  battery-monitor custom firmware**. With stock firmware or a sleeping keyboard it shows as offline.
  *Why:* this sets expectations. The dongle is detected even with stock firmware, which never sends reports.

## Verification

1. `dotnet build Batterio.sln -c Release` checks that the image name and DI registration compile and pass the resizetizer.
2. `dotnet test Batterio.sln -c Release --no-build` must pass: the new decoder/adapter tests and the updated `BuiltInDeviceTests`.
3. Hardware: `BATTERIO_HARDWARE_TESTS=1 dotnet test --filter KeychronQ1He` with the dongle connected and the keyboard awake.
4. App: `dotnet run --project src/Batterio.App/Batterio.App.csproj -c Debug`. Keychron Q1 HE appears first with
   its image and a percentage, and the refresh button updates it. Plug the cable in and press refresh to see
   charging/full. Let the keyboard sleep and press refresh to see offline. Check `batterio.log` for the decoded line.
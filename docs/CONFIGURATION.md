# Configuration reference

`%LocalAppData%\Batterio\devices.json` is a versioned JSON document. The current version is `1`.

Each device requires a unique `id`, a nonempty `name`, a registered `adapterId`, and a JSON-object `adapterSettings`. `enabled` defaults to `true`, `pollIntervalSeconds` defaults to `60` and must be at least `5`, and `timeoutSeconds` defaults to `10`, must be at least `1`, and cannot exceed the polling interval.

`photoPath` is optional. Relative paths are resolved from `%LocalAppData%\Batterio`; absolute local paths are also accepted. A missing, unreadable, or invalid photo does not reject the configuration and is rendered using the placeholder image.

Common schema errors reject the whole reload so a partially written file cannot replace a working configuration. Unknown adapter IDs and adapter-specific settings errors remain per-device errors, allowing other configured devices to continue polling.

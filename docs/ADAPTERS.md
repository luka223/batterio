# Adapter extension workflow

The adapter framework is deliberately compiled into the application. Runtime DLL loading and device discovery are outside the current scope.

Implement `IBatteryAdapterFactory` in a production adapter assembly and register it explicitly in `MauiProgram`:

```csharp
builder.Services.AddSingleton<IBatteryAdapterFactory, MyBatteryAdapterFactory>();
```

The factory must expose a stable, unique `AdapterId`, validate its device's `adapterSettings` JSON object, and create a new adapter instance for every configured device. Do not share mutable adapter instances between devices.

Each adapter implements:

```csharp
Task<int> GetBatteryStatus(CancellationToken cancellationToken = default);
```

Return a real percentage from `0` through `100`; throw for communication or protocol failures rather than returning a sentinel value. Honor cancellation promptly, and release connections, handles, and other resources in `DisposeAsync`.

Add deterministic tests for factory validation, independent instances/settings, 0/100/intermediate readings, invalid values, exceptions, cooperative cancellation, ignored cancellation, timeouts, disposal, and late-result rejection during reload.

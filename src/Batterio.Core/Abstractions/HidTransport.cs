namespace Batterio.Core.Abstractions;

public sealed record HidCollectionQuery
{
    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    public ushort? InterfaceNumber { get; init; }

    public ushort? UsagePage { get; init; }

    public ushort? Usage { get; init; }

    public byte? RequiredReportId { get; init; }

    public int MinimumReportLength { get; init; }

    public bool Matches(HidCollectionInfo collection)
    {
        return collection.InputReportLength >= MinimumReportLength &&
               collection.OutputReportLength >= MinimumReportLength &&
               collection.VendorId == VendorId &&
               collection.ProductId == ProductId &&
               (!InterfaceNumber.HasValue || collection.InterfaceNumber == InterfaceNumber) &&
               (!UsagePage.HasValue || collection.UsagePage == UsagePage) &&
               (!Usage.HasValue || collection.Usage == Usage) &&
               (!RequiredReportId.HasValue || collection.SupportsReport(RequiredReportId.Value));
    }
}

public sealed record HidCollectionInfo
{
    public required string DevicePath { get; init; }

    public required ushort VendorId { get; init; }

    public required ushort ProductId { get; init; }

    public ushort? InterfaceNumber { get; init; }

    public ushort? UsagePage { get; init; }

    public ushort? Usage { get; init; }

    public int InputReportLength { get; init; }

    public int OutputReportLength { get; init; }

    public int FeatureReportLength { get; init; }

    /// <summary>Known report IDs; null means the native descriptor did not expose an inventory.</summary>
    public IReadOnlySet<byte>? SupportedReportIds { get; init; }

    public bool SupportsReport(byte reportId) =>
        SupportedReportIds is not null && SupportedReportIds.Contains(reportId);
}

public interface IHidCollectionProvider
{
    IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query);

    ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default);
}

public interface IHidReportTransport : IAsyncDisposable
{
    HidCollectionInfo Collection { get; }

    ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

public interface IHidReportTransaction : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default);
}

public sealed class EmptyHidCollectionProvider : IHidCollectionProvider
{
    public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => Array.Empty<HidCollectionInfo>();

    public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IHidReportTransport>(new InvalidOperationException("No HID transport is configured."));
}

public enum BatteryAdapterErrorKind
{
    ReceiverMissing,
    ReceiverAmbiguous,
    AccessFailure,
    DeviceOffline,
    DeviceBusy,
    UnsupportedCapability,
    MalformedResponse,
    ProtocolError,
    BatteryReportedError
}

public sealed class BatteryAdapterException : Exception
{
    public BatteryAdapterException(BatteryAdapterErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public BatteryAdapterException(BatteryAdapterErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BatteryAdapterErrorKind Kind { get; }
}

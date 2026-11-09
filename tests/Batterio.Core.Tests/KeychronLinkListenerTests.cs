using System.Collections.Concurrent;
using System.Threading.Channels;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

/// <summary>The listener that follows the Keychron Link dongle all the time, and the route that reads the reports it keeps.</summary>
public sealed class KeychronLinkListenerTests
{
    private const string LinkPath = @"\\?\hid#vid_3434&pid_d030&mi_03&col01#a&1";

    [Fact]
    public async Task TheFirstReportMakesTheRouteOnlineAndRaisesAStateChange()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        await using var listener = new KeychronLinkListener(hid, Link(), time);
        var changes = 0;
        listener.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        listener.Start();
        await Eventually(() => hid.Opens == 1);
        Assert.Equal(RouteState.Unknown, listener.State);

        hid.Push(KeychronBatteryReportDecodingTests.Frame(72));

        await Eventually(() => listener.State == RouteState.Online);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(72, listener.TryGetFrame(TimeSpan.FromSeconds(4))![KeychronBatteryReportDecoding.PercentageOffset]);

        // Further reports keep it online without announcing anything.
        hid.Push(KeychronBatteryReportDecodingTests.Frame(71));
        await Eventually(() => listener.TryGetFrame(TimeSpan.FromSeconds(4))![KeychronBatteryReportDecoding.PercentageOffset] == 71);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Empty(hid.Writes);
    }

    [Fact]
    public async Task SilenceMakesTheRouteOfflineAndTheNextReportBringsItBack()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        await using var listener = new KeychronLinkListener(hid, Link(), time);
        var changes = new ConcurrentQueue<RouteState>();
        listener.StateChanged += (_, _) => changes.Enqueue(listener.State);
        listener.Start();
        hid.Push(KeychronBatteryReportDecodingTests.Frame());
        await Eventually(() => changes.Count == 1); // the handler ran, so it saw Online

        time.Advance(KeychronLinkListener.SilenceTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(RouteState.Online, listener.State);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(RouteState.Offline, listener.State);
        Assert.Equal(2, changes.Count);

        hid.Push(KeychronBatteryReportDecodingTests.Frame());
        await Eventually(() => changes.Count == 3);
        Assert.Equal(new[] { RouteState.Online, RouteState.Offline, RouteState.Online }, changes.ToArray());
    }

    [Fact]
    public async Task ARouteWithNoReportAtAllGoesOfflineAfterTheSilenceTimeout()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        await using var listener = new KeychronLinkListener(hid, Link(), time);
        listener.Start();
        await Eventually(() => hid.Opens == 1);

        time.Advance(KeychronLinkListener.SilenceTimeout + TimeSpan.FromMilliseconds(1));

        Assert.Equal(RouteState.Offline, listener.State);
    }

    [Fact]
    public async Task AReadErrorClosesTheListenerAndTheNextSyncReplacesIt()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        await using var monitor = new KeychronLinkMonitor(hid, null, time);
        var changes = 0;
        monitor.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        var collection = Link();
        await monitor.SyncAsync([collection]);
        Assert.True(monitor.TryGetListener(LinkPath, out var first));
        hid.Push(KeychronBatteryReportDecodingTests.Frame());
        await Eventually(() => first.State == RouteState.Online);

        hid.Fail(new IOException("the dongle was unplugged"));

        await Eventually(() => first.IsClosed);
        Assert.False(monitor.TryGetListener(LinkPath, out _));
        Assert.Equal(RouteState.Unknown, monitor.GetState(collection));
        Assert.True(Volatile.Read(ref changes) >= 2);

        hid.Reset();
        await monitor.SyncAsync([collection]);
        Assert.True(monitor.TryGetListener(LinkPath, out var second));
        Assert.NotSame(first, second);
        await Eventually(() => hid.Opens == 2);
    }

    [Fact]
    public async Task TheMonitorListensOnlyWhileTheDongleIsPluggedIn()
    {
        var hid = new PushProvider();
        await using var monitor = new KeychronLinkMonitor(hid, null, new ControlledTimeProvider());
        var other = Link() with { DevicePath = "somewhere-else", ProductId = 0x1234 };
        Assert.Null(monitor.GetState(other));

        await monitor.SyncAsync([other]);
        Assert.False(monitor.TryGetListener("somewhere-else", out _));

        await monitor.SyncAsync([Link()]);
        Assert.True(monitor.TryGetListener(LinkPath, out var listener));
        Assert.Equal(RouteState.Unknown, monitor.GetState(Link()));

        await monitor.SyncAsync([]);
        Assert.False(monitor.TryGetListener(LinkPath, out _));
        await Eventually(() => listener.IsClosed);
    }

    [Fact]
    public async Task ARecentReportAnswersTheReadWithoutOpeningASecondHandle()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        var harness = Harness(hid, time, out var monitor);
        await using var _ = monitor;
        await using var adapter = harness.CreateAdapter();
        await monitor.SyncAsync(harness.Routes.Select(route => route.Collection!).ToArray());
        hid.Push(KeychronBatteryReportDecodingTests.Frame(64));
        await Eventually(() => monitor.GetState(Link()) == RouteState.Online);

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(64, reading.Percentage);
        Assert.Equal(1, hid.Opens); // only the listener's
        Assert.Empty(hid.Writes);
    }

    [Fact]
    public async Task AStaleReportWaitsForTheNextOneInsteadOfReadingTheOldValue()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        var harness = Harness(hid, time, out var monitor);
        await using var _ = monitor;
        await using var adapter = harness.CreateAdapter();
        await monitor.SyncAsync(harness.Routes.Select(route => route.Collection!).ToArray());
        hid.Push(KeychronBatteryReportDecodingTests.Frame(64));
        await Eventually(() => monitor.GetState(Link()) == RouteState.Online);
        time.Advance(KeychronLinkRoute.CachedFrameMaxAge + TimeSpan.FromSeconds(1));

        var pending = adapter.GetBatteryStatus();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        hid.Push(KeychronBatteryReportDecodingTests.Frame(63));

        Assert.Equal(63, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Percentage);
        Assert.Equal(1, hid.Opens);
    }

    [Fact]
    public async Task WhenNothingArrivesWithinTheListenTimeoutTheDeviceIsOffline()
    {
        var hid = new PushProvider();
        var time = new ControlledTimeProvider();
        var harness = Harness(hid, time, out var monitor);
        await using var _ = monitor;
        await using var adapter = harness.CreateAdapter();
        await monitor.SyncAsync(harness.Routes.Select(route => route.Collection!).ToArray());

        var pending = adapter.GetBatteryStatus();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        time.Advance(KeychronLinkRoute.DefaultListenTimeout + TimeSpan.FromMilliseconds(1));

        var error = await Assert.ThrowsAsync<BatteryAdapterException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BatteryAdapterErrorKind.DeviceOffline, error.Kind);
    }

    [Fact]
    public async Task WithoutAListenerTheRouteListensForItselfAsBefore()
    {
        var hid = new PushProvider();
        var harness = new RouteHarness(KeychronQ1He.Profile, hid, keychronLinks: new KeychronLinkMonitor(hid));
        await using var adapter = harness.CreateAdapter();
        hid.Push(KeychronBatteryReportDecodingTests.Frame(55));

        var reading = await adapter.GetBatteryStatus();

        Assert.Equal(55, reading.Percentage);
        Assert.Equal(1, hid.Opens);
    }

    private static RouteHarness Harness(PushProvider hid, TimeProvider time, out KeychronLinkMonitor monitor)
    {
        monitor = new KeychronLinkMonitor(hid, null, time);
        return new RouteHarness(KeychronQ1He.Profile, hid, timeProvider: time, usbSources: [monitor], keychronLinks: monitor);
    }

    private static HidCollectionInfo Link() => UsbRoutesTests.CollectionFor(KeychronQ1He.LinkRawHidQuery, LinkPath);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>A dongle whose reports the test pushes by hand; every open gets its own reader on the same stream of reports.</summary>
    private sealed class PushProvider : IHidCollectionProvider
    {
        private Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>();
        private int _opens;

        public int Opens => Volatile.Read(ref _opens);

        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public IReadOnlyList<HidCollectionInfo> Enumerate(HidCollectionQuery query) => [Link()];

        public void Push(byte[] report) => _reports.Writer.TryWrite(report);

        public void Fail(Exception exception) => _reports.Writer.TryComplete(exception);

        public void Reset() => _reports = Channel.CreateUnbounded<byte[]>();

        public ValueTask<IHidReportTransport> OpenAsync(HidCollectionInfo collection, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opens);
            return ValueTask.FromResult<IHidReportTransport>(new Transport(this, collection, _reports));
        }

        private sealed class Transport(PushProvider owner, HidCollectionInfo collection, Channel<byte[]> reports)
            : IHidReportTransport, IHidReportTransaction
        {
            public HidCollectionInfo Collection => collection;

            public ValueTask<IHidReportTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<IHidReportTransaction>(this);

            public ValueTask WriteAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
            {
                owner.Writes.Enqueue(report.ToArray());
                return ValueTask.CompletedTask;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> report, CancellationToken cancellationToken = default)
            {
                var frame = await reports.Reader.ReadAsync(cancellationToken);
                frame.CopyTo(report);
                return frame.Length;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Time that only moves when the test says so, including the timers and delays built on it.</summary>
    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = new();
        private DateTimeOffset _now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate)
            {
                target = _now + by;
            }

            while (true)
            {
                Timer? next;
                lock (_gate)
                {
                    next = _timers.Where(timer => timer.Due is { } due && due <= target).MinBy(timer => timer.Due);
                    if (next is null)
                    {
                        _now = target;
                        return;
                    }

                    _now = next.Due!.Value;
                    next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
                }

                next.Fire();
            }
        }

        private sealed class Timer(ControlledTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public TimeSpan Period { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                    if (!owner._timers.Contains(this))
                    {
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class InteractionTests
{
    [Fact]
    public async Task IsolatedLeftClickRunsAfterConfiguredDoubleClickWindow()
    {
        var delay = new ControlledClickDelay();
        var arbiter = new TrayClickArbiter(delay, new FixedDoubleClickTimeProvider(TimeSpan.FromMilliseconds(250)));
        var actions = new List<TrayAction>();
        arbiter.ActionRaised += (_, action) => actions.Add(action);

        var pending = arbiter.HandleAsync(TrayNotification.LeftButtonUp);
        Assert.Empty(actions);
        Assert.Equal(TimeSpan.FromMilliseconds(250), delay.LastDelay);
        delay.Complete();
        await pending;

        Assert.Equal(new[] { TrayAction.TogglePopup }, actions);
    }

    [Fact]
    public async Task DoubleClickCancelsSingleClickAndShowsPopupExactlyOnce()
    {
        var delay = new ControlledClickDelay();
        var arbiter = new TrayClickArbiter(delay, new FixedDoubleClickTimeProvider(TimeSpan.FromMilliseconds(500)));
        var actions = new List<TrayAction>();
        arbiter.ActionRaised += (_, action) => actions.Add(action);

        var pending = arbiter.HandleAsync(TrayNotification.LeftButtonUp);
        await arbiter.HandleAsync(TrayNotification.LeftButtonDoubleClick);
        await arbiter.HandleAsync(TrayNotification.LeftButtonUp);
        await pending;

        Assert.Equal(new[] { TrayAction.ShowPopup }, actions);
    }

    [Fact]
    public async Task KeyboardAndRightClickAreImmediate()
    {
        var arbiter = new TrayClickArbiter(new ControlledClickDelay(), new FixedDoubleClickTimeProvider(TimeSpan.FromSeconds(1)));
        var actions = new List<TrayAction>();
        arbiter.ActionRaised += (_, action) => actions.Add(action);

        await arbiter.HandleAsync(TrayNotification.KeyboardActivate);
        await arbiter.HandleAsync(TrayNotification.RightButtonUp);

        Assert.Equal(new[] { TrayAction.TogglePopup, TrayAction.ShowMenu }, actions);
    }

    private sealed class FixedDoubleClickTimeProvider(TimeSpan value) : IDoubleClickTimeProvider
    {
        public TimeSpan GetDoubleClickTime() => value;
    }

    private sealed class ControlledClickDelay : IClickDelay
    {
        private TaskCompletionSource<bool>? _completion;

        public TimeSpan LastDelay { get; private set; }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            LastDelay = delay;
            _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete() => _completion!.TrySetResult(true);
    }
}


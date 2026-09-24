namespace MFTLib.Tests.TestSupport;

/// <summary>
///     One held step in a fake: <see cref="Entered" /> completes when the step reaches the gate,
///     and the step stays parked until <see cref="Release" />. A test uses the pair to order
///     another thread's work against the held step without sleeping.
/// </summary>
internal sealed class TestGate
{
    readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the held step has reached the gate.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    internal void MarkEntered() => _entered.TrySetResult();

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        return _released.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    ///     The blocking counterpart for a step that runs inside a synchronous callback. Bounded by
    ///     <see cref="FakeIndexWatchSource.HangGuard" /> so a test that never releases the gate
    ///     reports a failure instead of wedging the thread that entered it.
    /// </summary>
    internal void WaitForRelease()
    {
        _released.Task.Wait(FakeIndexWatchSource.HangGuard);
    }
}

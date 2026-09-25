using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 252: the token passed to <see cref="FileIndex.StopWatchingAsync" /> reaches the
///     source's teardown, so a source waiting on something outside the process (the broker's
///     acknowledgement) stops waiting when the caller's bound runs out, and waits for as long as it
///     takes when the caller sets none. The source here holds its teardown on a gate the test owns,
///     so every outcome is decided by the test, never by timing.
/// </summary>
[TestClass]
public class FileIndexWatchTeardownTokenTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StopWatchingAsync_CancelledDuringTheSourceTeardown_CancelsTheSourceTeardownToken()
    {
        var source = new TeardownGatedWatchSource();
        using var harness = new WatchHarness(source);
        await harness.Index.StartWatchingAsync(Token);

        using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stop = harness.Index.StopWatchingAsync(stopCancellation.Token);
        await source.TeardownEntered.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsFalse(stop.IsCompleted, "The stop finished while the source teardown was still waiting.");
        Assert.IsFalse(source.TeardownToken.IsCancellationRequested);

        await stopCancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => stop);
        await source.TeardownFinished.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsTrue(source.TeardownToken.IsCancellationRequested);

        // The pump has finished, so a later stop reclaims the session left in place.
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_WithAnUncancelledToken_LeavesTheSourceTeardownUnbounded()
    {
        var source = new TeardownGatedWatchSource();
        using var harness = new WatchHarness(source);
        await harness.Index.StartWatchingAsync(Token);

        var stop = harness.Index.StopWatchingAsync(CancellationToken.None);
        await source.TeardownEntered.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsFalse(stop.IsCompleted, "The stop finished while the source teardown was still waiting.");

        source.ReleaseTeardown();
        await stop.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsFalse(source.TeardownToken.IsCancellationRequested);
    }

    /// <summary>
    ///     Reports ready at once, yields nothing, and on the way out waits for
    ///     <see cref="ReleaseTeardown" /> bounded only by the teardown token it was started with.
    /// </summary>
    sealed class TeardownGatedWatchSource : IIndexWatchSource
    {
        readonly TaskCompletionSource _teardownEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _teardownFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _teardownRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TeardownEntered => _teardownEntered.Task;

        public Task TeardownFinished => _teardownFinished.Task;

        public CancellationToken TeardownToken { get; private set; }

        public void ReleaseTeardown()
        {
            _teardownRelease.TrySetResult();
        }

        public IAsyncEnumerable<WatchStreamItem> StartWatching(
            IReadOnlyList<IndexWatchTarget> targets, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("The index starts streams through the teardown-bounded overload.");
        }

        public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            Action reportStreamReady, CancellationToken teardownCancellationToken,
            CancellationToken cancellationToken)
        {
            TeardownToken = teardownCancellationToken;
            return StreamAsync(reportStreamReady, teardownCancellationToken, cancellationToken);
        }

        async IAsyncEnumerable<WatchStreamItem> StreamAsync(Action reportStreamReady,
            CancellationToken teardownCancellationToken, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reportStreamReady();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }
            finally
            {
                _teardownEntered.TrySetResult();
                await _teardownRelease.Task.WaitAsync(teardownCancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _teardownFinished.TrySetResult();
            }
        }

        public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

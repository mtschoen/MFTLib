using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.StopWatchingAsync" /> hands its token to the session's source as the
///     teardown token the stream was started with, so a source whose cleanup waits on something
///     outside the process stops waiting when the stop's caller gives up.
/// </summary>
[TestClass]
public class FileIndexWatchTeardownTokenTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StopWatchingAsync_TokenCancelled_CancelsTheSourcesTeardownWaitAndTheSessionCanBeReclaimed()
    {
        var source = new TeardownAwaitingWatchSource();
        using var harness = new WatchHarness(source);
        // Declared after the harness so it is disposed first, releasing any unbounded teardown
        // before the index's disposal waits for it.
        using var releaseTeardown = source;
        await harness.Index.StartWatchingAsync(Token).WaitAsync(FakeIndexWatchSource.HangGuard, Token);

        using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stop = harness.Index.StopWatchingAsync(stopCancellation.Token);
        await source.TeardownWaiting.Task.WaitAsync(FakeIndexWatchSource.HangGuard, Token);
        Assert.IsFalse(stop.IsCompleted, "The stop finished while its source's teardown was still waiting.");

        await stopCancellation.CancelAsync();
        await stop.WaitAsync(FakeIndexWatchSource.HangGuard, Token)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.IsTrue(stop.IsCanceled, $"The stop whose token was cancelled ended {stop.Status}.");
        await source.TeardownEnded.Task.WaitAsync(FakeIndexWatchSource.HangGuard, Token);

        await harness.Index.StopWatchingAsync(Token).WaitAsync(FakeIndexWatchSource.HangGuard, Token);
    }

    /// <summary>
    ///     Accepts only the teardown overload, reports ready at once, and after its stream is
    ///     cancelled waits in its cleanup for the teardown token, as the broker source waits for
    ///     the broker's acknowledgement.
    /// </summary>
    sealed class TeardownAwaitingWatchSource : IIndexWatchSource, IDisposable
    {
        public TaskCompletionSource TeardownWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TeardownEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The index must start this source with a teardown token.");
        }

        public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            Action reportStreamReady, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The index must start this source with a teardown token.");
        }

        public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            Action reportStreamReady, CancellationToken teardownCancellationToken,
            CancellationToken cancellationToken)
        {
            return StreamAsync(reportStreamReady, teardownCancellationToken, cancellationToken);
        }

        public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        async IAsyncEnumerable<WatchStreamItem> StreamAsync(Action reportStreamReady,
            CancellationToken teardownCancellationToken, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reportStreamReady();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                TeardownWaiting.TrySetResult();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                    teardownCancellationToken, _abandon.Token);
                await Task.Delay(Timeout.Infinite, wait.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (teardownCancellationToken.IsCancellationRequested)
                {
                    TeardownEnded.TrySetResult();
                }
            }

            yield break;
        }

        readonly CancellationTokenSource _abandon = new();

        /// <summary>Ends a teardown wait the index never bounded, so a failing test tears down instead of hanging.</summary>
        public void Dispose()
        {
            _abandon.Cancel();
            _abandon.Dispose();
        }
    }
}

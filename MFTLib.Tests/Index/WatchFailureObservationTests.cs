using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class WatchFailureObservationTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task BrokerFailure_DoesNotLeaveUnobservedInternalTasks(
        bool caughtUpFirst, bool requestLateWaits)
    {
        var marker = $"watch-failure-{Guid.NewGuid():N}";
        var count = await CountUnobservedAsync(marker,
            () => RunIndexScenario(marker, caughtUpFirst, requestLateWaits));
        Assert.AreEqual(0, count, "A library-owned watch notification task was unobserved.");
    }

    [TestMethod]
    public async Task BrokerReader_ReportsFailureWithoutAnUnobservedTask()
    {
        var marker = $"broker-only-{Guid.NewGuid():N}";
        Assert.AreEqual(0, await CountUnobservedAsync(marker,
            () => RunBrokerScenario(marker)));
    }

    [TestMethod]
    public async Task CollectionProbe_DetectsAnAbandonedFaultedTask()
    {
        var marker = $"collection-control-{Guid.NewGuid():N}";
        Assert.AreEqual(1, await CountUnobservedAsync(marker,
            () => CreateAbandonedTask(marker)));
    }

    // No completed async test-helper task may keep the source or index rooted in this frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] RunIndexScenario(string marker, bool caughtUpFirst, bool requestLateWaits)
    {
        return Task.Run(() => RunIndexScenarioAsync(marker, caughtUpFirst, requestLateWaits))
            .GetAwaiter().GetResult();
    }

    // The failure is a Stalled frame, the one channel-loss frame that carries its own text, so the
    // marker reaches the fault the index announces.
    static async Task<WeakReference[]> RunIndexScenarioAsync(
        string marker, bool caughtUpFirst, bool requestLateWaits)
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        using var harness = new WatchHarness(source, 'T');
        var index = harness.Index;
        using var timeout = new CancellationTokenSource(HostChannelHarness.HangGuard);
        var token = timeout.Token;
        var announced = new TaskCompletionSource<WatchFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFault(WatchFault fault) => announced.TrySetResult(fault);
        index.WatchFaulted += OnFault;
        try
        {
            // The scripted host answers the channel open and reads StartWatch before the start can return.
            var starting = index.StartWatchingAsync('T', token);
            await using var host = await broker.AcceptChannelAsync();
            Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(host))?.Kind);
            await starting;
            if (caughtUpFirst)
            {
                await HostChannelHarness.WriteFrameAsync(host, BrokerProtocol.WriteCaughtUp);
                await index.WaitForCatchUpAsync('T', token);
                Assert.AreEqual(WatchCatchUpState.CaughtUp, index.Drives.Single().Watch.CatchUpState);
            }

            await HostChannelHarness.WriteFrameAsync(host, writer => BrokerProtocol.WriteStalled(writer, marker));
            var fault = await announced.Task.WaitAsync(token);
            Assert.AreEqual(WatchFaultKind.Channel, fault.Kind);
            Assert.AreEqual('T', fault.DriveLetter);
            Assert.AreEqual(marker, fault.Exception.Message);
            var status = index.Drives.Single();
            Assert.AreEqual(WatchCatchUpState.Faulted, status.Watch.CatchUpState);
            Assert.AreEqual(marker, status.Watch.FailureMessage);

            if (requestLateWaits)
            {
                var late = await WatchDeduplicationTestSupport.ThrowsAsync<BrokerChannelLostException>(
                    () => index.WaitForCatchUpAsync('T', token));
                Assert.AreSame(fault.Exception, late);
                var lateBatch = (await index.WaitForCatchUpAsync(token)).Single();
                Assert.AreEqual(DriveOperationOutcome.Failed, lateBatch.Outcome);
                Assert.AreSame(fault.Exception, lateBatch.Failure);
            }

            var stopped = await WatchDeduplicationTestSupport.ThrowsAsync<BrokerChannelLostException>(
                () => index.StopWatchingAsync('T', token));
            Assert.AreSame(fault.Exception, stopped);
            return [new WeakReference(index), new WeakReference(source)];
        }
        finally
        {
            index.WatchFaulted -= OnFault;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] RunBrokerScenario(string marker)
    {
        return Task.Run(() => RunBrokerScenarioAsync(marker)).GetAwaiter().GetResult();
    }

    static async Task<WeakReference[]> RunBrokerScenarioAsync(string marker)
    {
        await using var broker = new ScriptedBroker();
        var process = broker.Process;
        var source = new BrokerIndexWatchSource(_ => Task.FromResult(process));
        var starting = source.StartAsync(new IndexWatchTarget('T', 7, 100), CancellationToken.None);
        await using var host = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await HostChannelHarness.ReadFrameAsync(host))?.Kind);
        await using var handle = await starting.WaitAsync(HostChannelHarness.HangGuard);
        var reader = handle.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await using var _ = reader.ConfigureAwait(false);
        var first = reader.MoveNextAsync().AsTask();
        await HostChannelHarness.WriteFrameAsync(host, writer => BrokerProtocol.WriteError(writer, 0, marker));

        var failure = await WatchDeduplicationTestSupport.ThrowsAsync<DriveWatchFaultException>(
            () => first.WaitAsync(HostChannelHarness.HangGuard));
        Assert.AreEqual('T', failure.DriveLetter);
        Assert.AreEqual(marker, failure.Message);
        return [new WeakReference(source)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] CreateAbandonedTask(string marker)
    {
        var task = Task.FromException(new InvalidOperationException(marker));
        return [new WeakReference(task)];
    }

    static async Task<int> CountUnobservedAsync(string marker, Func<WeakReference[]> runScenario)
    {
        var counter = new UnobservedCounter();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs arguments)
        {
            if (arguments.Exception.Flatten().InnerExceptions.Any(exception => exception.Message == marker))
            {
                counter.Increment();
                arguments.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var references = runScenario();
            var collectedPasses = 0;
            for (var attempt = 0; attempt < 400; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                collectedPasses = references.Any(reference => reference.IsAlive) ? 0 : collectedPasses + 1;
                if (collectedPasses == 3)
                {
                    return counter.Value;
                }

                await Task.Yield();
            }

            Assert.Fail("Scenario objects remained rooted; the collection assertion would be inconclusive.");
            return counter.Value;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    sealed class UnobservedCounter
    {
        int _count;

        public void Increment() => Interlocked.Increment(ref _count);

        public int Value => Volatile.Read(ref _count);
    }
}

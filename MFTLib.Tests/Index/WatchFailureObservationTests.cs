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

    static async Task<WeakReference[]> RunIndexScenarioAsync(
        string marker, bool caughtUpFirst, bool requestLateWaits)
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var source = new BrokerIndexWatchSource(broker.ConnectAsync);
        using var harness = new WatchHarness(source);
        var index = harness.Index;
        var announced = new TaskCompletionSource<WatchFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFault(WatchFault fault) => announced.TrySetResult(fault);
        index.WatchFaulted += OnFault;
        try
        {
            // The scripted pipe does not buffer, so the StartWatch frame has to be read before the
            // source can publish its stream and the start can report ready.
            var starting = index.StartWatchingAsync(broker.CancellationToken);
            var start = await broker.ReadFrameAsync();
            Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
            await starting;
            var epoch = broker.ArmEpochForDrive(start, 'T');
            if (caughtUpFirst)
            {
                await broker.WriteAsync(writer => BrokerProtocol.WriteCaughtUp(writer, "T", epoch));
                await index.WaitForCatchUpAsync('T', broker.CancellationToken);
                Assert.AreEqual(WatchCatchUpState.CaughtUp, index.Drives.Single().WatchCatchUp);
            }

            await broker.WriteAsync(writer => BrokerProtocol.WriteError(writer, "T", epoch, marker));
            var fault = await announced.Task.WaitAsync(broker.CancellationToken);
            Assert.AreEqual(WatchFaultKind.Source, fault.Kind);
            Assert.AreEqual('T', fault.DriveLetter);
            Assert.AreEqual(marker, fault.Exception.Message);
            var status = index.Drives.Single();
            Assert.AreEqual(WatchCatchUpState.Faulted, status.WatchCatchUp);
            Assert.AreEqual(marker, status.WatchFailureMessage);

            if (requestLateWaits)
            {
                var single = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => index.WaitForCatchUpAsync('T', broker.CancellationToken));
                var all = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => index.WaitForCatchUpAsync(broker.CancellationToken));
                Assert.AreSame(fault.Exception, single);
                Assert.AreSame(fault.Exception, all);
            }

            Assert.AreEqual(BrokerFrameKind.EndWatch, (await broker.ReadFrameAsync()).Kind);
            await broker.WriteAsync(BrokerProtocol.WriteEndWatchAck);
            var stopped = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => index.StopWatchingAsync(broker.CancellationToken));
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
        await using var broker = new ScriptedWatchBrokerHarness();
        var source = new BrokerIndexWatchSource(broker.ConnectAsync);
        await using var reader = source.StartWatching(
            [new IndexWatchTarget('T', 7, 100)], broker.CancellationToken).GetAsyncEnumerator();
        var first = reader.MoveNextAsync().AsTask();
        var start = await broker.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        await broker.WriteAsync(writer => BrokerProtocol.WriteError(
            writer, "T", broker.ArmEpochForDrive(start, 'T'), marker));
        Assert.IsTrue(await first);
        Assert.IsInstanceOfType<DriveWatchFailure>(reader.Current);
        var failure = (DriveWatchFailure)reader.Current;
        Assert.AreEqual('T', failure.DriveLetter);
        Assert.AreEqual(marker, failure.Exception.Message);
        var finished = reader.MoveNextAsync().AsTask();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await broker.ReadFrameAsync()).Kind);
        await broker.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        Assert.IsFalse(await finished);
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
            for (var attempt = 0; attempt < 40; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                collectedPasses = references.Any(reference => reference.IsAlive) ? 0 : collectedPasses + 1;
                if (collectedPasses == 3)
                {
                    return counter.Value;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25));
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

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostChannelTests
{
    static readonly UsnJournalCursor Tip = new(7, 300);

    [TestMethod]
    public async Task WatchChannel_StreamsBatchesAndCaughtUp_NoDriveFields()
    {
        var since = new UsnJournalCursor(7, 100);
        string? watchedDrive = null;
        UsnJournalCursor? watchedSince = null;
        var host = CreateHost(queryCursor: _ => Tip, watchDrive: Watch);
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', since);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual("C", watchedDrive);
        Assert.AreEqual(since, watchedSince);
        CollectionAssert.AreEqual(
            new[] { BrokerFrameKind.JournalBatch, BrokerFrameKind.JournalBatch, BrokerFrameKind.CaughtUp },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual(200L, frames[0].Cursor.NextUsn);
        Assert.AreEqual("a.txt", frames[0].Entries.Single().FileName);
        Assert.AreEqual(Tip, frames[1].Cursor);
        Assert.IsTrue(frames.All(frame => frame.Drive == null && frame.RequestId == 0));
        return;

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> Watch(string drive,
            UsnJournalCursor from, IBrokerOperationReporter operation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            watchedDrive = drive;
            watchedSince = from;
            await Task.Yield();
            operation.Processing("journal batch");
            yield return ([JournalEntryFactory.Create(20, 150, "a.txt")], new UsnJournalCursor(7, 200));
            yield return ([JournalEntryFactory.Create(21, 250, "b.txt")], Tip);
        }
    }

    [TestMethod]
    public async Task WatchChannel_SourceThrows_WritesErrorAndCloses()
    {
        var host = CreateHost(queryCursor: _ => Tip, watchDrive: FailingWatch);
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', default);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp, BrokerFrameKind.Error },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual("journal read failed", frames[1].Message);
        return;

        static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> FailingWatch(
            string drive, UsnJournalCursor since, IBrokerOperationReporter operation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("journal read failed");
            }

            yield break;
        }
    }

    [TestMethod]
    public async Task WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected()
    {
        var sources = new Dictionary<string, ScriptedWatch>
        {
            ["C"] = new(),
            ["D"] = new()
        };
        var host = CreateHost(queryCursor: _ => Tip, watchDrive: (drive, _, _, token) => sources[drive].RunAsync(token));
        await using var harness = new HostChannelHarness(host);
        var pipeC = await harness.OpenWatchChannelAsync('C', Tip);
        var pipeD = await harness.OpenWatchChannelAsync('D', Tip);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipeC))?.Kind);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipeD))?.Kind);
        await sources["C"].Entered.WaitAsync(HostChannelHarness.HangGuard);

        await pipeC.DisposeAsync();
        await sources["C"].Cancelled.WaitAsync(HostChannelHarness.HangGuard);
        sources["D"].Push(new UsnJournalCursor(7, 400));

        var batch = await HostChannelHarness.ReadFrameAsync(pipeD);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual(400L, batch?.Cursor.NextUsn);
        Assert.IsFalse(sources["D"].Cancelled.IsCompleted);
        await pipeD.DisposeAsync();
    }

    [TestMethod]
    public async Task ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod()
    {
        var clock = new FakeTimeProvider();
        var stubborn = new ScriptedWatch(ignoreCancellation: true);
        var cooperative = new ScriptedWatch();
        var host = CreateHost(queryCursor: _ => Tip, timeProvider: clock,
            watchDrive: (drive, _, _, token) => (drive == "C" ? stubborn : cooperative).RunAsync(token));
        var harness = new HostChannelHarness(host);
        try
        {
            await harness.OpenWatchChannelAsync('C', Tip);
            await harness.OpenWatchChannelAsync('D', Tip);
            await Task.WhenAll(stubborn.Entered, cooperative.Entered).WaitAsync(HostChannelHarness.HangGuard);
            var start = clock.GetUtcNow();

            await harness.CloseControlAsync();
            await cooperative.Cancelled.WaitAsync(HostChannelHarness.HangGuard);
            await stubborn.Cancelled.WaitAsync(HostChannelHarness.HangGuard);
            Assert.IsFalse(harness.Serve.IsCompleted, "A source still running must hold the session open.");

            await AdvanceUntilCompleteAsync(clock, harness.Serve, TimeSpan.FromSeconds(1));
            Assert.IsTrue(clock.GetUtcNow() - start >= JournalBrokerHost.ControlClosedGracePeriod);
        }
        finally
        {
            stubborn.Release();
            await harness.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound()
    {
        var clock = new FakeTimeProvider();
        var stubborn = new ScriptedWatch(ignoreCancellation: true);
        var cooperative = new ScriptedWatch();
        var host = CreateHost(queryCursor: _ => Tip, timeProvider: clock, queryVolumeInfo: _ => Volume,
            watchDrive: (drive, _, _, token) => (drive == "C" ? stubborn : cooperative).RunAsync(token));
        var harness = new HostChannelHarness(host, breakableControl: true);
        var hostControl = harness.BreakableControl!;
        try
        {
            await harness.OpenWatchChannelAsync('C', Tip);
            await harness.OpenWatchChannelAsync('D', Tip);
            await Task.WhenAll(stubborn.Entered, cooperative.Entered).WaitAsync(HostChannelHarness.HangGuard);

            hostControl.BreakPipe();
            await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 1, "C"));
            await hostControl.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);
            await Task.WhenAll(stubborn.Cancelled, cooperative.Cancelled).WaitAsync(HostChannelHarness.HangGuard);

            await AdvanceUntilCompleteAsync(clock, harness.Serve, TimeSpan.FromSeconds(1));
        }
        finally
        {
            stubborn.Release();
            await harness.DisposeAsync();
        }
    }

    // A watch source a test drives by hand: it reports entry, yields each pushed cursor as one
    // batch, and reports its cancellation. One that ignores cancellation keeps running until
    // released, the way a wedged native read would.
    sealed class ScriptedWatch(bool ignoreCancellation = false)
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Channel<UsnJournalCursor> _batches = Channel.CreateUnbounded<UsnJournalCursor>();

        public Task Entered => _entered.Task;

        public Task Cancelled => _cancelled.Task;

        public void Push(UsnJournalCursor cursor) => _batches.Writer.TryWrite(cursor);

        public void Release() => _released.TrySetResult();

        public async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> RunAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());
            _entered.TrySetResult();
            if (ignoreCancellation)
            {
                await _released.Task;
                yield break;
            }

            await foreach (var cursor in _batches.Reader.ReadAllAsync(cancellationToken))
            {
                yield return ([JournalEntryFactory.Create(30, cursor.NextUsn - 1, "c.txt")], cursor);
            }
        }
    }
}

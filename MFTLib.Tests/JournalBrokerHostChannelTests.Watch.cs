using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostChannelTests
{
    static readonly UsnJournalCursor Tip = new(7, 300);

    [TestMethod]
    public async Task WatchChannel_StreamsBatchesAndCaughtUp_NoVolumeFields()
    {
        var since = new UsnJournalCursor(7, 100);
        VolumeKey? watchedVolume = null;
        UsnJournalCursor? watchedSince = null;
        var host = BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ => Tip,
            WatchVolume = Watch,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', since);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(new VolumeKey('C'), watchedVolume);
        Assert.AreEqual(since, watchedSince);
        Assert.AreSequenceEqual(
            [BrokerFrameKind.JournalBatch, BrokerFrameKind.JournalBatch, BrokerFrameKind.CaughtUp], [.. frames.Select(frame => frame.Kind)]);
        Assert.AreEqual(200L, frames[0].Cursor.NextUsn);
        Assert.AreEqual("a.txt", frames[0].Entries.Single().FileName);
        Assert.AreEqual(Tip, frames[1].Cursor);
        Assert.IsTrue(frames.All(frame => frame.Volume == null && frame.RequestId == 0));
        return;

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> Watch(VolumeKey volume,
            UsnJournalCursor from, ISourceOperationReporter operation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            watchedVolume = volume;
            watchedSince = from;
            await Task.Yield();
            operation.Processing("journal batch");
            yield return ([JournalEntries.Create(20, 150, "a.txt")], new UsnJournalCursor(7, 200));
            yield return ([JournalEntries.Create(21, 250, "b.txt")], Tip);
        }
    }

    [TestMethod]
    public async Task WatchChannel_SourceThrows_WritesErrorAndCloses()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ => Tip,
            WatchVolume = FailingWatch,
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', default);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreSequenceEqual([BrokerFrameKind.CaughtUp, BrokerFrameKind.Error], [.. frames.Select(frame => frame.Kind)]);
        Assert.AreEqual("journal read failed", frames[1].Message);
        return;

        static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> FailingWatch(
            VolumeKey volume, UsnJournalCursor since, ISourceOperationReporter operation,
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
        var sources = new Dictionary<VolumeKey, ScriptedWatch>
        {
            ['C'] = new(),
            ['D'] = new()
        };
        var host = BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ => Tip,
            WatchVolume = (volume, _, _, token) => sources[volume].RunAsync(token),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host);
        var pipeC = await harness.OpenWatchChannelAsync('C', Tip);
        var pipeD = await harness.OpenWatchChannelAsync('D', Tip);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipeC))?.Kind);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipeD))?.Kind);
        await sources['C'].Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await pipeC.DisposeAsync();
        await sources['C'].Cancelled.WaitAsync(HangGuard, TestContext.CancellationToken);
        sources['D'].Push(new UsnJournalCursor(7, 400));

        var batch = await HostChannelHarness.ReadFrameAsync(pipeD);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual(400L, batch?.Cursor.NextUsn);
        Assert.IsFalse(sources['D'].Cancelled.IsCompleted);
        await pipeD.DisposeAsync();
    }

    [TestMethod]
    public async Task ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod()
    {
        var clock = new TimerSignalingClock();
        var stubborn = new ScriptedWatch(ignoreCancellation: true);
        var cooperative = new ScriptedWatch();
        var host = BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ => Tip,
            TimeProvider = clock,
            WatchVolume = (volume, _, _, token) => (volume.Letter == 'C' ? stubborn : cooperative).RunAsync(token),
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        var harness = new HostChannelHarness(host);
        try
        {
            await harness.OpenWatchChannelAsync('C', Tip);
            await harness.OpenWatchChannelAsync('D', Tip);
            await Task.WhenAll(stubborn.Entered, cooperative.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);
            var start = clock.GetUtcNow();

            await harness.CloseControlAsync();
            await cooperative.Cancelled.WaitAsync(HangGuard, TestContext.CancellationToken);
            await stubborn.Cancelled.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsFalse(harness.Serve.IsCompleted, "A source still running must hold the session open.");

            // The heartbeat sender's 5 second timer is the first; the drain's grace period is the second.
            await AdvanceWhenTimerExistsAsync(clock, BrokerLiveness.ControlClosedGracePeriod, 2, harness.Serve);
            Assert.IsGreaterThanOrEqualTo(BrokerLiveness.ControlClosedGracePeriod, clock.GetUtcNow() - start);
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
        var clock = new TimerSignalingClock();
        var stubborn = new ScriptedWatch(ignoreCancellation: true);
        var cooperative = new ScriptedWatch();
        var host = BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryCursor = _ => Tip,
            TimeProvider = clock,
            QueryVolumeInformation = _ => Volume,
            WatchVolume = (volume, _, _, token) => (volume.Letter == 'C' ? stubborn : cooperative).RunAsync(token)
        });
        var harness = new HostChannelHarness(host, breakableControl: true);
        var hostControl = harness.BreakableControl!;
        try
        {
            await harness.OpenWatchChannelAsync('C', Tip);
            await harness.OpenWatchChannelAsync('D', Tip);
            await Task.WhenAll(stubborn.Entered, cooperative.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);

            hostControl.BreakPipe();
            await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(1, 'C')));
            await hostControl.WriteFailureObserved.WaitAsync(HangGuard, TestContext.CancellationToken);
            await Task.WhenAll(stubborn.Cancelled, cooperative.Cancelled).WaitAsync(HangGuard, TestContext.CancellationToken);

            // The heartbeat sender's 5 second timer is the first; the drain's grace period is the second.
            await AdvanceWhenTimerExistsAsync(clock, BrokerLiveness.ControlClosedGracePeriod, 2, harness.Serve);
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

            try
            {
                await foreach (var cursor in _batches.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return ([JournalEntries.Create(30, cursor.NextUsn - 1, "c.txt")], cursor);
                }
            }
            finally
            {
                // Registered callbacks run last-in first-out, so the read's own wake-up can end this
                // iteration and dispose the registration before the callback above has run.
                if (cancellationToken.IsCancellationRequested)
                {
                    _cancelled.TrySetResult();
                }
            }
        }
    }
}

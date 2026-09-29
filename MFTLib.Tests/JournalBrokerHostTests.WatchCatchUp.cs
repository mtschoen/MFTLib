using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_WithNoBacklog_WritesCaughtUpBeforeTheFirstBatch()
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 100L),
            watchDrive: (_, _, _, _) => FiniteWatch([([WatchEntry()], new UsnJournalCursor(7UL, 110L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp, BrokerFrameKind.JournalBatch },
            frames.Select(frame => frame.Kind).ToArray());
    }

    [TestMethod]
    public async Task StartWatch_WithBacklog_WritesCaughtUpAfterTheBatchThatPassesTheTip()
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 150L),
            watchDrive: (_, _, _, _) => FiniteWatch(
            [
                ([WatchEntry()], new UsnJournalCursor(7UL, 140L)),
                ([WatchEntry()], new UsnJournalCursor(7UL, 160L))
            ]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(
            new[] { BrokerFrameKind.JournalBatch, BrokerFrameKind.JournalBatch, BrokerFrameKind.CaughtUp },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual(new UsnJournalCursor(7UL, 140L), frames[0].Cursor);
        Assert.AreEqual(new UsnJournalCursor(7UL, 160L), frames[1].Cursor);
    }

    [TestMethod]
    public async Task StartWatch_WhileBelowTheTip_WritesNoCaughtUp()
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 150L),
            watchDrive: (_, _, _, _) => FiniteWatch([([WatchEntry()], new UsnJournalCursor(7UL, 140L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        // The channel closes after its one batch, which proves no CaughtUp followed it.
        CollectionAssert.AreEqual(new[] { BrokerFrameKind.JournalBatch }, frames.Select(frame => frame.Kind).ToArray());
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursorSentinel_WritesCaughtUpImmediately()
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(8UL, 50L),
            watchDrive: (_, _, _, _) => FiniteWatch([]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', default);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp }, frames.Select(frame => frame.Kind).ToArray());
    }

    [TestMethod]
    public async Task StartWatch_QueriesTheJournalTipOncePerArm()
    {
        var queryCallCount = 0;
        var host = CreateWatchHost(
            queryCursor: _ =>
            {
                queryCallCount++;
                return new UsnJournalCursor(7UL, 150L);
            },
            watchDrive: (_, _, _, _) => FiniteWatch([([WatchEntry()], new UsnJournalCursor(7UL, 140L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, queryCallCount);
    }

    [TestMethod]
    public async Task StartWatch_TipQueryFailure_EndsTheDrivesStreamWithAnError()
    {
        var host = CreateWatchHost(
            queryCursor: _ => throw new UnauthorizedAccessException("Access is denied"),
            watchDrive: (_, _, _, _) => FiniteWatch([([WatchEntry()], new UsnJournalCursor(7UL, 110L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("Access is denied", frames[0].Message);
    }
}

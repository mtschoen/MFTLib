using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_StaleCachedCursor_EndsThatDrivesStreamWithARescanErrorAndTheOtherDriveKeepsStreaming()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(drive => drive == 'C'
            ? new JournalWindow(8UL, 0L, 1000L, 64L, 4096L)
            : null);
        var queryCallCount = 0;
        (UsnJournalEntry[], UsnJournalCursor)[] batchD = [([WatchEntry()], new UsnJournalCursor(2UL, 210L))];

        UsnJournalCursor QueryCursor(string drive)
        {
            Interlocked.Increment(ref queryCallCount);
            return drive == "C" ? new UsnJournalCursor(1UL, 999L) : new UsnJournalCursor(2UL, 500L);
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, IBrokerOperationReporter operation,
            CancellationToken cancellationToken)
        {
            if (drive == "C" && since.JournalIdentifier == 7UL)
            {
                throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
            }

            return LiveWatch(batchD, cancellationToken);
        }

        var host = CreateWatchHost(QueryCursor, WatchDrive);
        await using var harness = new HostChannelHarness(host);

        var pipeC = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var pipeD = await harness.OpenWatchChannelAsync('D', new UsnJournalCursor(2UL, 200L));
        var framesC = await HostChannelHarness.ReadToEndAsync(pipeC);

        var error = framesC.Single();
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        StringAssert.Contains(error.RequireMessage(), "7:100");
        StringAssert.Contains(error.RequireMessage(), "USN journal entries have been deleted; full rescan needed");
        StringAssert.Contains(error.RequireMessage(), "rescan");

        var batch = await HostChannelHarness.ReadFrameAsync(pipeD);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual(new UsnJournalCursor(2UL, 210L), batch?.Cursor);
        Assert.AreEqual(2, queryCallCount);
        await AssertControlStillServesAsync(harness);
    }

    [TestMethod]
    public async Task StartWatch_MidStreamThrows_EmitsErrorFrame_SessionContinues()
    {
        var queryCount = 0;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
        {
            queryCount++;
            return new JournalWindow(8UL, 200L, 1000L, 64L, 4096L);
        });
        var host = CreateWatchHost(
            queryCursor: _ => default,
            watchDrive: (_, _, _, _) => ThrowingWatchMidStream());
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.JournalBatch, BrokerFrameKind.Error },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual("journal wrapped mid-stream", frames[1].Message);
        await AssertControlStillServesAsync(harness);
        Assert.AreEqual(0, queryCount);
    }

    [TestMethod]
    public async Task StartWatch_StartupFailureThatIsNotAboutTheCursor_CarriesThePlainExceptionMessage()
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(1UL, 0L),
            watchDrive: (_, _, _, _) => throw new UnauthorizedAccessException("Access is denied"));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("Access is denied", frames[0].Message);
    }

    [TestMethod]
    public async Task StartWatch_StaleCachedCursor_NeverWatchesFromTheCurrentJournalPosition()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(drive => drive == 'C'
            ? new JournalWindow(8UL, 0L, 1000L, 64L, 4096L)
            : null);
        var queryCallCount = 0;
        var watchCallCount = 0;
        var host = CreateWatchHost(
            queryCursor: _ =>
            {
                queryCallCount++;
                return new UsnJournalCursor(1UL, 0L);
            },
            watchDrive: (_, since, _, cancellationToken) =>
            {
                watchCallCount++;
                return since == new UsnJournalCursor(7UL, 100L)
                    ? throw new InvalidOperationException("USN journal wrapped before the cached cursor")
                    : LiveWatch([([WatchEntry()], new UsnJournalCursor(8UL, 10L))], cancellationToken);
            });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        StringAssert.Contains(frames[0].RequireMessage(), "USN journal wrapped before the cached cursor");
        Assert.AreEqual(1, watchCallCount);
        Assert.AreEqual(1, queryCallCount);
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursorSentinel_StartupFailure_CarriesThePlainExceptionMessage()
    {
        const string failureMessage = "USN journal wrapped before watching could start";
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(8UL, 50L),
            watchDrive: (_, _, _, _) => throw new InvalidOperationException(failureMessage));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', default);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp, BrokerFrameKind.Error },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual(failureMessage, frames[1].Message);
    }

    [TestMethod]
    public async Task StartWatch_JournalIdMismatchOnTheCachedCursor_IsAlsoAStaleCursorError()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(drive => drive == 'C'
            ? new JournalWindow(8UL, 0L, 1000L, 64L, 4096L)
            : null);
        var queryCallCount = 0;
        var host = CreateWatchHost(
            queryCursor: _ =>
            {
                queryCallCount++;
                return new UsnJournalCursor(8UL, 50L);
            },
            watchDrive: (_, since, _, cancellationToken) => since.JournalIdentifier == 7UL
                ? throw new InvalidOperationException(
                    "Journal ID mismatch: the cached cursor refers to a journal that was recreated")
                : LiveWatch([([WatchEntry()], new UsnJournalCursor(8UL, 60L))], cancellationToken));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        var error = frames.Single();
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        StringAssert.Contains(error.RequireMessage(), "7:100");
        StringAssert.Contains(error.RequireMessage(), "Journal ID mismatch");
        StringAssert.Contains(error.RequireMessage(), "rescan");
        Assert.AreEqual(1, queryCallCount);
    }
}

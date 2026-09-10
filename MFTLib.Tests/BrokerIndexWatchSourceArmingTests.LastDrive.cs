using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     What an arm or a disarm that fails owes the consumer once it is the last thing the stream
///     was waiting for. Both take the drive out of the reader map before anything they await can
///     throw, so a failure can leave no reader to notice the stream is over, and an open merged
///     channel would then park an <c>await foreach</c> on a stream nothing can write to again.
///     Each test disarms first, so the drive provably has no reader by the time the call under
///     test fails and no reader path can be what ends the stream.
/// </summary>
public partial class BrokerIndexWatchSourceArmingTests
{
    [TestMethod]
    public async Task ArmDriveAsync_FailingWithNoOtherReaderLeft_EndsTheMergedStreamWithTheFailure()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);

        // Where a rescan leaves the drive for the whole of its scan: retired, awaiting a re-arm
        // that is the only thing left to put a reader back on this stream.
        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);

        await harness.BreakTransportAsync();
        var armFailure = await CaptureFailureAsync(
            () => source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken));

        var failure = (DriveWatchFailure)await ExpectItemAsync(firstItem, enumerator);
        Assert.AreEqual('C', failure.DriveLetter);
        Assert.AreSame(armFailure, failure.Exception);
        await AssertStreamEndedAsync(enumerator);
    }

    [TestMethod]
    public async Task DisarmDriveAsync_FailingWithNoOtherReaderLeft_EndsTheMergedStreamWithTheFailure()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);

        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);

        // Unlike a disarm that succeeds, a disarm that throws has no re-arm coming to restore the
        // drive, so it cannot leave the stream open waiting for one.
        await harness.BreakTransportAsync();
        var disarmFailure = await CaptureFailureAsync(
            () => source.DisarmDriveAsync('C', harness.CancellationToken));

        var failure = (DriveWatchFailure)await ExpectItemAsync(firstItem, enumerator);
        Assert.AreEqual('C', failure.DriveLetter);
        Assert.AreSame(disarmFailure, failure.Exception);
        await AssertStreamEndedAsync(enumerator);
    }

    /// <summary>Runs one per-drive call and hands back the failure it threw.</summary>
    static async Task<Exception> CaptureFailureAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertFailedException("The call was expected to fail on the broken transport.");
    }

    /// <summary>
    ///     A stream that ended itself runs its finally, and so its stop, inside the
    ///     <c>MoveNextAsync</c> that returns false. That stop needs no scripted handshake here:
    ///     the client swallows an <c>EndWatch</c> it cannot write and its demux ends on the same
    ///     end-of-file the broken transport gave it.
    /// </summary>
    static async Task AssertStreamEndedAsync(IAsyncEnumerator<WatchStreamItem> enumerator)
    {
        Assert.IsFalse(await enumerator.MoveNextAsync(),
            "The merged stream must end once its last drive has no reader left and none coming.");
        await enumerator.DisposeAsync();
    }
}

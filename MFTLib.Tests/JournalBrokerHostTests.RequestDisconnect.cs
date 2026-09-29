using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A client that closes its end of a pipe while a request is being answered. On a drive pipe the
// write that finds the client gone ends that channel quietly and leaves the session serving; on
// the control pipe it ends the session normally instead of throwing the broken pipe's
// IOException out of ServeAsync and the elevated broker child (MFTLib#199).
public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ScanChannelWriteHitsBrokenPipeMidScan_ChannelEndsQuietlyAndSessionContinues()
    {
        var scanParked = new TestGate();
        var host = ScanHost(
            scanDrive: (_, _, _, _, _) =>
            {
                scanParked.MarkEntered();
                scanParked.WaitForRelease();
                return [[ScanRecord(5, ".", 3)]];
            },
            queryVolumeInfo: _ => ControlVolume);
        var connector = new BreakableScanPipeConnector();
        await using var harness = new HostChannelHarness(host, new RowCountingSectionWriter(), connector.ConnectAsync);
        connector.Harness = harness;
        var pipe = await harness.OpenScanChannelAsync('C');

        // The scan is live over the healthy pipe: the armed cursor arrives, and the record source
        // then parks with the rest of the scan still ahead of it.
        Assert.AreEqual(BrokerFrameKind.Cursor, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
        await scanParked.Entered.WaitAsync(HostChannelHarness.HangGuard);

        // The client goes away mid-scan. Breaking the pipe before releasing the scan keeps the
        // disconnect deterministic: the next frame the host writes lands on the dead client end.
        var hostEnd = await connector.Connected.WaitAsync(HostChannelHarness.HangGuard);
        hostEnd.BreakPipe();
        scanParked.Release();
        await hostEnd.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await AssertControlStillAnswersAsync(harness);
        Assert.IsFalse(harness.Serve.IsCompleted, "One channel's dead pipe must not end the session.");
    }

    [TestMethod]
    public async Task ServeAsync_VolumeQueryReplyHitsBrokenPipe_SessionEndsNormally()
    {
        var host = ScanHost(queryVolumeInfo: _ => ControlVolume);
        await using var harness = new HostChannelHarness(host, breakableControl: true);
        // The client is already gone when its request arrives, so the VolumeInfo reply is the
        // first frame to land on the dead pipe.
        harness.BreakableControl!.BreakPipe();

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 1, "C"));
        await harness.BreakableControl.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        // ServeAsync completes rather than faulting with the broken pipe's IOException, which is
        // what killed the elevated broker child.
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task ServeAsync_GrowUsnJournalReplyHitsBrokenPipe_SessionEndsNormally()
    {
        var host = ScanHost(growUsnJournal: (_, maximumSize, allocationDelta) => new UsnJournalSettings
        {
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        });
        await using var harness = new HostChannelHarness(host, breakableControl: true);
        harness.BreakableControl!.BreakPipe();

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 1, "C", 0x08000000, 0x01000000));
        await harness.BreakableControl.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task ServeAsync_ControlReplyHitsBrokenPipeWhileWatchIsLive_StopsTheWatchChannel()
    {
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = ScanHost(
            watchDrive: (_, _, _, cancellationToken) => ParkedUntilStopped(watchStopped, cancellationToken),
            queryVolumeInfo: _ => ControlVolume);
        await using var harness = new HostChannelHarness(host, breakableControl: true);

        // The watch is armed and parked: the leading CaughtUp arrived over the healthy pipe
        // (the cursor sits at the journal tip) and no batch follows it.
        var watch = await harness.OpenWatchChannelAsync('C', ScanArmedCursor);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(watch))?.Kind);

        // A control request answers while the watch is still live, and its reply is what finds
        // the client gone.
        harness.BreakableControl!.BreakPipe();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 9, "C"));
        await harness.BreakableControl.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);

        // Ending the session on a disconnect must not leave the armed watch behind: it was
        // cancelled on the way out, not merely awaited.
        await watchStopped.Task.WaitAsync(HostChannelHarness.HangGuard);
    }

    // A watch that yields nothing: it parks until the session stops it and reports that stop, so
    // a test can see the channel was cancelled on the way out.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ParkedUntilStopped(
        TaskCompletionSource watchStopped, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            watchStopped.TrySetResult();
        }

        yield break;
    }

    // Connects the named in-memory pipe through a BrokenPipeStream on the host's end, so a test
    // can make one channel's writes fail while the control pipe stays healthy.
    sealed class BreakableScanPipeConnector
    {
        readonly TaskCompletionSource<BrokenPipeStream> _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostChannelHarness? Harness { get; set; }

        public Task<BrokenPipeStream> Connected => _connected.Task;

        public async Task<Stream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
        {
            var broken = new BrokenPipeStream(await Harness!.ConnectInMemoryAsync(pipeName, cancellationToken));
            _connected.TrySetResult(broken);
            return broken;
        }
    }
}

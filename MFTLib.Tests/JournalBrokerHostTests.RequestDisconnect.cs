using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The request/response half of the client-disconnect contract (MFTLib#199): a client that
// closes its end of the pipe while a request is being answered ends the session normally,
// the way the watch-path fix for MFTLib#196 already does, instead of throwing the broken
// pipe's IOException out of ServeAsync and the elevated broker child.
public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ServeAsync_ScanReplyHitsBrokenPipeMidScan_SessionEndsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var brokenServer = new BrokenPipeStream(serverSide);
        var scanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 100L),
            (_, _, cancellationToken) => ScanBlockedOnGate(scanGate.Task, cancellationToken),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteBrokerRequestAsync(clientSide,
            writer => BrokerProtocol.WriteArmAndScan(writer, "C:0:0:mftlib-scan-C"), cts.Token);

        var serveTask = host.ServeAsync(brokenServer, CreateSectionWriter(), false, cts.Token);

        // The scan is live over the healthy pipe: the armed cursor arrives, and the record
        // source then parks with the rest of the scan still ahead of it.
        var cursor = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.Cursor, cursor.Kind);

        // The client goes away mid-scan, the way a consumer closing its UI does. Breaking the
        // pipe before releasing the scan keeps the disconnect deterministic: the next frame
        // the host writes, a progress report or a completion frame, is the one that lands on
        // the dead client end.
        brokenServer.BreakPipe();
        scanGate.SetResult();
        await brokenServer.WriteFailureObserved.WaitAsync(cts.Token);
        await clientSide.DisposeAsync();

        // ServeAsync completes rather than faulting with the broken pipe's IOException, which
        // is what killed the elevated broker child.
        await serveTask.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task ServeAsync_VolumeQueryReplyHitsBrokenPipe_SessionEndsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // The client is already gone when its request arrives, so the VolumeInfo reply is the
        // first frame to land on the dead pipe.
        await using var brokenServer = new BrokenPipeStream(serverSide);
        brokenServer.BreakPipe();
        var host = new JournalBrokerHost(
            _ => default,
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            queryVolumeInfo: _ => new NtfsVolumeInformation(1024, 1024, 512, 4096, 1, 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteBrokerRequestAsync(clientSide,
            writer => BrokerProtocol.WriteQueryVolumes(writer, "C:0:0"), cts.Token);

        var serveTask = host.ServeAsync(brokenServer, CreateSectionWriter(), false, cts.Token);

        await brokenServer.WriteFailureObserved.WaitAsync(cts.Token);
        await clientSide.DisposeAsync();

        await serveTask.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task ServeAsync_GrowUsnJournalReplyHitsBrokenPipe_SessionEndsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var brokenServer = new BrokenPipeStream(serverSide);
        brokenServer.BreakPipe();
        var host = new JournalBrokerHost(
            _ => default,
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            growUsnJournal: (_, maximumSize, allocationDelta) => new UsnJournalSettings
            {
                MaximumSize = maximumSize,
                AllocationDelta = allocationDelta
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteBrokerRequestAsync(clientSide,
            writer => BrokerProtocol.WriteGrowUsnJournal(writer, "C", 0x08000000, 0x01000000), cts.Token);

        var serveTask = host.ServeAsync(brokenServer, CreateSectionWriter(), false, cts.Token);

        await brokenServer.WriteFailureObserved.WaitAsync(cts.Token);
        await clientSide.DisposeAsync();

        await serveTask.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task ServeAsync_EndWatchAckHitsBrokenPipe_SessionEndsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var brokenServer = new BrokenPipeStream(serverSide);
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 100L),
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))],
                cancellationToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cts.Token);

        var serveTask = host.ServeAsync(brokenServer, CreateSectionWriter(), false, cts.Token);

        // The watch is live over the healthy pipe: the leading CaughtUp (the armed cursor
        // sits at the journal tip) and one batch arrive, and the watch then parks.
        var caughtUp = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp.Kind);
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        // The client goes away and its EndWatch is the request still in flight: the watch
        // generation stops without writing, so the ack is the frame that lands on the dead
        // pipe.
        brokenServer.BreakPipe();
        await WriteBrokerRequestAsync(clientSide, writer => BrokerProtocol.WriteEndWatch(writer, 1), cts.Token);

        await brokenServer.WriteFailureObserved.WaitAsync(cts.Token);
        await clientSide.DisposeAsync();

        await serveTask.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task ServeAsync_ReplyHitsBrokenPipeWhileWatchIsLive_StopsTheWatchGeneration()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var brokenServer = new BrokenPipeStream(serverSide);
        var watchStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 100L),
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, cancellationToken) => ParkedWatch(watchStopped, cancellationToken),
            queryVolumeInfo: _ => new NtfsVolumeInformation(1024, 1024, 512, 4096, 1, 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cts.Token);

        var serveTask = host.ServeAsync(brokenServer, CreateSectionWriter(), false, cts.Token);

        // The watch is armed and parked: the leading CaughtUp arrived over the healthy pipe
        // and no batch follows it.
        var caughtUp = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp.Kind);

        // A foreground request answers while the watch is still live, and its reply is what
        // finds the client gone.
        brokenServer.BreakPipe();
        await WriteBrokerRequestAsync(clientSide,
            writer => BrokerProtocol.WriteQueryVolumes(writer, "C:0:0"), cts.Token);

        await brokenServer.WriteFailureObserved.WaitAsync(cts.Token);
        await clientSide.DisposeAsync();

        await serveTask.WaitAsync(cts.Token);

        // Ending the session on a disconnect must not leave the armed watch behind: it was
        // cancelled on the way out, not merely awaited.
        await watchStopped.Task.WaitAsync(cts.Token);
    }

    // Yields one batch, then parks until the test releases the gate, so a disconnect can be
    // ordered into the middle of a scan instead of racing it.
    static IEnumerable<IReadOnlyList<MftRecord>> ScanBlockedOnGate(Task scanGate,
        CancellationToken cancellationToken)
    {
        yield return [SampleRecord()];
        scanGate.Wait(cancellationToken);
        yield return [SampleRecord()];
    }

    // A watch that yields nothing: it parks until the session stops it and reports that stop,
    // so a test can see the generation was cancelled on the way out.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ParkedWatch(
        TaskCompletionSource watchStopped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
}

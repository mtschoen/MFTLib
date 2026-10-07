using System.Runtime.CompilerServices;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Shared members of the watch partials. Their catch-up tests install JournalCheckpointCheck's
// process-wide journal override and the diagnostics tests change BrokerDiagnostics' process-wide
// state, which is why the class (attributes on the main partial) runs serially.
public partial class JournalBrokerHostTests
{
    static readonly NtfsVolumeInformation WatchVolume = new(1024 * 1000, 1024);

    static JournalBrokerHost CreateWatchHost(
        UsnJournalCursorQuery? queryCursor = null,
        JournalBatchSource? watchDrive = null,
        UsnJournalCatchUpSource? readJournal = null,
        MftRecordBatchSource? scanDrive = null)
    {
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                queryCursor ?? (_ => new UsnJournalCursor(7, 1000)),
                scanDrive ?? ((_, _, _, _, _, _) => [[WatchRecord(5, ".", 3)]]),
                readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
                watchDrive,
                _ => WatchVolume),
            4);
    }

    static MftRecord WatchRecord(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }

    static UsnJournalEntry WatchEntry()
    {
        return JournalEntries.Create(100, 110, "a.txt", UsnReason.FileCreate | UsnReason.Close);
    }

    // Yields its batches and then stays open until cancelled, the way a live watch does.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> LiveWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Yields its batches and then completes normally, so the channel ends by itself.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FiniteWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches)
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ThrowingWatchMidStream()
    {
        yield return ([WatchEntry()], new UsnJournalCursor(7UL, 110L));
        await Task.Yield();
        throw new InvalidOperationException("journal wrapped mid-stream");
    }

    // Yields one batch, parks until the gate completes (the point where the test breaks the
    // pipe), yields a second batch whose frame write then fails, and finally blocks like a live
    // watch until cancelled.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> GatedWatch(
        (UsnJournalEntry[], UsnJournalCursor) firstBatch,
        Task secondBatchGate,
        (UsnJournalEntry[], UsnJournalCursor) secondBatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return firstBatch;
        await secondBatchGate.WaitAsync(cancellationToken);
        yield return secondBatch;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Yields one batch, parks until the gate completes, then faults for real. With the pipe
    // broken at the gate, the attempt to report the fault as an Error frame fails too.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FaultingAfterGate(
        (UsnJournalEntry[], UsnJournalCursor) firstBatch,
        Task faultGate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return firstBatch;
        await faultGate.WaitAsync(cancellationToken);
        throw new InvalidOperationException("journal wrapped mid-stream");
    }

    static async Task AssertControlStillServesAsync(HostChannelHarness harness)
    {
        var requestId = harness.NextRequestId();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, requestId, "C"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, reply.Kind);
        Assert.AreEqual(requestId, reply.RequestId);
        Assert.IsFalse(harness.Serve.IsCompleted, "The session must outlive one channel's failure.");
    }

    // Connects the named in-memory pipe of one drive through a BrokenPipeStream on the host's
    // end, so a test can make that drive's writes fail while every other drive's pipe stays healthy.
    sealed class BreakableDrivePipe(char drive, bool brokenOnConnect = false)
    {
        readonly TaskCompletionSource<BrokenPipeStream> _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostChannelHarness? Harness { get; set; }

        public Task<BrokenPipeStream> Connected => _connected.Task;

        public async Task<Stream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
        {
            var stream = await Harness!.ConnectInMemoryAsync(pipeName, cancellationToken);
            if (!pipeName.StartsWith($"harness-{drive}-", StringComparison.Ordinal))
            {
                return stream;
            }

            var broken = new BrokenPipeStream(stream);
            if (brokenOnConnect)
            {
                broken.BreakPipe();
            }

            _connected.TrySetResult(broken);
            return broken;
        }
    }
}

namespace MFTLib;

// A stop whose acknowledgement wait is cancelled cancels the demux, but a demux already inside a
// frame reads that frame whole (see ReadFrameAsync), and a broker that stalls mid-frame may never
// send the rest. The stop does not wait for it: it retires the reader here and returns. Every later
// pipe reader waits for the retired one first, within its own bound, so no read can start in the
// middle of that frame; a bound that runs out first fails the connection instead.
public sealed partial class JournalBrokerClient
{
    // Guarded by _liveChannelsLock.
    Task? _retiredReader;

    void RetireReader(Task reader, CancellationTokenSource readerCancellation)
    {
        lock (_liveChannelsLock)
        {
            _retiredReader = FinishRetiredReaderAsync(reader, readerCancellation);
        }
    }

    async Task FinishRetiredReaderAsync(Task reader, CancellationTokenSource readerCancellation)
    {
        // DemuxLoopAsync never faults.
        await reader.ConfigureAwait(false);
        readerCancellation.Dispose();
        lock (_liveChannelsLock)
        {
            // The stop already reset the live-watch state; the reader's own ending latched it again.
            // A reader that ended on broker death keeps the latch, since that death is still true.
            if (Volatile.Read(ref _controlFailure) == null && Volatile.Read(ref _brokerDeathSignaled) == 0)
            {
                _liveEnded = false;
                _liveEndError = null;
            }
        }
    }

    // Called with the arm-ordering gate held, before anything reads the pipe.
    async Task AwaitRetiredReaderAsync(CancellationToken cancellationToken)
    {
        Task? retired;
        lock (_liveChannelsLock)
        {
            retired = _retiredReader;
        }
        if (retired == null)
        {
            return;
        }

        try
        {
            await retired.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposeStarted) == 0)
        {
            // The frame did not finish within this caller's bound, and nothing may read from its
            // middle. Failing the connection also cancels the retired reader's read.
            AbortControlExchange(new InvalidOperationException(
                "A cancelled watch stop left the broker pipe inside a frame that did not finish arriving."));
            throw;
        }

        lock (_liveChannelsLock)
        {
            if (ReferenceEquals(_retiredReader, retired))
            {
                _retiredReader = null;
            }
        }
    }

    // Disposal has already cancelled the control token, which ends a read inside a frame.
    Task JoinRetiredReaderAsync()
    {
        lock (_liveChannelsLock)
        {
            return _retiredReader ?? Task.CompletedTask;
        }
    }
}

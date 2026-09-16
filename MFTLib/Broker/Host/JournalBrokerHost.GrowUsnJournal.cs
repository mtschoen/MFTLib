namespace MFTLib;

public sealed partial class JournalBrokerHost
{
    // Mirrors HandleQueryVolumesAsync's contract: exactly one reply per request, tagged
    // NoArmEpoch - a UsnJournalSettings frame on success, an Error frame carrying the
    // refusal (grow-only) or OS failure message otherwise. Cancellation is not a
    // per-drive error and propagates to end the session.
    async Task HandleGrowUsnJournalAsync(
        Stream stream, string drive, long maximumSize, long allocationDelta,
        SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        if (_growUsnJournal == null)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, BrokerFrame.NoArmEpoch,
                        "Broker has no journal grow source"),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var settings = _growUsnJournal(drive, maximumSize, allocationDelta);
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteUsnJournalSettings(
                        writer, drive, settings.MaximumSize, settings.AllocationDelta),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, BrokerFrame.NoArmEpoch,
                        exception.Message),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

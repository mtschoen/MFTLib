namespace MFTLib;

/// <summary>Answers volume sizing queries without arming a scan or opening a block section.</summary>
public sealed partial class JournalBrokerHost
{
    async Task HandleQueryVolumesAsync(
        Stream stream, string drivesSpec, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        foreach (var request in ParseScanSpec(drivesSpec)) // volume-query tokens omit the section and profile
        {
            if (_queryVolumeInfo == null)
            {
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter, BrokerFrame.NoArmEpoch,
                            "Broker has no volume information source"),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                var info = _queryVolumeInfo(request.Letter);
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteVolumeInfo(
                            writer, request.Letter, info.MftRecordCount, info.BytesPerFileRecordSegment,
                            info.MftValidDataLength),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            // Deliberate per-drive boundary, matching HandleArmAndScanAsync: one drive's
            // query failure (access denied, volume closed) becomes an Error frame for
            // that drive, and the remaining drives are still queried. Cancellation is not
            // a per-drive error and propagates to end the session.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter, BrokerFrame.NoArmEpoch, exception.Message),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

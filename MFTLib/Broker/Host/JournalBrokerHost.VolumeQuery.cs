namespace MFTLib;

// QueryVolumes handling: answers one VolumeInfo (or Error) frame per requested drive
// without arming a scan or touching any shared-memory map. See JournalBrokerHost.Scan.cs
// for the sibling ArmAndScan handler and ParseScanSpec, which this reuses.
public sealed partial class JournalBrokerHost
{
    async Task HandleQueryVolumesAsync(
        Stream stream, string drivesSpec, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        foreach (var request in ParseScanSpec(drivesSpec)) // volume-query tokens omit the map name, like watch tokens
        {
            if (queryVolumeInfo == null)
            {
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter,
                            "Broker has no volume information source"),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                var info = queryVolumeInfo(request.Letter);
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
                        writer => BrokerProtocol.WriteError(writer, request.Letter, exception.Message),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

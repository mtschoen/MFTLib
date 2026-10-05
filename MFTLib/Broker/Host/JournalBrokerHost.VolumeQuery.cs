namespace MFTLib;

/// <summary>Answers one volume sizing query without arming a scan or opening a block section.</summary>
internal sealed partial class JournalBrokerHost
{
    // Exactly one reply per request, carrying its id: VolumeInfo on success, Error carrying the
    // failure (access denied, volume closed) otherwise. A reply that cannot reach the client
    // throws ClientDisconnectedException, which ends the session.
    async Task HandleQueryVolumeAsync(ControlSession session, BrokerFrame request)
    {
        var requestId = request.RequestId;
        if (_queryVolumeInfo == null)
        {
            await WriteControlErrorAsync(session, requestId, "Broker has no volume information source")
                .ConfigureAwait(false);
            return;
        }

        NtfsVolumeInformation info;
        try
        {
            info = _queryVolumeInfo(BrokerDriveLetter.Normalize(request.RequireDrive()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteControlErrorAsync(session, requestId, exception.Message).ConfigureAwait(false);
            return;
        }

        await WriteControlFrameAsync(session, writer => BrokerProtocol.WriteVolumeInfo(writer, requestId,
            info.BytesPerFileRecordSegment, info.MftValidDataLength)).ConfigureAwait(false);
    }
}

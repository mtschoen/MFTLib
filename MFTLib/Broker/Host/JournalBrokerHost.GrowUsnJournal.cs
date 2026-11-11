
namespace MFTLib;

/// <summary>Grows one volume's USN journal in place.</summary>
sealed partial class JournalBrokerHost
{
    // Exactly one reply per request, carrying its id: UsnJournalGrowthResult with the classified
    // growth result on success, Error carrying the argument or OS failure otherwise.
    async Task HandleGrowUsnJournalAsync(ControlSession session, BrokerFrame request)
    {
        var requestId = request.RequestId;
        UsnJournalGrowthResult growth;
        try
        {
            growth = _volumeAccess.GrowUsnJournal(
                request.RequireVolume(),
                request.JournalMaximumSize,
                request.JournalAllocationDelta,
                request.JournalAllowAboveMaximumRecommendedSize);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteControlErrorAsync(session, requestId, exception.Message).ConfigureAwait(false);
            return;
        }

        await WriteControlFrameAsync(session,
                writer => BrokerProtocol.WriteUsnJournalGrowthResult(writer, BrokerFrame.UsnJournalGrowthResult(requestId, growth)))
            .ConfigureAwait(false);
    }
}


namespace MFTLib;

/// <summary>Grows one drive's USN journal in place.</summary>
internal sealed partial class JournalBrokerHost
{
    // Exactly one reply per request, carrying its id: UsnJournalSettings on success, Error
    // carrying the refusal (grow only) or OS failure otherwise.
    async Task HandleGrowUsnJournalAsync(ControlSession session, BrokerFrame request)
    {
        var requestId = request.RequestId;
        if (_growUsnJournal == null)
        {
            await WriteControlErrorAsync(session, requestId, "Broker has no journal grow source")
                .ConfigureAwait(false);
            return;
        }

        UsnJournalSettings settings;
        try
        {
            settings = _growUsnJournal(BrokerDriveLetter.Normalize(request.RequireDrive()),
                request.JournalMaximumSize, request.JournalAllocationDelta);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteControlErrorAsync(session, requestId, exception.Message).ConfigureAwait(false);
            return;
        }

        await WriteControlFrameAsync(session, writer => BrokerProtocol.WriteUsnJournalSettings(writer, requestId,
            settings.MaximumSize, settings.AllocationDelta)).ConfigureAwait(false);
    }
}

using MFTLib.Index;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    ///     Ask the elevated broker to grow one drive's USN change journal in place. Grow
    ///     only: the host refuses a <paramref name="maximumSize" /> at or below the current
    ///     maximum, and that refusal (or any OS failure) reaches the caller as an
    ///     <see cref="InvalidOperationException" /> carrying the host's message. On success
    ///     returns the post-change settings the host read back from the volume, which can
    ///     round up past the request to an allocation-delta multiple. Runs beside live
    ///     watches on the other drives, serialized with the client's other reply-bearing
    ///     operations. MFTLib never calls this by itself: resizing a journal is a
    ///     persistent change to a resource shared with Windows Search, backup agents, and
    ///     replication, so it belongs behind an explicit consumer action. The caller supplies
    ///     a maximum derived from its own retention target and cap. MFTLib never initiates
    ///     growth automatically.
    /// </summary>
    public Task<UsnJournalSettings> GrowUsnJournalAsync(
        char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSize, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(allocationDelta, 0L);
        return RunControlExchangeAsync(
            (exchange, token) => GrowUsnJournalCoreAsync(driveLetter, maximumSize, allocationDelta, exchange, token),
            cancellationToken);
    }

    async Task<UsnJournalSettings> GrowUsnJournalCoreAsync(
        char driveLetter, long maximumSize, long allocationDelta,
        ControlExchange exchange, CancellationToken cancellationToken)
    {
        var drive = NormalizeDriveLetter(driveLetter.ToString());
        ExpectControlReplies(exchange, frame =>
            (frame.Kind == BrokerFrameKind.UsnJournalSettings ||
             (frame.Kind == BrokerFrameKind.Error && frame.ArmEpoch == BrokerFrame.NoArmEpoch)) &&
            string.Equals(frame.RequireDrive(), drive, StringComparison.OrdinalIgnoreCase));
        await WriteFrameAsync(
            writer => BrokerProtocol.WriteGrowUsnJournal(writer, drive, maximumSize, allocationDelta),
            () => exchange.RequestInFlight = true,
            cancellationToken).ConfigureAwait(false);

        // The host emits exactly one UsnJournalSettings or Error frame per request (see
        // JournalBrokerHost.HandleGrowUsnJournalAsync), so one read always settles this.
        var frame = await ReadControlFrameAsync(exchange, cancellationToken).ConfigureAwait(false);
        exchange.RequestInFlight = false;
        if (frame == null)
        {
            throw new InvalidOperationException(
                $"Broker disconnected before the journal grow request for drive {drive} completed.");
        }

        if (frame.Value.Kind == BrokerFrameKind.Error)
        {
            throw new InvalidOperationException(frame.Value.RequireMessage());
        }

        return new UsnJournalSettings
        {
            MaximumSize = frame.Value.JournalMaximumSize,
            AllocationDelta = frame.Value.JournalAllocationDelta
        };
    }
}

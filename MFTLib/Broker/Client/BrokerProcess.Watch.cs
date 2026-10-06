using MFTLib.Index;

namespace MFTLib;

/// <summary>Opens the drive pipe one drive's live watch runs on.</summary>
internal sealed partial class BrokerProcess
{
    // The watch owns its pipe from the moment the channel is open: a start that fails or is
    // cancelled closes the pipe in OpenChannelAsync, and the host reads that EOF as the end of the
    // watch. Nothing is shared with any other drive's channel.
    internal async Task<BrokerWatchChannel> OpenWatchChannelAsync(IndexWatchTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var since = new UsnJournalCursor(target.JournalId, target.NextUsn);
        var channel = await OpenChannelAsync(target.DriveLetter, writer => BrokerProtocol.WriteStartWatch(writer, since),
            cancellationToken).ConfigureAwait(false);
        return new BrokerWatchChannel(channel);
    }
}

using System.Runtime.CompilerServices;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     One volume's live watch on its own broker pipe. Reading it decodes the host's frames one at a
///     time straight off the pipe, with no queue between them, so a volume that is not being read
///     holds back only its own pipe. The index reads it from one pump and disposes it once.
/// </summary>
sealed class BrokerWatchChannel(BrokerVolumeChannel channel) : IIndexVolumeWatch
{
    VolumeKey Volume { get; } = channel.Volume;

    /// <summary>
    ///     Yields the volume's batches and its caught-up marker. The host's <c>Error</c> frame throws
    ///     <see cref="VolumeWatchFaultException" />; a stall report, an end of the pipe, a failed read
    ///     or a frame that has no place in a watch throws <see cref="BrokerChannelLostException" />.
    ///     Cancelling <paramref name="cancellationToken" /> ends a pending read without disposing
    ///     the channel.
    /// </summary>
    public async IAsyncEnumerable<WatchStreamItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await channel.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new BrokerChannelLostException(Volume.Letter,
                            $"Volume {Volume} watch channel was closed by the broker.");
            switch (frame.Kind)
            {
                case BrokerFrameKind.Heartbeat:
                    break;

                case BrokerFrameKind.JournalBatch:
                    yield return new JournalBatch(frame.Entries, frame.Cursor.JournalIdentifier, frame.Cursor.NextUsn);
                    break;

                case BrokerFrameKind.CaughtUp:
                    yield return new VolumeCaughtUp();
                    break;

                case BrokerFrameKind.Error:
                    throw new VolumeWatchFaultException(frame.RequireMessage());

                default:
                    throw new BrokerChannelLostException(Volume.Letter,
                        $"Volume {Volume} watch channel sent {frame.Kind}, which a watch does not carry.");
            }
        }
    }

    /// <summary>Closes the pipe, which the host reads as the end of the watch.</summary>
    public ValueTask DisposeAsync() => channel.DisposeAsync();
}

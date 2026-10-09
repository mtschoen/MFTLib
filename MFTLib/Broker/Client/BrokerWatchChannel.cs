using System.Runtime.CompilerServices;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     One drive's live watch on its own broker pipe. Reading it decodes the host's frames one at a
///     time straight off the pipe, with no queue between them, so a drive that is not being read
///     holds back only its own pipe. The index reads it from one pump and disposes it once.
/// </summary>
internal sealed class BrokerWatchChannel(BrokerDriveChannel channel) : IIndexDriveWatch
{
    public char DriveLetter { get; } = channel.DriveLetter;

    /// <summary>
    ///     Yields the drive's batches and its caught-up marker. The host's <c>Error</c> frame throws
    ///     <see cref="DriveWatchFaultException" />; a stall report, an end of the pipe, a failed read
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
                        ?? throw new BrokerChannelLostException(DriveLetter,
                            $"Drive {DriveLetter} watch channel was closed by the broker.");
            switch (frame.Kind)
            {
                case BrokerFrameKind.Heartbeat:
                    break;

                case BrokerFrameKind.JournalBatch:
                    yield return new JournalBatch(frame.Entries, frame.Cursor.JournalIdentifier, frame.Cursor.NextUsn);
                    break;

                case BrokerFrameKind.CaughtUp:
                    yield return new DriveCaughtUp();
                    break;

                case BrokerFrameKind.Error:
                    throw new DriveWatchFaultException(DriveLetter, frame.RequireMessage());

                default:
                    throw new BrokerChannelLostException(DriveLetter,
                        $"Drive {DriveLetter} watch channel sent {frame.Kind}, which a watch does not carry.");
            }
        }
    }

    /// <summary>Closes the pipe, which the host reads as the end of the watch.</summary>
    public ValueTask DisposeAsync() => channel.DisposeAsync();
}

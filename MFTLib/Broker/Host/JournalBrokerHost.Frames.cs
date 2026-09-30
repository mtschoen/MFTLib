using System.Buffers;

namespace MFTLib;

/// <summary>Frame reads and writes on the control pipe and the drive pipes.</summary>
public sealed partial class JournalBrokerHost
{
    // One drive pipe and what its operation needs to write to it. The pipe writer's lock keeps a
    // scan's progress pump, its operation and the heartbeat sender from interleaving frames on
    // this pipe only; the pipe writer is also the operation state the channel's source reports to.
    sealed class DriveChannel(Stream stream, string drive, HostPipeWriter pipe)
    {
        public Stream Stream { get; } = stream;
        public string Drive { get; } = drive;
        public string Tag => Pipe.Tag;
        public HostPipeWriter Pipe { get; } = pipe;
    }

    static Task WriteChannelFrameAsync(DriveChannel channel, Action<ArrayBufferWriter<byte>> write,
        CancellationToken cancellationToken)
    {
        return channel.Pipe.WriteFrameAsync(write, cancellationToken);
    }

    static Task WriteChannelErrorAsync(DriveChannel channel, string message, CancellationToken cancellationToken)
    {
        return WriteChannelFrameAsync(channel, writer => BrokerProtocol.WriteError(writer, 0, message),
            cancellationToken);
    }

    // Returns null on a clean EOF before any byte of a frame. A malformed frame, including a length
    // prefix no frame can have, throws InvalidDataException: on a drive pipe the first-request read
    // answers it with an Error frame and ends that channel, and on the control pipe it ends the
    // session.
    static Task<BrokerFrame?> ReadFrameAsync(Stream stream, string channelTag, CancellationToken cancellationToken)
    {
        return BrokerFrameStream.ReadFrameAsync(stream, channelTag, cancellationToken);
    }
}

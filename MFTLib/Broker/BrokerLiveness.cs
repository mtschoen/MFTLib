namespace MFTLib;

/// <summary>
///     The liveness limits of the broker wire. Known failures arrive as explicit frames; these
///     timers are the backstop for the ones nothing reports. Every limit is measured on an
///     injected <see cref="TimeProvider" />, so tests advance a fake clock.
/// </summary>
internal static class BrokerLiveness
{
    /// <summary>
    ///     How often the host's heartbeat sender visits each pipe that wrote nothing since its last
    ///     visit and writes a <see cref="BrokerFrameKind.Heartbeat" /> to an idle one. Enforced by
    ///     the host's heartbeat sender.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long a host operation may stay in its processing state without progress before the
    ///     host writes <see cref="BrokerFrameKind.Stalled" /> naming the step and cancels that channel.
    ///     Enforced by the host's heartbeat sender.
    /// </summary>
    public static readonly TimeSpan ProcessingLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long a client pipe may read no frame of any kind before the client closes it and
    ///     fails its operation with <see cref="BrokerChannelLostException" />; on the control pipe
    ///     that ends the process. Enforced by the client's frame reader.
    /// </summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the client waits on one control request: to finish writing a request frame it
    ///     has started, for a drive pipe to connect after its <see cref="BrokerFrameKind.ChannelOpened" />
    ///     reply, and for the reply itself. Enforced by <see cref="BrokerProcess" />.
    /// </summary>
    public static readonly TimeSpan ControlReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The journal buffers (64 KB each, so 16 MB) one catch-up read after a scan takes at most,
    ///     so a long backlog republishes the scan's progress per read instead of holding one step
    ///     for the whole backlog.
    /// </summary>
    public const int CatchUpBufferReadsPerCall = 256;
}

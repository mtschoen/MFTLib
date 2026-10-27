using System.Globalization;

namespace MFTLib;

/// <summary>
///     Broker timing defaults and diagnostic seconds formatting. Known failures arrive as
///     explicit frames; liveness limits bound failures nothing reports. Operations measure
///     their timers on an injected <see cref="TimeProvider" />.
/// </summary>
internal static class BrokerLiveness
{
    /// <summary>
    ///     How often the host's liveness loop visits every open stream to send a heartbeat or check
    ///     in-flight processing progress.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long an active host request may process without advancing its progress counter, or a
    ///     pipe write may remain in flight, before the host faults the operation.
    /// </summary>
    public static readonly TimeSpan ProcessingLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the client waits between incoming frames (data or heartbeat) on the control pipe
    ///     or a volume channel before treating the stream as stalled.
    /// </summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the client waits for a control request write, a control reply, or the host's
    ///     channel pipe connection after <c>ChannelOpened</c>.
    /// </summary>
    public static readonly TimeSpan ControlReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the host gives in-flight channel tasks to finish after the control pipe closes
    ///     before cancelling the session.
    /// </summary>
    public static readonly TimeSpan ControlClosedGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long the host waits for the client to connect to a newly opened channel pipe.
    /// </summary>
    public static readonly TimeSpan ChannelConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the host waits for the opening request frame on a connected channel pipe.
    /// </summary>
    public static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Default time the client waits for an elevated broker process to connect back to the
    ///     caller's control pipe during launch.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Default time the elevated <c>--broker</c> child spends trying to connect to the caller's
    ///     control pipe before aborting startup.
    /// </summary>
    public static readonly TimeSpan ElevatedConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long the elevated <c>--broker</c> child waits for its diagnostic log flush at exit.
    /// </summary>
    public static readonly TimeSpan DiagnosticsFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Default minimum interval between <c>ScanProgress</c> frames emitted by the host during a
    ///     root block scan.
    /// </summary>
    public static readonly TimeSpan ProgressThrottleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Formats a duration's total seconds using invariant culture for broker diagnostic messages.
    /// </summary>
    public static string FormatSeconds(TimeSpan duration, string format = "G") =>
        duration.TotalSeconds.ToString(format, CultureInfo.InvariantCulture);
}

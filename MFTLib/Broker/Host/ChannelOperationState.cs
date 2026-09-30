namespace MFTLib;

/// <summary>What one pipe's operation is doing, as its loop and its source last published it.</summary>
internal enum ChannelOperationKind
{
    /// <summary>Nothing is in flight: on the control pipe, the loop awaits a client request. Heartbeats on the control pipe.</summary>
    Idle,

    /// <summary>Blocked in a volume read with no time limit, such as a quiet journal. Heartbeats, never stalls.</summary>
    WaitingOnVolume,

    /// <summary>A scan waiting for the parse-thread budget to admit it. Heartbeats, never stalls.</summary>
    Queued,

    /// <summary>
    ///     Working on a named step. Heartbeats while its last progress is within
    ///     <see cref="BrokerLiveness.ProcessingLimit" />; stalls once no progress is published for that long.
    /// </summary>
    Processing
}

/// <summary>
///     One pipe's operation state and when its progress clock last restarted, on the host's clock.
///     Every publication and every frame write restarts the clock, so the time since
///     <see cref="Since" /> measures time without progress rather than time spent working.
/// </summary>
/// <param name="Kind">What the operation is doing.</param>
/// <param name="Step">The step a <see cref="ChannelOperationKind.Processing" /> operation works on; empty otherwise.</param>
/// <param name="Since">When the progress clock last restarted.</param>
internal readonly record struct ChannelOperationState(ChannelOperationKind Kind, string Step, DateTimeOffset Since);

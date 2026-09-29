namespace MFTLib;

/// <summary>What one pipe's operation is doing, as its loop and its source last published it.</summary>
internal enum BrokerOperationPhase
{
    /// <summary>Nothing is in flight: on the control pipe, the loop awaits a client request. Heartbeats.</summary>
    Idle,

    /// <summary>Blocked in a volume read with no time limit, such as a quiet journal. Heartbeats, never stalls.</summary>
    WaitingOnVolume,

    /// <summary>A scan waiting for the parse-thread budget to admit it. Heartbeats, never stalls.</summary>
    Queued,

    /// <summary>Working on a named step; stalls when no progress is published for too long.</summary>
    Processing
}

/// <summary>
///     Records one pipe's operation state and when it was published, on the host's clock. Every
///     publication restarts the clock, so the time since <see cref="Snapshot.Since" /> measures time
///     without progress rather than time spent working.
/// </summary>
internal sealed class BrokerOperationState(TimeProvider timeProvider) : IBrokerOperationReporter
{
    readonly Lock _gate = new();
    Snapshot _current = new(BrokerOperationPhase.Idle, null, timeProvider.GetTimestamp());

    internal readonly record struct Snapshot(BrokerOperationPhase Phase, string? Step, long Since);

    public Snapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void WaitingOnVolume()
    {
        Publish(BrokerOperationPhase.WaitingOnVolume, null);
    }

    public void Processing(string stepName)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        Publish(BrokerOperationPhase.Processing, stepName);
    }

    public void Queued()
    {
        Publish(BrokerOperationPhase.Queued, null);
    }

    void Publish(BrokerOperationPhase phase, string? step)
    {
        var now = timeProvider.GetTimestamp();
        lock (_gate)
        {
            _current = new Snapshot(phase, step, now);
        }
    }
}

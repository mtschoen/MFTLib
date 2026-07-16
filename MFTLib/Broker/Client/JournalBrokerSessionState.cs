namespace MFTLib;

/// <summary>
/// Lifecycle state of a <see cref="JournalBrokerScanSession"/>. A session begins
/// <see cref="Parked"/> after its initial scan and moves to <see cref="Watching"/>
/// once live watching starts, back to <see cref="Parked"/> when it stops.
/// <see cref="Faulted"/> latches once the broker dies; <see cref="Disposed"/> latches
/// once the session is disposed. Both terminal states reject every operation except
/// queries and disposal.
/// </summary>
public enum JournalBrokerSessionState
{
    Parked,
    Watching,
    Faulted,
    Disposed,
}

namespace MFTLibTestExtensions;

/// <summary>USN journal bounds and capacity limits used to simulate volume state in consumer tests.</summary>
/// <param name="JournalIdentifier">The journal instance identifier.</param>
/// <param name="FirstUsn">The earliest retained journal position.</param>
/// <param name="NextUsn">The next journal position to be written.</param>
/// <param name="AllocationDelta">The journal allocation increment in bytes.</param>
/// <param name="MaximumSize">The journal's configured maximum size in bytes.</param>
public readonly record struct SyntheticJournalWindow(
    ulong JournalIdentifier, long FirstUsn, long NextUsn, long AllocationDelta, long MaximumSize);

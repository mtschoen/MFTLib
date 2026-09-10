namespace MFTLib.Index;

/// <summary>Identifies which boundary raised a live watch fault.</summary>
public enum WatchFaultKind
{
    /// <summary>A <see cref="FileIndex.Changed" /> subscriber threw while receiving a batch.</summary>
    Subscriber,

    /// <summary>
    ///     A watch source failure. With a drive letter, that one drive's watch has ended and the
    ///     rest of the session continues without it. With a null drive letter, the merged stream
    ///     itself failed and the pump has ended.
    /// </summary>
    Source,

    /// <summary>
    ///     The index could not apply a batch. The block is unchanged, the drive's cursor was not
    ///     advanced, and that drive is dropped for the rest of the session.
    /// </summary>
    Apply
}

/// <summary>
///     A fault observed by the watch pump. <paramref name="DriveLetter" /> names the drive the
///     fault belongs to, whichever <see cref="WatchFaultKind" /> it carries, and is null only for
///     a <see cref="WatchFaultKind.Source" /> failure of the merged stream itself, which is
///     attributable to no single drive and ends the pump.
/// </summary>
public sealed record WatchFault(WatchFaultKind Kind, char? DriveLetter, Exception Exception);

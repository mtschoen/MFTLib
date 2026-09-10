namespace MFTLib.Index;

/// <summary>
///     One item on the merged stream an <see cref="IIndexWatchSource" /> yields: either a
///     <see cref="JournalBatch" /> to apply or a <see cref="DriveWatchFailure" /> saying that
///     one drive's watch has ended badly. A per-drive failure travels as data rather than as an
///     exception so that one drive's problem cannot end another drive's watch.
/// </summary>
public abstract record WatchStreamItem
{
    private protected WatchStreamItem()
    {
    }
}

/// <summary>
///     One drive's watch has failed and will deliver nothing further in this session. The index
///     stops applying that drive's batches and leaves its block header cursor where the last
///     successfully applied batch left it, so a later re-arm resumes from a cursor that is still
///     true.
/// </summary>
public sealed record DriveWatchFailure(char DriveLetter, Exception Exception) : WatchStreamItem;

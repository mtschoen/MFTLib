namespace MFTLib;

/// <summary>Producer-side record filter applied when writing cold-scan records into an index block.</summary>
/// <param name="Profile">Scan profile selecting which records receive block rows.</param>
/// <param name="KeepFileNames">Optional file names preserved under directory-index mode.</param>
/// <param name="IncludeFreed">Whether freed records receive rows; a freed record is otherwise dropped, whatever the source yielded.</param>
internal readonly record struct MftBlockRowFilter(
    BrokerScanProfile Profile,
    IReadOnlyCollection<string>? KeepFileNames = null,
    bool IncludeFreed = false)
{
    /// <summary>Unfiltered scan profile preserving all in-use MFT records and no freed ones.</summary>
    public static MftBlockRowFilter Full { get; } = new(BrokerScanProfile.Full);

    internal HashSet<string>? CreateKeepSet()
    {
        return Profile == BrokerScanProfile.DirectoryIndex && KeepFileNames is { Count: > 0 }
            ? new HashSet<string>(KeepFileNames, StringComparer.OrdinalIgnoreCase)
            : null;
    }
}

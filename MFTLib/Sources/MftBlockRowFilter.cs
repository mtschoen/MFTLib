namespace MFTLib;

/// <summary>Producer-side record filter applied when writing cold-scan records into an index block.</summary>
/// <param name="DirectoryScanFileNames">Null keeps all eligible rows; otherwise keeps directories plus matching file names.</param>
/// <param name="IncludeFreed">Whether freed records receive rows; a freed record is otherwise dropped, whatever the source yielded.</param>
internal readonly record struct MftBlockRowFilter(
    IReadOnlyCollection<string>? DirectoryScanFileNames = null,
    bool IncludeFreed = false)
{
    /// <summary>Unfiltered scan preserving all in-use MFT records and no freed ones.</summary>
    public static MftBlockRowFilter Full => default;

    internal HashSet<string>? CreateKeepSet()
    {
        return DirectoryScanFileNames is not null
            ? new HashSet<string>(DirectoryScanFileNames, StringComparer.OrdinalIgnoreCase)
            : null;
    }
}

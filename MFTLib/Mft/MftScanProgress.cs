namespace MFTLib;

/// <summary>
///     Progress sample emitted during an MFT scan.
/// </summary>
public readonly record struct MftScanProgress(
    MftScanPhase Phase,
    long RecordsScanned,
    long TotalRecords,
    TimeSpan Elapsed)
{
    public MftScanProgress(long recordsScanned, long totalRecords, TimeSpan elapsed)
        : this(MftScanPhase.Parsing, recordsScanned, totalRecords, elapsed)
    {
    }

    public void Deconstruct(
        out long recordsScanned,
        out long totalRecords,
        out TimeSpan elapsed)
    {
        recordsScanned = RecordsScanned;
        totalRecords = TotalRecords;
        elapsed = Elapsed;
    }
}

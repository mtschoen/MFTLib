using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>What a scan source is told, and how it reports back.</summary>
public sealed class ScriptedScan
{
    readonly BlockWriteReporting _reporting;

    internal ScriptedScan(DriveKey drive, ParseThreadAllowance parseThreads, BlockWriteReporting reporting,
        CancellationToken cancellationToken)
    {
        Drive = drive.Validated();
        ParseThreads = parseThreads;
        CancellationToken = cancellationToken;
        _reporting = reporting;
    }

    /// <summary>The drive the client asked to scan, as the host names it.</summary>
    public DriveKey Drive { get; }

    /// <summary>The scan's share of the host's parse threads.</summary>
    internal ParseThreadAllowance ParseThreads { get; }

    /// <summary>Cancelled when the client cancels the scan or its pipe closes.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    ///     Emits one parsing-phase progress frame, which the client reports as
    ///     <see cref="MFTLib.Index.IndexScanPhase.ParsingMft" />.
    /// </summary>
    /// <param name="recordsProcessed">Records parsed so far.</param>
    /// <param name="totalRecords">The expected total, or null when unknown.</param>
    public void ReportParsed(long recordsProcessed, long? totalRecords)
    {
        _reporting.Operation?.Processing("MFT parse");
        _reporting.Progress?.Report(new BlockWriteProgress(recordsProcessed, 0, totalRecords, null, BlockScanPhase.Parsing));
    }
}

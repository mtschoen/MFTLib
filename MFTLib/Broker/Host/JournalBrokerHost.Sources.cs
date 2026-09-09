namespace MFTLib;

/// <summary>Supplies native record batches without path resolution for the broker's block writer.</summary>
public sealed partial class JournalBrokerHost
{
    static IEnumerable<IReadOnlyList<MftRecord>> ScanDriveRecordBatches(
        string driveLetter, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        using var volume = MftVolume.Open(Bare(driveLetter));
        var mftProgress = CreateMftProgressAdapter(progress);
        foreach (var batch in volume.ReadRecordBatches(resolvePaths: false, 4096, mftProgress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = Array.FindAll(batch, record => record.InUse);
            if (records.Length > 0)
            {
                yield return records;
            }
        }
    }

    // Synchronous delivery preserves the parse total before the transfer phase reports its
    // smaller live-record count. The source feeds the per-drive progress reporter.
    static DirectProgress<MftScanProgress>? CreateMftProgressAdapter(IProgress<BlockWriteProgress>? progress)
    {
        return progress == null
            ? null
            : new DirectProgress<MftScanProgress>(value => progress.Report(new BlockWriteProgress(
                value.RecordsScanned, 0, value.TotalRecords, null,
                BrokerScanPhase.Parsing)));
    }
}

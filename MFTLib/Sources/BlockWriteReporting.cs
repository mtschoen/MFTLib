namespace MFTLib;

internal readonly record struct BlockWriteResult(
    long RowCount, long NamePoolUsedBytes, long SkippedRecordCount);

internal readonly record struct BlockWriteReporting(
    IProgress<BlockWriteProgress>? Progress, ISourceOperationReporter? Operation);

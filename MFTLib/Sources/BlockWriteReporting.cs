namespace MFTLib;

readonly record struct BlockWriteResult(
    long RowCount, long NamePoolUsedBytes, long SkippedRecordCount);

readonly record struct BlockWriteReporting(
    IProgress<BlockWriteProgress>? Progress, ISourceOperationReporter? Operation);

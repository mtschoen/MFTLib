using MFTLib.Index;

namespace MFTLib;

/// <summary>The caller owns Block after the scan returns; disposing the broker process does not release it.</summary>
public sealed record BlockScanOutcome(string SectionName, BlockFile Block, long RowCount, long NamePoolUsedBytes, long SkippedRecordCount);

using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The caller owns Block after the scan returns; disposing the broker process does not release it.
///     The row count and name pool size are in the block's own header.
/// </summary>
public sealed record BlockScanOutcome(BlockFile Block, long SkippedRecordCount);

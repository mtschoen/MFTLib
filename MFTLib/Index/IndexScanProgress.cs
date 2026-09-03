namespace MFTLib.Index;

/// <summary>Progress from a producer, shaped the same way for every producer.</summary>
public sealed record IndexScanProgress(uint RowsWritten, string CurrentDirectory);

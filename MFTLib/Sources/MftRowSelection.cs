namespace MFTLib;

/// <summary>Which records of a scan receive block rows: whether freed records count, and which files are retained.</summary>
/// <param name="ScanOptions">Freed-record inclusion.</param>
/// <param name="RetainedFileNames">Null keeps every eligible record; empty keeps directories only; otherwise directories plus files with these names.</param>
internal sealed record MftRowSelection(MftScanOptions ScanOptions, IReadOnlyCollection<string>? RetainedFileNames = null);

namespace MFTLib;

/// <summary>The destination of one drive's client-created packed block.</summary>
public sealed record BlockScanTarget(string Path, uint VolumeSerial, bool DeleteOnClose);

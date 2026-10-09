namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
/// <param name="DriveLetter">The drive the client scanned.</param>
/// <param name="DirectoryScanFileNames">Null for a full scan; otherwise directories plus these file names are retained.</param>
public sealed record InProcessBrokerScan(
    string DriveLetter, IReadOnlyCollection<string>? DirectoryScanFileNames);

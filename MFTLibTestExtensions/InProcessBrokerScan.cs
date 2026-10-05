using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
/// <param name="DriveLetter">The drive the client scanned.</param>
/// <param name="Profile">Which records the client asked the host to keep.</param>
/// <param name="KeepFileNames">The file names the client asked to keep under the directory-index profile, or null.</param>
public sealed record InProcessBrokerScan(
    string DriveLetter, BrokerScanProfile Profile, IReadOnlyCollection<string>? KeepFileNames);

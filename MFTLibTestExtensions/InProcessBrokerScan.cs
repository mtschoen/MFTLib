using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
/// <param name="Drive">The normalized drive key the client scanned.</param>
/// <param name="RetainedFileNames">Null for a full scan; otherwise directories plus these file names are retained.</param>
public sealed record InProcessBrokerScan(
    DriveKey Drive, IReadOnlyCollection<string>? RetainedFileNames)
{
    /// <summary>Identifies the host scan's volume; its constructor and init reject the default key.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); } = Drive.Validated();
}

using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
public sealed record InProcessBrokerScan
{
    internal InProcessBrokerScan(VolumeKey drive, IReadOnlyCollection<string>? retainedFileNames)
    {
        Drive = drive;
        RetainedFileNames = retainedFileNames;
    }

    /// <summary>Identifies the host scan's volume; its constructor and init reject the default key.</summary>
    public VolumeKey Drive { get; init => field = value.Validated(); }

    /// <summary>Null for a full scan; otherwise directories plus these file names are retained.</summary>
    public IReadOnlyCollection<string>? RetainedFileNames { get; init; }
}

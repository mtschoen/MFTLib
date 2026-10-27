using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
public sealed record InProcessBrokerScan
{
    internal InProcessBrokerScan(VolumeKey volume, IReadOnlyCollection<string>? retainedFileNames)
    {
        Volume = volume;
        RetainedFileNames = retainedFileNames;
    }

    /// <summary>Identifies the host scan's volume; its constructor and init reject the default key.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); }

    /// <summary>Null for a full scan; otherwise directories plus these file names are retained.</summary>
    public IReadOnlyCollection<string>? RetainedFileNames { get; init; }
}

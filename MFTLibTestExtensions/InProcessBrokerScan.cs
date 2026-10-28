using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One scan the in-process host served, as the client requested it.</summary>
public sealed record InProcessBrokerScan
{
    internal InProcessBrokerScan(VolumeKey volume)
    {
        Volume = volume;
    }

    /// <summary>Identifies the host scan's volume; its constructor and init reject the default key.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); }
}

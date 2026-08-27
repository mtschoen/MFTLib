namespace MFTLib;

/// <summary>
///     Result of <see cref="JournalBrokerClient.QueryVolumesAsync" />: per-drive volume
///     information for drives the broker could query, and per-drive error messages for
///     drives it could not. Both dictionaries key by bare drive letter.
/// </summary>
/// <remarks>
///     The <see cref="NtfsVolumeInformation" /> records in <see cref="Volumes" /> contain
///     the MFT sizing fields (<see cref="NtfsVolumeInformation.MftValidDataLength" />,
///     <see cref="NtfsVolumeInformation.BytesPerFileRecordSegment" />, and derived
///     <see cref="NtfsVolumeInformation.MftRecordCount" />) transmitted by the broker.
///     Cluster and sector geometry fields (<see cref="NtfsVolumeInformation.BytesPerSector" />,
///     <see cref="NtfsVolumeInformation.BytesPerCluster" />,
///     <see cref="NtfsVolumeInformation.TotalClusters" />, and
///     <see cref="NtfsVolumeInformation.FreeClusters" />) are not transmitted over the broker
///     protocol and are set to zero.
/// </remarks>
public sealed class NtfsVolumeQueryResult(
    IReadOnlyDictionary<string, NtfsVolumeInformation> volumes,
    IReadOnlyDictionary<string, string> errors)
{
    /// <summary>
    ///     Per-drive volume information for drives the broker successfully queried.
    ///     Only MFT sizing fields (<see cref="NtfsVolumeInformation.MftValidDataLength" />,
    ///     <see cref="NtfsVolumeInformation.BytesPerFileRecordSegment" />, and
    ///     <see cref="NtfsVolumeInformation.MftRecordCount" />) are populated; geometry
    ///     fields (<see cref="NtfsVolumeInformation.BytesPerSector" />,
    ///     <see cref="NtfsVolumeInformation.BytesPerCluster" />,
    ///     <see cref="NtfsVolumeInformation.TotalClusters" />,
    ///     <see cref="NtfsVolumeInformation.FreeClusters" />) are zero.
    /// </summary>
    public IReadOnlyDictionary<string, NtfsVolumeInformation> Volumes { get; } = volumes;

    /// <summary>Per-drive error messages for drives the broker could not query.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

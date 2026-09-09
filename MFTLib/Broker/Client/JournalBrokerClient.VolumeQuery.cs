namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    ///     Queries elevated-side NTFS volume information for each drive without arming a
    ///     scan or allocating a block section. The client uses this information to
    ///     size each destination before creating its section.
    ///     A drive the broker could not query appears in
    ///     <see cref="NtfsVolumeQueryResult.Errors" /> instead of
    ///     <see cref="NtfsVolumeQueryResult.Volumes" />; both are keyed by bare drive
    ///     letter.
    /// </summary>
    /// <remarks>
    ///     The broker wire protocol transmits only the fields required for MFT capacity
    ///     planning (<see cref="NtfsVolumeInformation.MftValidDataLength" />,
    ///     <see cref="NtfsVolumeInformation.BytesPerFileRecordSegment" />, and derived
    ///     <see cref="NtfsVolumeInformation.MftRecordCount" />). In the returned
    ///     <see cref="NtfsVolumeQueryResult.Volumes" /> entries, cluster and sector geometry
    ///     fields (<see cref="NtfsVolumeInformation.BytesPerSector" />,
    ///     <see cref="NtfsVolumeInformation.BytesPerCluster" />,
    ///     <see cref="NtfsVolumeInformation.TotalClusters" />, and
    ///     <see cref="NtfsVolumeInformation.FreeClusters" />) are set to zero. To obtain full
    ///     cluster geometry directly on Windows with administrator elevation, use
    ///     <see cref="NtfsVolumeInformation.Query(string)" />.
    /// </remarks>
    public Task<NtfsVolumeQueryResult> QueryVolumesAsync(
        IReadOnlyList<string> drives, CancellationToken cancellationToken = default)
    {
        return QueryVolumesAsync(drives, null, cancellationToken);
    }

    internal async Task<NtfsVolumeQueryResult> QueryVolumesAsync(
        IReadOnlyList<string> drives,
        Action? transmissionStarted,
        CancellationToken cancellationToken = default)
    {
        var normalizedDrives = drives.Select(NormalizeDriveLetter).ToArray();
        var drivesSpec = string.Join(
            ",", normalizedDrives.Select(letter => FormattableString.Invariant($"{letter}:0:0")));

        await WriteFrameAsync(
            writer => BrokerProtocol.WriteQueryVolumes(writer, drivesSpec),
            transmissionStarted,
            cancellationToken).ConfigureAwait(false);

        var volumes = new Dictionary<string, NtfsVolumeInformation>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new HashSet<string>(normalizedDrives, StringComparer.OrdinalIgnoreCase);

        // The host emits exactly one VolumeInfo or Error frame per requested drive (see
        // JournalBrokerHost.HandleQueryVolumesAsync), so this loop always terminates
        // without depending on EOF - the `frame == null` break below only guards a broker
        // death mid-exchange.
        while (remaining.Count > 0)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                foreach (var drive in remaining)
                {
                    errors[drive] = "Broker disconnected before volume query completed";
                }

                break;
            }

            switch (frame.Value.Kind)
            {
                case BrokerFrameKind.VolumeInfo:
                    {
                        var drive = frame.Value.RequireDrive();
                        // Reconstruct from the two raw fields rather than trusting the
                        // transmitted MftRecordCount directly, so the derivation stays
                        // centralized in NtfsVolumeInformation.MftRecordCount. Geometry
                        // fields (BytesPerSector, BytesPerCluster, TotalClusters, FreeClusters)
                        // are not transmitted over the wire protocol and are set to zero.
                        volumes[drive] = new NtfsVolumeInformation(
                            frame.Value.MftValidDataLength, frame.Value.BytesPerFileRecordSegment, 0, 0, 0, 0);
                        remaining.Remove(drive);
                        break;
                    }

                case BrokerFrameKind.Error:
                    {
                        var drive = frame.Value.RequireDrive();
                        errors[drive] = frame.Value.RequireMessage();
                        remaining.Remove(drive);
                        break;
                    }
            }
        }

        return new NtfsVolumeQueryResult(volumes, errors);
    }

}

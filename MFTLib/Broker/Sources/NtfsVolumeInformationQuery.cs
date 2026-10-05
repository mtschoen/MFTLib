namespace MFTLib;

/// <summary>
///     Production wires this to <c>NtfsVolumeInformation.Query</c> (read a live volume's
///     NTFS geometry and MFT sizing via <c>FSCTL_GET_NTFS_VOLUME_DATA</c>); tests inject a
///     fake so <see cref="JournalBrokerHost" /> can be exercised without a real elevated
///     volume handle.
/// </summary>
internal delegate NtfsVolumeInformation NtfsVolumeInformationQuery(string driveLetter);

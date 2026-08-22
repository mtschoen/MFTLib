namespace MFTLib;

/// <summary>
///     Production wires this to the elevated full-drive scan (walk the MFT and
///     return every record); tests inject a fake so <see cref="JournalBrokerHost" />
///     can be exercised without a real elevated volume handle.
/// </summary>
public delegate ScanRecord[] DriveScanSource(string driveLetter);

/// <summary>
///     Streaming variant of <see cref="DriveScanSource" /> that yields bounded batches
///     of <see cref="ScanRecord" />s directly from the MFT without building a full-drive array.
/// </summary>
public delegate IEnumerable<IReadOnlyList<ScanRecord>> StreamingDriveScanSource(
    string driveLetter, CancellationToken cancellationToken);

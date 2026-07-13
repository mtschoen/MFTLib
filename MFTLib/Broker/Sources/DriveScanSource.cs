namespace MFTLib;

/// <summary>
/// Production wires this to the elevated full-drive scan (walk the MFT and
/// return every record); tests inject a fake so <see cref="JournalBrokerHost"/>
/// can be exercised without a real elevated volume handle.
/// </summary>
public delegate ScanRecord[] DriveScanSource(string driveLetter);

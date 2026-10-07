namespace MFTLib;

/// <summary>The production volume seams <see cref="CreateDefault()" /> wires.</summary>
internal sealed partial class JournalBrokerHost
{
    /// <summary>Creates the host that scans, reads and watches real volumes through the native seams of MFTLib itself.</summary>
    /// <returns>A host whose volume access needs the elevation that raw volume handles require.</returns>
    public static JournalBrokerHost CreateDefault()
    {
        return CreateDefault(null);
    }

    /// <summary>The production host on a given clock, so a test can drive its timeouts.</summary>
    internal static JournalBrokerHost CreateDefault(TimeProvider? timeProvider)
    {
        return new JournalBrokerHost(
            new VolumeSources(
                LiveVolumeSources.QueryCursor,
                LiveVolumeSources.ScanDriveRecordBatches,
                LiveVolumeSources.ReadJournal,
                LiveVolumeSources.WatchAndDisposeAsync,
                LiveVolumeSources.QueryVolumeInfo,
                LiveVolumeSources.GrowUsnJournal),
            timeProvider: timeProvider);
    }
}

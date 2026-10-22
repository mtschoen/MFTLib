namespace MFTLib;

/// <summary>The production volume access <see cref="CreateDefault()" /> wires.</summary>
sealed partial class JournalBrokerHost
{
    /// <summary>Creates the host that scans, reads and watches real volumes through the live volume access of MFTLib itself.</summary>
    /// <returns>A host whose volume access needs the elevation that raw volume handles require.</returns>
    public static JournalBrokerHost CreateDefault()
    {
        return CreateDefault(null);
    }

    /// <summary>The production host on a given clock, so a test can drive its timeouts.</summary>
    internal static JournalBrokerHost CreateDefault(TimeProvider? timeProvider)
    {
        return new JournalBrokerHost(LiveVolumeAccess.Instance, timeProvider: timeProvider);
    }
}

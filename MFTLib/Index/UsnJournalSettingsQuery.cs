using System.Runtime.Versioning;

namespace MFTLib.Index;

/// <summary>
///     Reads one volume's USN change journal sizing via <c>FSCTL_QUERY_USN_JOURNAL</c>
///     against the unelevated volume-root handle <see cref="UsnJournalVolumeInterop" />
///     opens - the same query <c>fsutil usn queryjournal</c> performs unelevated.
/// </summary>
[SupportedOSPlatform("windows")]
static class UsnJournalSettingsQuery
{
    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs
    ///     one test at a time. Same shape as <c>IndexedDrive._volumeSerialReaderOverride</c>.
    /// </summary>
    internal static Func<char, UsnJournalSettings>? _queryOverride;

    public static UsnJournalSettings Query(char driveLetter)
    {
        if (_queryOverride is { } queryOverride)
        {
            return queryOverride(driveLetter);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USN journal settings queries require Windows (FSCTL_QUERY_USN_JOURNAL).");
        }

        using var handle = UsnJournalVolumeInterop.OpenVolumeRoot(
            driveLetter, "query its USN journal settings");
        return ToSettings(UsnJournalVolumeInterop.QueryJournalData(handle));
    }

    internal static UsnJournalSettings ToSettings(UsnJournalVolumeInterop.UsnJournalDataV0 data)
    {
        return new UsnJournalSettings
        {
            MaximumSize = checked((long)data.MaximumSize),
            AllocationDelta = checked((long)data.AllocationDelta)
        };
    }

    internal static IDisposable OverrideQueryForTest(Func<char, UsnJournalSettings> query)
    {
        var previous = _queryOverride;
        _queryOverride = query;
        return new RestoreQuery(previous);
    }

    sealed class RestoreQuery(Func<char, UsnJournalSettings>? previous) : IDisposable
    {
        public void Dispose()
        {
            _queryOverride = previous;
        }
    }
}

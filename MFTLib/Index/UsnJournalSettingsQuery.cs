using System.Runtime.Versioning;
using MFTLib.NativeInterop;

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
    internal static Func<DriveKey, UsnJournalSettings>? _queryOverride;

    public static UsnJournalSettings Query(DriveKey drive)
    {
        if (_queryOverride is { } queryOverride)
        {
            return queryOverride(drive);
        }

        return Query(drive, OperatingSystem.IsWindows());
    }

    /// <summary>
    ///     The real query behind <see cref="Query(DriveKey)" />, given whether the host is Windows, so
    ///     the refusal off Windows is tested on every platform. Depends only on its arguments and
    ///     the platform APIs; the test override is handled by <see cref="Query(DriveKey)" />.
    /// </summary>
    internal static UsnJournalSettings Query(DriveKey drive, bool isWindows)
    {
        if (!isWindows || !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USN journal settings queries require Windows (FSCTL_QUERY_USN_JOURNAL).");
        }

        using var handle = UsnJournalVolumeInterop.OpenVolumeRoot(
            drive, "query its USN journal settings");
        return UsnJournalVolumeInterop.QueryJournalData(handle).ToSettings();
    }

    internal static IDisposable OverrideQueryForTest(Func<DriveKey, UsnJournalSettings> query)
    {
        var previous = _queryOverride;
        _queryOverride = query;
        return new RestoreQuery(previous);
    }

    sealed class RestoreQuery(Func<DriveKey, UsnJournalSettings>? previous) : IDisposable
    {
        public void Dispose()
        {
            _queryOverride = previous;
        }
    }
}

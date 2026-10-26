using MFTLib.NativeInterop;

namespace MFTLib.Index;

/// <summary>
///     Reads one volume's USN change journal sizing via <c>FSCTL_QUERY_USN_JOURNAL</c>
///     against the unelevated volume-root handle <see cref="UsnJournalVolumeInterop" />
///     opens - the same query <c>fsutil usn queryjournal</c> performs unelevated.
/// </summary>
static class UsnJournalSettingsQuery
{
    internal static readonly QueryDependencies Dependencies = new();

    /// <summary>Process-global test dependencies for querying USN journal settings.</summary>
    internal sealed class QueryDependencies
    {
        internal Func<DriveKey, UsnJournalSettings>? _queryOverride;

        internal void ResetToDefaults() => _queryOverride = null;
    }

    public static UsnJournalSettings Query(DriveKey drive)
    {
        if (Dependencies._queryOverride is { } queryOverride)
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
}

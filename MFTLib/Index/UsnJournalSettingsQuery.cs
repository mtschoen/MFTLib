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
        internal Func<VolumeKey, UsnJournalSettings>? _queryOverride;

        internal void ResetToDefaults() => _queryOverride = null;
    }

    public static UsnJournalSettings Query(VolumeKey volume)
    {
        if (Dependencies._queryOverride is { } queryOverride)
        {
            return queryOverride(volume);
        }

        return Query(volume, OperatingSystem.IsWindows());
    }

    /// <summary>
    ///     The real query behind <see cref="Query(VolumeKey)" />, given whether the host is Windows, so
    ///     the refusal off Windows is tested on every platform. Depends only on its arguments and
    ///     the platform APIs; the test override is handled by <see cref="Query(VolumeKey)" />.
    /// </summary>
    internal static UsnJournalSettings Query(VolumeKey volume, bool isWindows)
    {
        if (!isWindows || !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USN journal settings queries require Windows (FSCTL_QUERY_USN_JOURNAL).");
        }

        using var handle = UsnJournalVolumeInterop.OpenVolumeRoot(
            volume, "query its USN journal settings");
        return UsnJournalVolumeInterop.QueryJournalData(handle).ToSettings();
    }
}

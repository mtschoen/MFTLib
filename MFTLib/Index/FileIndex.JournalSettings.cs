using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads the current USN change journal sizing of one drive in this index, without
    ///     elevation and without the broker: <c>FSCTL_QUERY_USN_JOURNAL</c> answers from a
    ///     backup-semantics handle on the volume root, the same query
    ///     <c>fsutil usn queryjournal</c> performs. Pair the result with
    ///     <see cref="UsnJournalSettings.IsBelowRecommended" /> to warn about a journal
    ///     that could wrap under load. Resizing stays an explicit, elevated call:
    ///     <c>JournalBrokerClient.GrowUsnJournalAsync</c>.
    /// </summary>
    /// <param name="driveLetter">A configured drive's letter, either case.</param>
    [SupportedOSPlatform("windows")]
    public UsnJournalSettings QueryUsnJournalSettings(char driveLetter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var letter = char.ToUpperInvariant(driveLetter);
        if (_options.Drives.All(drive => char.ToUpperInvariant(drive.DriveLetter) != letter))
        {
            throw new ArgumentException($"Drive {letter} is not part of this index.", nameof(driveLetter));
        }

        return UsnJournalSettingsQuery.Query(letter);
    }
}

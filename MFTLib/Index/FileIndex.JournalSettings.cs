using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads current sizing without elevation. Consumers own retention targets and sizing policy;
    ///     resizing is an explicit elevated broker operation.
    /// </summary>
    /// <param name="driveLetter">A configured drive's letter, either case.</param>
    [SupportedOSPlatform("windows")]
    public UsnJournalSettings QueryUsnJournalSettings(char driveLetter)
    {
        return UsnJournalSettingsQuery.Query(RequireConfiguredDrive(driveLetter));
    }

    char RequireConfiguredDrive(char driveLetter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var letter = char.ToUpperInvariant(driveLetter);
        if (_options.Drives.All(drive => char.ToUpperInvariant(drive.DriveLetter) != letter))
        {
            throw new ArgumentException($"Drive {letter} is not part of this index.", nameof(driveLetter));
        }

        return letter;
    }
}

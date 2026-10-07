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
        var letter = RequireConfiguredDrive(driveLetter);
        if (IsMftDumpDrive(letter))
        {
            throw new InvalidOperationException(
                MftIndexSource.FormatUnavailable(letter, MftIndexSource.NoJournalSettingsReason));
        }

        return UsnJournalSettingsQuery.Query(letter);
    }

    /// <summary>True when the drive's block comes from an MFT dump, whether or not the open produced one.</summary>
    bool IsMftDumpDrive(char driveLetter) =>
        _options.MftSource?.DumpIdentity is { } dump && dump.DriveLetter == char.ToUpperInvariant(driveLetter);

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

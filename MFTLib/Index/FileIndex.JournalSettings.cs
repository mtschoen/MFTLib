using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads current sizing without elevation. Consumers own retention targets and sizing policy;
    ///     resizing is an explicit elevated broker operation.
    /// </summary>
    /// <param name="driveLetter">A configured drive key. The default key is invalid.</param>
    [SupportedOSPlatform("windows")]
    public UsnJournalSettings QueryUsnJournalSettings(DriveKey driveLetter)
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
    bool IsMftDumpDrive(DriveKey driveLetter) =>
        _options.MftSource?.DumpIdentity is { } dump && dump.Drive == driveLetter;

    DriveKey RequireConfiguredDrive(DriveKey driveLetter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var letter = driveLetter.Validated();
        if (_options.Drives.All(drive => drive.Drive != letter))
        {
            throw new ArgumentException($"Drive {letter} is not part of this index.", nameof(driveLetter));
        }

        return letter;
    }
}

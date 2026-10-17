using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads current sizing without elevation. The library reports the size that would have retained
    ///     a lost checkpoint in JournalCheckpointLoss; the consumer owns the decision to grow.
    ///     Resizing is an explicit elevated broker operation.
    /// </summary>
    /// <param name="drive">A configured drive key. The default key is invalid.</param>
    [SupportedOSPlatform("windows")]
    public UsnJournalSettings QueryUsnJournalSettings(DriveKey drive)
    {
        var letter = RequireConfiguredDrive(drive);
        var configured = _driveConfigurations[letter];
        if (!DriveKind.For(configured.Producer, _options.MftSource).HasJournalSettings)
        {
            throw new InvalidOperationException(
                MftIndexSource.FormatUnavailable(letter, MftIndexSource.NoJournalSettingsReason));
        }

        return UsnJournalSettingsQuery.Query(letter);
    }

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

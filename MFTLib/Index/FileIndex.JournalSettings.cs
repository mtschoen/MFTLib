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
        var validatedDrive = RequireConfiguredDrive(drive);
        return GetDriveRuntime(validatedDrive).QueryUsnJournalSettings();
    }

    DriveKey RequireConfiguredDrive(DriveKey drive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validatedDrive = drive.Validated();
        if (_options.Drives.All(configuredDrive => configuredDrive.Drive != validatedDrive))
        {
            throw new ArgumentException($"Drive {validatedDrive} is not part of this index.", nameof(drive));
        }

        return validatedDrive;
    }
}

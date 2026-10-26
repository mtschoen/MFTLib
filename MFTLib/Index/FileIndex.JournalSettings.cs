using System.Diagnostics.CodeAnalysis;
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
    [ExcludeFromCodeCoverage(Justification = "Windows-only pass-through to ReadUsnJournalSettings; the target is covered.")]
    public UsnJournalSettings QueryUsnJournalSettings(DriveKey drive) => ReadUsnJournalSettings(drive);

    /// <summary>
    ///     The logic behind <see cref="QueryUsnJournalSettings" />: validates the drive, refuses a source without
    ///     live journal settings, and otherwise reads the volume, which throws
    ///     <see cref="PlatformNotSupportedException" /> off Windows.
    /// </summary>
    internal UsnJournalSettings ReadUsnJournalSettings(DriveKey drive)
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

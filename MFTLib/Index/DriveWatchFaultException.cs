using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Thrown by <see cref="IIndexDriveWatch.ReadAsync" /> when the drive's own watch failed, as
///     opposed to the channel carrying it being lost. <see cref="FileIndex" /> reports it as a
///     <see cref="WatchFaultKind.Drive" /> fault on <see cref="DriveLetter" />.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "Every instance names the drive whose watch failed, which is how FileIndex attributes " +
                    "the fault; the parameterless and message-only overloads the standard set would add " +
                    "construct one that names no drive.")]
public sealed class DriveWatchFaultException : Exception
{
    public DriveWatchFaultException(char driveLetter, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DriveLetter = driveLetter;
    }

    public char DriveLetter { get; }
}

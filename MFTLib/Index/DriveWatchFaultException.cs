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
internal sealed class DriveWatchFaultException : Exception
{
    /// <summary>Initializes a drive-specific watch fault.</summary>
    /// <param name="driveLetter">Drive whose watch failed.</param>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying failure, if one is available.</param>
    public DriveWatchFaultException(char driveLetter, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DriveLetter = driveLetter;
    }

    /// <summary>Gets the drive whose watch failed; the index attributes the fault to this drive.</summary>
    public char DriveLetter { get; }
}

using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Thrown by <see cref="IIndexDriveWatch.ReadAsync" /> when the drive's own watch failed,
///     rather than its channel. <see cref="FileIndex" /> reports a
///     <see cref="WatchFaultKind.Drive" /> fault for the runtime that owns the pump.
/// </summary>
/// <param name="message">Description of the failure.</param>
/// <param name="innerException">Underlying failure, if one is available.</param>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "This internal classification marker requires the source's failure message. " +
                    "Its two creation sites use the message and optional inner exception; " +
                    "additional standard constructors have no distinct caller.")]
internal sealed class DriveWatchFaultException(string message, Exception? innerException = null)
    : Exception(message, innerException);

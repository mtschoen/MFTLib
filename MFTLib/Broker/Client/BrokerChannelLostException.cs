using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     A broker pipe failed in a way the host did not report with an <c>Error</c> frame: it
///     reached EOF, a read or write failed, it carried a frame out of protocol order, or the host
///     reported a stall. A lost control pipe ends the whole <see cref="BrokerProcess" />; a lost
///     drive channel ends only that drive's operation.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "Every instance says which pipe was lost, a drive's channel or the control pipe, which is " +
                    "how a consumer attributes the failure; the standard overloads would construct one that " +
                    "does not say.")]
sealed class BrokerChannelLostException : IOException
{
    /// <summary>Records which pipe was lost.</summary>
    /// <param name="drive">The drive whose channel was lost, or null for the control pipe.</param>
    /// <param name="message">What was lost and why.</param>
    /// <param name="innerException">The read, write or protocol failure behind the loss, if any.</param>
    internal BrokerChannelLostException(VolumeKey? drive, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Drive = drive?.Validated();
    }

    /// <summary>The drive whose channel was lost, or null when the control pipe was.</summary>
    internal VolumeKey? Drive { get; }
}

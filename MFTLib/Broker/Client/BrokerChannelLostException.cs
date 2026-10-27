using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     A broker pipe failed in a way the host did not report with an <c>Error</c> frame: it
///     reached EOF, a read or write failed, it carried a frame out of protocol order, or the host
///     reported a stall. A lost control pipe ends the whole <see cref="BrokerProcess" />; a lost
///     volume channel ends only that volume's operation.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "Every instance says which pipe was lost, a volume's channel or the control pipe, which is " +
                    "how a consumer attributes the failure; the standard overloads would construct one that " +
                    "does not say.")]
sealed class BrokerChannelLostException : IOException
{
    /// <summary>Records which pipe was lost.</summary>
    /// <param name="volume">The volume whose channel was lost, or null for the control pipe.</param>
    /// <param name="message">What was lost and why.</param>
    /// <param name="innerException">The read, write or protocol failure behind the loss, if any.</param>
    internal BrokerChannelLostException(VolumeKey? volume, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Volume = volume?.Validated();
    }

    /// <summary>The volume whose channel was lost, or null when the control pipe was.</summary>
    internal VolumeKey? Volume { get; }
}

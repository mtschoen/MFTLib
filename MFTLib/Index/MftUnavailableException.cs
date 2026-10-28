using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Thrown by an <see cref="MftIndexSource" /> producer when the MFT cannot be reached at all, as opposed to
///     failing once reached. The index settles the attempt as <see cref="RootFailureKind.MftUnavailable" /> with
///     <see cref="Reason" />, and an <see cref="ProducerSelection.Automatic" /> open walks instead. Lives here so the
///     index can catch it without referencing the broker.
/// </summary>
/// <param name="reason">Why the MFT is unavailable.</param>
/// <param name="message">The failure text the root's status carries.</param>
/// <param name="innerException">The launch failure that caused it.</param>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "An internal classification marker with one creation site, which supplies the reason, the message and the launch failure; the other standard constructors have no caller.")]
sealed class MftUnavailableException(MftUnavailableReason reason, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>Why the MFT is unavailable.</summary>
    internal MftUnavailableReason Reason { get; } = reason;
}

using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

/// <summary>
///     The launcher returned false: the elevation prompt was declined, so no broker process exists. Typed so an
///     Automatic open walks instead of failing the index.
/// </summary>
/// <param name="message">What the caller reads; callers outside the library see an <see cref="InvalidOperationException" />.</param>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "An internal classification marker with one creation site, which supplies the message; the other standard constructors have no caller.")]
sealed class BrokerLaunchDeclinedException(string message) : InvalidOperationException(message);

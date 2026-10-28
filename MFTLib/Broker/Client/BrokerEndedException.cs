using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

/// <summary>
///     Thrown to every later use of a broker session whose process ended. Typed so an Automatic open can tell a
///     broker that is gone (walk instead) from a producer that failed while it ran (fail the index).
/// </summary>
/// <param name="message">What the caller reads; callers outside the library see an <see cref="InvalidOperationException" />.</param>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "An internal classification marker with one creation site, which supplies the message; the other standard constructors have no caller.")]
sealed class BrokerEndedException(string message) : InvalidOperationException(message);

using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

/// <summary>
///     Signals that a reply frame could not be written because the client closed its end of
///     the broker pipe. It reports the end of a session rather than a fault: no frame can
///     reach a client that is gone, so there is nothing left to serve or to explain, and
///     <see cref="JournalBrokerHost.ServeAsync" /> catches it and returns normally the way it
///     already does for a caller cancellation or a clean EOF. It is internal and never
///     reaches a consumer of the library.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "An internal control-flow signal rather than a fault a caller diagnoses: it is " +
                    "thrown only by the host's reply-frame write, caught only by ServeAsync, and never " +
                    "crosses an assembly or serialization boundary. The message and inner-exception " +
                    "overloads the standard set would add have no caller.")]
internal sealed class ClientDisconnectedException : Exception
{
    public ClientDisconnectedException()
        : base("The client closed its end of the broker pipe.")
    {
    }
}

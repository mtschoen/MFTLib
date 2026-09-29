using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

/// <summary>
///     Signals that a frame could not be written because the client closed its end of a broker
///     pipe. It reports the end of a pipe rather than a fault: no frame can reach a client that is
///     gone. On a drive pipe it ends that channel quietly; on the control pipe it ends the session,
///     and <see cref="JournalBrokerHost.ServeAsync" /> returns normally. It is internal and never
///     reaches a consumer of the library.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "An internal control-flow signal rather than a fault a caller diagnoses: it is " +
                    "thrown only by the host's frame write, caught only by the host's channel and " +
                    "request handlers, and never crosses an assembly or serialization boundary. The " +
                    "parameterless and message overloads the standard set would add have no caller.")]
internal sealed class ClientDisconnectedException(IOException innerException)
    : Exception("The client closed its end of the broker pipe.", innerException);

using System.IO.Pipes;

namespace MFTLib;

/// <summary>Creates the server end of each drive pipe a <see cref="BrokerProcess" /> opens.</summary>
internal interface IBrokerPipeFactory
{
    /// <summary>Starts listening on a new pipe named <paramref name="pipeName" />.</summary>
    BrokerPipeListener Listen(string pipeName);
}

/// <summary>
///     One listening drive pipe. Disposing it releases the pipe, connected or not, so the host
///     reads EOF on whatever end it holds; the stream <see cref="WaitForConnectionAsync" /> returns
///     is the pipe itself and needs no separate disposal. Disposal is idempotent.
/// </summary>
internal sealed class BrokerPipeListener(
    string pipeName,
    Func<CancellationToken, Task<Stream>> waitForConnection,
    Func<ValueTask> release) : IAsyncDisposable
{
    int _released;

    public string PipeName { get; } = pipeName;

    /// <summary>Completes with the connected stream once the host connects to the pipe.</summary>
    public Task<Stream> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        return waitForConnection(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref _released, 1) == 0 ? release() : ValueTask.CompletedTask;
    }
}

/// <summary>
///     Production drive pipes: one <see cref="NamedPipeServerStream" /> per channel. The
///     unelevated client creates every pipe server and the elevated broker connects as the client,
///     the only safe direction across integrity levels.
/// </summary>
internal sealed class NamedPipeBrokerPipeFactory : IBrokerPipeFactory
{
    public BrokerPipeListener Listen(string pipeName)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        return new BrokerPipeListener(pipeName, async cancellationToken =>
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return server;
        }, server.DisposeAsync);
    }
}

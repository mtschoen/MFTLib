using System.Collections.Concurrent;
using System.IO.Pipelines;
using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     In-memory broker pipes, matched by name: the client side listens through
///     <see cref="IBrokerPipeFactory" />, and the host side connects through
///     <see cref="ConnectAsync" />, the host's <see cref="BrokerChannelConnector" />. Closing either
///     end is EOF to the other, as with a named pipe.
/// </summary>
internal sealed class InMemoryBrokerPipes(BrokerTestHarnessOptions options, Func<string, Stream, Stream>? wrapClientStream)
    : IBrokerPipeFactory
{
    /// <summary>The name <see cref="BrokerTestHarnessOptions" /> seams use for the control pipe.</summary>
    public const string ControlPipeName = "control";

    readonly ConcurrentDictionary<string, TaskCompletionSource<Stream>> _listening = new(StringComparer.Ordinal);

    readonly HostPipeEnds _hostEnds = new();

    public BrokerPipeListener Listen(string pipeName)
    {
        var connection = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_listening.TryAdd(pipeName, connection))
        {
            throw new IOException($"A broker pipe named '{pipeName}' is already listening.");
        }

        return new BrokerPipeListener(pipeName, cancellationToken => connection.Task.WaitAsync(cancellationToken),
            () => ReleaseAsync(pipeName, connection));
    }

    /// <summary>The host's connect: hands the client its end of a new pair and returns the host's end.</summary>
    public Task<Stream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        if (options.FailConnection?.Invoke(pipeName) is { } failure)
        {
            return Task.FromException<Stream>(failure);
        }

        if (_hostEnds.HasExited)
        {
            return Task.FromException<Stream>(new IOException("The in-process broker has exited."));
        }

        if (!_listening.TryRemove(pipeName, out var connection))
        {
            return Task.FromException<Stream>(new IOException($"No broker pipe named '{pipeName}' is listening."));
        }

        var (client, host) = CreatePair(pipeName);
        if (!connection.TrySetResult(client))
        {
            client.Dispose();
            host.Dispose();
            return Task.FromException<Stream>(new IOException($"Broker pipe '{pipeName}' was closed before it connected."));
        }

        return Task.FromResult(host);
    }

    /// <summary>A connected pair; the host end holds its writes when <see cref="BrokerTestHarnessOptions.HoldWrites" /> says so.</summary>
    public (Stream Client, Stream Host) CreatePair(string pipeName)
    {
        var clientToHost = new Pipe();
        var hostToClient = new Pipe();
        Stream client = new InMemoryDuplexStream(hostToClient.Reader.AsStream(), clientToHost.Writer.AsStream());
        Stream host = new InMemoryDuplexStream(clientToHost.Reader.AsStream(), hostToClient.Writer.AsStream());
        if (options.HoldWrites is { } holdWrites)
        {
            host = new HeldWriteStream(host, () => holdWrites(pipeName));
        }

        _hostEnds.Track(host);
        return (wrapClientStream?.Invoke(pipeName, client) ?? client, host);
    }

    /// <summary>The host's exit: closes every end the host holds, so the client reads EOF on each.</summary>
    public void CloseHostEnds() => _hostEnds.CloseAll();

    // Stops listening. A connection the host already made is closed, so the host reads EOF.
    async ValueTask ReleaseAsync(string pipeName, TaskCompletionSource<Stream> connection)
    {
        _listening.TryRemove(KeyValuePair.Create(pipeName, connection));
        if (!connection.TrySetCanceled() && connection.Task.IsCompletedSuccessfully)
        {
            await (await connection.Task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
    }
}

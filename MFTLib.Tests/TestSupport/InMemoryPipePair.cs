using System.IO.Pipelines;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Both ends of one in-memory broker pipe: <see cref="Client" /> plays the unelevated side that
///     created the pipe, <see cref="Host" /> the broker's connected end. Disposing
///     <see cref="Client" /> is the client closing its pipe: the host's reads reach EOF. A test
///     that needs only the two streams deconstructs the pair.
/// </summary>
internal sealed class InMemoryPipePair : IAsyncDisposable
{
    public InMemoryPipePair()
    {
        var clientToHost = new Pipe();
        var hostToClient = new Pipe();
        Client = new InMemoryDuplexStream(hostToClient.Reader.AsStream(), clientToHost.Writer.AsStream());
        Host = new InMemoryDuplexStream(clientToHost.Reader.AsStream(), hostToClient.Writer.AsStream());
    }

    public Stream Client { get; }

    public Stream Host { get; }

    public void Deconstruct(out Stream client, out Stream host)
    {
        client = Client;
        host = Host;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Host.DisposeAsync();
    }
}

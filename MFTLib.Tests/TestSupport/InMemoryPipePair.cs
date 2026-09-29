namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Both ends of one in-memory broker pipe: <see cref="Client" /> plays the unelevated side that
///     created the pipe, <see cref="Host" /> the broker's connected end. Disposing
///     <see cref="Client" /> is the client closing its pipe: the host's reads reach EOF.
/// </summary>
internal sealed class InMemoryPipePair : IAsyncDisposable
{
    public InMemoryPipePair()
    {
        (Client, Host) = DuplexStream.CreatePair();
    }

    public DuplexStream Client { get; }

    public DuplexStream Host { get; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Host.DisposeAsync();
    }
}

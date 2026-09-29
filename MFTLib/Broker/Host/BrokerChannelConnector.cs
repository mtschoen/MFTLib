namespace MFTLib;

/// <summary>
///     Connects the broker to the drive pipe a client created and named in an
///     <see cref="BrokerFrameKind.OpenChannel" /> request. Production opens a
///     <c>NamedPipeClientStream</c> to that pipe; tests return an in-memory stream.
/// </summary>
public delegate Task<Stream> BrokerChannelConnector(string pipeName, CancellationToken cancellationToken);

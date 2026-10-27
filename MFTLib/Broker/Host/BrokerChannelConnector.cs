namespace MFTLib;

/// <summary>
///     Connects the broker to the volume pipe a client created and named in an
///     channel-open request. Production opens a
///     <c>NamedPipeClientStream</c> to that pipe; tests return an in-memory stream.
/// </summary>
delegate Task<Stream> BrokerChannelConnector(string pipeName, CancellationToken cancellationToken);

using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The index's watch source over a broker: every start opens a drive pipe of its own on the
///     process the connect callback yields and returns that pipe as the drive's watch. The source
///     keeps no state per drive, so a drive that stops, faults or is restarted never reaches
///     another drive's watch. The caller keeps ownership of the process.
/// </summary>
public sealed class BrokerIndexWatchSource : IIndexWatchSource
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;

    /// <summary>Builds a source over the given broker connection.</summary>
    /// <param name="connectAsync">Yields the process each watch runs on. The caller keeps ownership of it.</param>
    public BrokerIndexWatchSource(Func<CancellationToken, Task<BrokerProcess>> connectAsync)
    {
        _connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
    }

    /// <summary>
    ///     Connects, opens the drive's channel and writes <c>StartWatch</c>, then returns the
    ///     channel as the drive's watch. A start cancelled or failed on the way closes its own pipe,
    ///     and one whose token is already cancelled connects to nothing.
    /// </summary>
    public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var process = await _connectAsync(cancellationToken).ConfigureAwait(false);
        return await process.OpenWatchChannelAsync(target, cancellationToken).ConfigureAwait(false);
    }
}

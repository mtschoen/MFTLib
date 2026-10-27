using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     The index's watch source over a broker: every start opens a volume pipe of its own on the
///     process the connect callback yields and returns that pipe as the volume's watch. The source
///     keeps no state per volume, so a volume that stops, faults or is restarted never reaches
///     another volume's watch. The caller keeps ownership of the process.
/// </summary>
sealed class BrokerIndexWatchSource : IIndexWatchSource
{
    readonly Func<CancellationToken, Task<BrokerProcess>> _connectAsync;

    /// <summary>Builds a source over the given broker connection.</summary>
    /// <param name="connectAsync">Yields the process each watch runs on. The caller keeps ownership of it.</param>
    public BrokerIndexWatchSource(Func<CancellationToken, Task<BrokerProcess>> connectAsync)
    {
        _connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
    }

    /// <summary>
    ///     Connects, opens the volume's channel and writes <c>StartWatch</c>, then returns the
    ///     channel as the volume's watch. A start cancelled or failed on the way closes its own pipe,
    ///     and one whose token is already cancelled connects to nothing.
    /// </summary>
    public async Task<IIndexVolumeWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var process = await _connectAsync(cancellationToken).ConfigureAwait(false);
        return await process.OpenWatchChannelAsync(target, cancellationToken).ConfigureAwait(false);
    }
}

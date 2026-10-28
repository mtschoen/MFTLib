namespace MFTLib.Index;

/// <summary>
///     The client surface over one mapped root block. Opening picks a producer for the configured
///     root from its volume facts, warm-starts from a valid block or cold-scans, and publishes the block. A rescan
///     writes a new block beside the old one and swaps it in; handles minted from the old
///     block keep it mapped until they are collected, so a held handle never dangles across a rescan.
/// </summary>
public sealed partial class FileIndex : IAsyncDisposable
{
    readonly FileIndexOptions _options;

    /// <summary>
    ///     Cancelled by <see cref="DisposeAsync" /> before it waits for anything. Every query's
    ///     effective token is linked to this one, so a scan in flight is told to stop rather than
    ///     holding disposal open for the rest of a whole-root pass. Every rescan's token is linked
    ///     to it as well. Deliberately not disposed, for the same reason as the runtime's gates: a
    ///     query that raced the disposal still reads this token, and a disposed source would
    ///     answer it with an exception naming the source rather than the index.
    /// </summary>
    readonly CancellationTokenSource _disposalCancellation = new();

    /// <summary>
    ///     The signal that stops the queries in flight when this index is disposed. A query's
    ///     effective token is this one, or this one linked with the caller's own.
    /// </summary>
    internal CancellationToken DisposalToken => _disposalCancellation.Token;

    /// <summary>
    ///     Guards the root's runtime state, and is the one step
    ///     that publishes a block: a commit of the root's block and the
    ///     retirement of the replaced block happen under it, so a reader (<see cref="Status" />,
    ///     <see cref="CurrentBlock" />) never observes a partial swap. It is innermost in the
    ///     lock order (see <see cref="VolumeScan" />) and never held across an <c>await</c>.
    /// </summary>
    Lock StateLock => _view.Scan.StateLock;

    bool IsDisposed => _view.IsDisposed;

    /// <summary>
    ///     How many scans of the root in a row may lose their journal catch-up before a scan
    ///     operation stops rescanning the root by itself.
    /// </summary>
    internal const int LostCatchUpRecoveryLimit = 3;

    FileIndex(FileIndexOptions options, ResolvedRoot resolved, string cacheDirectoryPath, IndexHooks hooks)
    {
        _options = options;
        CacheDirectoryPath = cacheDirectoryPath;
        Hooks = hooks;
        _view = new IndexView(this, resolved);
        _view.Scan = new VolumeScan(new VolumeScanCreation(resolved, options, cacheDirectoryPath, hooks));
        _view.Scan.AttachFirstView(_view);
    }

    internal RootKey RootKey => _view.Root;

    internal RootKind Kind => _view.Kind;

    /// <summary>The normalized host path of the root: the volume root, the directory, or a dump's virtual root.</summary>
    internal string RootDirectory => _view.RootDirectory;

    internal string? MatchableRootDirectoryPath => _view.MatchableRootDirectoryPath;

    internal void ThrowIfDisposed()
    {
        lock (StateLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
        }
    }

    /// <summary>
    ///     The explicitly configured cache directory, or empty for <see cref="CacheMode.Disabled" /> and dump opens.
    /// </summary>
    internal string CacheDirectoryPath { get; }

    /// <summary>
    ///     Recomputed from the block header on every read, so it reflects the latest mutation.
    /// </summary>
    public RootStatus Status
    {
        get
        {
            Hooks.RootsReadBeforeLock?.Invoke();
            lock (StateLock)
            {
                // Checked under the lock: disposal sets the flag under it before it unpublishes
                // the block, so a read that sees the flag clear sees the block still published.
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return _view.Scan.DescribeLocked(_view);
            }
        }
    }

    internal RootBlock? CurrentBlock
    {
        get
        {
            lock (StateLock)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return _view.Block;
            }
        }
    }

    /// <summary>
    ///     Opens an index over the configured root, adopting a valid cached block or
    ///     producing a fresh one. A missing root settles as <see cref="RootState.Offline" />, and an
    ///     operational scan failure settles as <see cref="RootState.Failed" /> in <see cref="Status" />
    ///     without failing the open, for both MFT and enumeration producers; this includes
    ///     <see cref="ProducerSelection.MftOnly" /> over a root the MFT cannot serve. Argument validation
    ///     performed before production throws. Caller cancellation fails the open and releases any adopted block.
    /// </summary>
    /// <param name="options">The root to index and the cache, progress and producer settings.</param>
    /// <param name="cancellationToken">
    ///     Cancels the open. Any adopted block is released before the cancellation is thrown.
    /// </param>
    /// <returns>The opened index, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="options" /> specifies an undefined <see cref="FileIndexOptions.CacheMode" /> or an
    ///     undefined <see cref="FileIndexOptions.Producer" />.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     Cache configuration is missing, dump options are invalid (including <see cref="ProducerSelection.WalkOnly" />),
    ///     or <see cref="FileIndexOptions.Root" /> is null or invalid.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the open finished.</exception>
    public static Task<FileIndex> OpenAsync(FileIndexOptions options,
        CancellationToken cancellationToken) =>
        OpenWithHooksAsync(options, new IndexHooks(), cancellationToken);

    /// <summary>
    ///     Opens an index with the supplied per-index hooks, invoking open-time dependencies before returning.
    /// </summary>
    internal static async Task<FileIndex> OpenWithHooksAsync(FileIndexOptions options,
        IndexHooks hooks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        options = RootKind.PrepareOpenOptions(options);
        var resolved = RootResolution.Resolve(options);
        var cacheDirectoryPath = string.Empty;
        if (options.CacheMode != CacheMode.Disabled)
        {
            var suppliedCacheDirectory = options.CacheDirectory;
            if (string.IsNullOrWhiteSpace(suppliedCacheDirectory))
            {
                throw new ArgumentException(
                    "Set FileIndexOptions.CacheDirectory or set FileIndexOptions.CacheMode to Disabled.",
                    nameof(options));
            }

            cacheDirectoryPath = suppliedCacheDirectory;
            CacheDirectory.EnsureCreated(cacheDirectoryPath);
        }

        var index = new FileIndex(options, resolved, cacheDirectoryPath, hooks);
        try
        {
            await index.SettleRootAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            index.ReleaseUnpublishedBlocks();
            throw;
        }

        return index;
    }
}

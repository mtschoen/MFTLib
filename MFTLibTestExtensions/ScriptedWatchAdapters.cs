using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Presents a <see cref="ScriptedWatchSource" /> to the index as an <see cref="IIndexWatchSource" />.</summary>
sealed class ScriptedWatchSourceAdapter(ScriptedWatchSource source) : IIndexWatchSource
{
    public async Task<IIndexVolumeWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        var watch = await source.StartAsync(target, cancellationToken).ConfigureAwait(false);
        return watch.IndexWatch;
    }
}

/// <summary>Presents a <see cref="ScriptedVolumeWatch" /> to the index as an <see cref="IIndexVolumeWatch" />.</summary>
sealed class ScriptedVolumeWatchAdapter(ScriptedVolumeWatch watch) : IIndexVolumeWatch
{
    public IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken) =>
        watch.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync() => watch.DisposeWatchAsync();
}

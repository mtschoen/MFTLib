using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Presents a <see cref="ScriptedWatchSource" /> to the index as an <see cref="IIndexWatchSource" />.</summary>
internal sealed class ScriptedWatchSourceAdapter(ScriptedWatchSource source) : IIndexWatchSource
{
    public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        var watch = await source.StartAsync(target, cancellationToken).ConfigureAwait(false);
        return watch.IndexWatch;
    }
}

/// <summary>Presents a <see cref="ScriptedDriveWatch" /> to the index as an <see cref="IIndexDriveWatch" />.</summary>
internal sealed class ScriptedDriveWatchAdapter(ScriptedDriveWatch watch) : IIndexDriveWatch
{
    public IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken) =>
        watch.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync() => watch.DisposeWatchAsync();
}

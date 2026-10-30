using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Fallback cleanup for tests that explicitly dispose their subject before the test scope ends.</summary>
sealed class IndexDisposalScope : IAsyncDisposable
{
    readonly List<FileIndex> _indices = [];

    internal FileIndex Add(FileIndex index)
    {
        _indices.Add(index);
        return index;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var index in _indices)
        {
            await index.DisposeAsync();
        }
    }
}

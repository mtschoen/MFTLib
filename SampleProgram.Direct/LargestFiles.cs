using MFTLib.Index;

namespace SampleProgram.Direct;

/// <summary>The largest files in an index: one pass over the rows with a bounded heap, building no name or path.</summary>
internal static class LargestFiles
{
    internal static IReadOnlyList<FileEntry> Find(FileIndex index, int count, FileEntry? under,
        CancellationToken cancellationToken, Action<int>? resultAssembledForTest = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0)
        {
            return [];
        }

        // A min-heap of the best so far: once it holds count entries, each larger row evicts the smallest.
        var best = new PriorityQueue<FileEntry, long>();
        foreach (var row in index.EnumerateRows(new SearchQuery(null, Under: under, Directories: false), cancellationToken))
        {
            if (!row.IsSizeKnown)
            {
                continue;
            }

            if (best.Count < count)
            {
                best.Enqueue(row.ToEntry(), row.Size);
            }
            else if (best.TryPeek(out _, out var smallest) && row.Size > smallest)
            {
                best.EnqueueDequeue(row.ToEntry(), row.Size);
            }
        }

        var largestFirst = new FileEntry[best.Count];
        for (var position = largestFirst.Length - 1; position >= 0; position--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            largestFirst[position] = best.Dequeue();
            // Per-call observation lets tests cancel during assembly without clocks or shared state.
            resultAssembledForTest?.Invoke(largestFirst.Length - position);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return largestFirst;
    }
}

using MFTLib.Index;

namespace Benchmark;

/// <summary>Spike: "C foreach". Plain loops over EnumerateRows with the sieve state in locals. Public API only.</summary>
static class ForeachSieve
{
    public static Dictionary<string, List<FileEntry>> Find(FileIndex index, long rowCount, out int scanPassCount, CancellationToken token)
    {
        var query = new SearchQuery(null, Directories: false);
        var bucketCount = ConsumerSieve.ComputeBucketCount(rowCount);
        var passes = new ConsumerSieve.Pass[4];
        var passCount = 0;
        var previous = long.MaxValue;
        scanPassCount = 0;
        for (var passIndex = 0; passIndex <= 3; passIndex++)
        {
            var pass = new ConsumerSieve.Pass(bucketCount, passIndex);
            long candidates = 0;
            foreach (var row in index.EnumerateRows(query, token))
            {
                if (row.Name.IsEmpty) continue;
                var hash = string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase);
                var rejected = false;
                for (var earlier = 0; earlier < passCount; earlier++)
                {
                    if (!passes[earlier].MayRepeat(hash)) { rejected = true; break; }
                }

                if (rejected) continue;
                pass.Increment(hash);
                candidates++;
            }

            scanPassCount++;
            passes[passCount++] = pass;
            var stop = candidates <= bucketCount / 256 || candidates >= previous / 2 || passIndex == 3;
            previous = candidates;
            if (stop) break;
        }

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in index.EnumerateRows(query, token))
        {
            if (row.Name.IsEmpty) continue;
            var hash = string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase);
            var rejected = false;
            for (var earlier = 0; earlier < passCount; earlier++)
            {
                if (!passes[earlier].MayRepeat(hash)) { rejected = true; break; }
            }

            if (rejected) continue;
            var key = new string(row.Name);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            list.Add(row.ToEntry());
        }

        scanPassCount++;
        return byName;
    }

    public static List<long> Largest(FileIndex index, int count)
    {
        var best = new PriorityQueue<FileEntry, long>(count + 1);
        foreach (var row in index.EnumerateRows(new SearchQuery(null, Directories: false)))
        {
            if (row.IsDeleted || !row.IsSizeKnown) continue;
            best.Enqueue(row.ToEntry(), row.Size);
            if (best.Count > count) best.Dequeue();
        }

        var sizes = new List<long>();
        while (best.TryDequeue(out var entry, out _)) sizes.Add(entry.Size);
        sizes.Sort();
        return sizes;
    }
}

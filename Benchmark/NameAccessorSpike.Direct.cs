using MFTLib.Index;

namespace Benchmark;

/// <summary>Spike: "B direct". Struct visitors hold the sieve state themselves; no delegate, no class forwarder. Public API only.</summary>
static class DirectSieve
{
    const int MaximumRefinementPassCount = 3;

    struct HashPassVisitor(ConsumerSieve.Pass[] earlier, int earlierCount, ConsumerSieve.Pass current) : IIndexRowVisitor
    {
        public long Candidates;

        public void Visit(in IndexRow row)
        {
            if (row.Name.IsEmpty) return;
            var hash = string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase);
            for (var index = 0; index < earlierCount; index++)
            {
                if (!earlier[index].MayRepeat(hash)) return;
            }

            current.Increment(hash);
            Candidates++;
        }
    }

    struct MaterializeVisitor(ConsumerSieve.Pass[] earlier, int earlierCount, Dictionary<string, List<FileEntry>> byName) : IIndexRowVisitor
    {
        public void Visit(in IndexRow row)
        {
            if (row.Name.IsEmpty) return;
            var hash = string.GetHashCode(row.Name, StringComparison.OrdinalIgnoreCase);
            for (var index = 0; index < earlierCount; index++)
            {
                if (!earlier[index].MayRepeat(hash)) return;
            }

            var key = new string(row.Name);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            list.Add(row.ToEntry());
        }
    }

    public static Dictionary<string, List<FileEntry>> Find(FileIndex index, long rowCount, out int scanPassCount, CancellationToken token)
    {
        var query = new SearchQuery(null, Directories: false);
        var bucketCount = ConsumerSieve.ComputeBucketCount(rowCount);
        var passes = new ConsumerSieve.Pass[MaximumRefinementPassCount + 1];
        var passCount = 0;
        var previous = long.MaxValue;
        scanPassCount = 0;
        for (var passIndex = 0; passIndex <= MaximumRefinementPassCount; passIndex++)
        {
            var pass = new ConsumerSieve.Pass(bucketCount, passIndex);
            var visitor = new HashPassVisitor(passes, passCount, pass);
            index.ForEachRow(query, ref visitor, token);
            scanPassCount++;
            passes[passCount++] = pass;
            var candidates = visitor.Candidates;
            var stop = candidates <= bucketCount / 256 || candidates >= previous / 2 || passIndex == MaximumRefinementPassCount;
            previous = candidates;
            if (stop) break;
        }

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        var materialize = new MaterializeVisitor(passes, passCount, byName);
        index.ForEachRow(query, ref materialize, token);
        scanPassCount++;
        return byName;
    }
}

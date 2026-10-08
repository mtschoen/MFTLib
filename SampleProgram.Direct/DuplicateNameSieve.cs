using MFTLib.Index;

namespace SampleProgram.Direct;

/// <summary>A file name and every live file that carries it, compared without regard to case.</summary>
internal sealed record DuplicateNameGroup(string Name, IReadOnlyList<FileEntry> Entries);

/// <summary>
///     Up to four seeded hash passes narrow candidates before names and entries are materialized.
///     The bitmaps peak at 80 MiB; final candidate storage depends on the surviving rows. Each pass
///     takes its own snapshot, so concurrent changes can add or drop candidates. Final grouping by
///     real names prevents false groups from hash collisions. Entries keep enumeration order.
/// </summary>
internal static class DuplicateNameSieve
{
    const int MaximumRefinementPassCount = 3;
    const int CandidateThresholdDivisor = 256;

    internal static IReadOnlyList<DuplicateNameGroup> Find(FileIndex index, CancellationToken cancellationToken,
        int? bucketCountOverride = null, Action<int>? resultAssembledForTest = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        cancellationToken.ThrowIfCancellationRequested();
        var query = new SearchQuery(null, Directories: false);
        var bucketCount = bucketCountOverride ?? SieveBitmap.ComputeBucketCount(LiveRowCount(index));
        var passes = RunHashPasses(index, query, bucketCount, cancellationToken);

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in index.EnumerateRows(query, cancellationToken))
        {
            if (row.Name.IsEmpty || !SurvivesEvery(passes, HashOf(row.Name)))
            {
                continue;
            }

            var name = new string(row.Name);
            if (!byName.TryGetValue(name, out var entries))
            {
                byName[name] = entries = [];
            }

            entries.Add(row.ToEntry());
        }

        var groups = new List<DuplicateNameGroup>();
        foreach (var (name, entries) in byName)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Count > 1)
            {
                groups.Add(new DuplicateNameGroup(name, entries));
                // Per-call observation lets tests cancel during assembly without clocks or shared state.
                resultAssembledForTest?.Invoke(groups.Count);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return groups;
    }

    static List<SieveBitmap> RunHashPasses(FileIndex index, SearchQuery query, int bucketCount,
        CancellationToken cancellationToken)
    {
        var passes = new List<SieveBitmap>();
        var previousCandidateCount = long.MaxValue;
        for (var passIndex = 0; passIndex <= MaximumRefinementPassCount; passIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pass = new SieveBitmap(bucketCount, passIndex);
            long candidateCount = 0;
            foreach (var row in index.EnumerateRows(query, cancellationToken))
            {
                if (row.Name.IsEmpty)
                {
                    continue;
                }

                var hash = HashOf(row.Name);
                if (!SurvivesEvery(passes, hash))
                {
                    continue;
                }

                pass.Record(hash);
                candidateCount++;
            }

            pass.CompletePass();
            passes.Add(pass);
            var stop = ShouldStopRefining(candidateCount, previousCandidateCount, bucketCount, passIndex);
            previousCandidateCount = candidateCount;
            if (stop)
            {
                break;
            }
        }

        return passes;
    }

    internal static bool ShouldStopRefining(long candidateCount, long previousCandidateCount, int bucketCount, int passIndex)
    {
        var candidateCountIsSmall = candidateCount <= bucketCount / CandidateThresholdDivisor;
        var candidateCountDidNotHalve = candidateCount >= previousCandidateCount / 2;
        var atHardMaximum = passIndex == MaximumRefinementPassCount;
        return candidateCountIsSmall || candidateCountDidNotHalve || atHardMaximum;
    }

    static int HashOf(ReadOnlySpan<char> name) => string.GetHashCode(name, StringComparison.OrdinalIgnoreCase);

    static bool SurvivesEvery(List<SieveBitmap> passes, int hash)
    {
        foreach (var pass in passes)
        {
            if (!pass.MayRepeat(hash))
            {
                return false;
            }
        }

        return true;
    }

    static long LiveRowCount(FileIndex index)
    {
        long total = 0;
        foreach (var drive in index.Drives)
        {
            total += drive.Block.LiveRowCount;
        }

        return total;
    }
}

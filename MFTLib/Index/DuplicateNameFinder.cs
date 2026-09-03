namespace MFTLib.Index;

/// <summary>
///     Refinement-chain implementation behind <see cref="AggregateEngine.DuplicateNames" />. A
///     single sieve pass (see <see cref="NameHashTable" />) never drops a real duplicate group,
///     but a bucket collision between two distinct names makes that pass admit both as
///     candidates: at the row counts real drives produce, that false-positive set is itself
///     proportional to the row count, so materializing a name string, a dictionary entry, a
///     list, and a <see cref="FileEntry" /> for every admitted row would move the same unbounded
///     cost from the sieve into the second pass instead of removing it.
/// </summary>
/// <remarks>
///     <para>
///         The fix is to keep sieving before materializing anything. Pass 0 is the sieve as
///         before, seeded 0, over every live non-directory row. Each refinement pass i = 1, 2,
///         ... rescans every row but only admits one that passes every earlier pass's
///         <see cref="MayRepeatBitmap.Contains" /> check (not just the previous pass's, since a
///         row eliminated two passes back must not re-enter through a fresh collision in the
///         newest sieve), then feeds admitted rows into a new sieve seeded i. Because a real
///         duplicate's hash sets <c>seenAgain</c> in every seed-independent pass by construction,
///         it survives every refinement pass; only false positives can be filtered out. Passes
///         stop once the candidate count <see cref="CandidateThresholdDivisor" />-shrinks below
///         the bucket count, once a pass fails to at least halve the previous pass's candidate
///         count, or at <see cref="MaximumRefinementPassCount" /> refinement passes, whichever
///         comes first. Only the final chain is used to gate the materialization pass, which is
///         the only pass that allocates a name string.
///     </para>
///     <para>
///         Peak memory: every pass shares one bucket count, sized from the snapshot's row count
///         via <see cref="NameHashTable.ComputeBucketCount" /> and clamped to
///         <see cref="NameHashTable.MaximumBucketCount" /> (2^27, 16 MiB per bit array). A
///         completed pass keeps only its <c>seenAgain</c> bitmap (<see cref="MayRepeatBitmap" />)
///         once <see cref="NameHashTable.ToMayRepeatBitmap" /> is called, so the chain's peak is
///         the bitmaps for every already-completed pass plus the two bit arrays
///         (<c>seenOnce</c> and <c>seenAgain</c>) of whichever pass is currently being built. At
///         the hard maximum of pass 0 plus 3 refinement passes, that peak is 4 completed
///         <see cref="MayRepeatBitmap" /> instances plus one <see cref="NameHashTable" /> under
///         construction: 4 * 16 MiB + 16 MiB (<c>seenOnce</c>, the concurrently live
///         <c>seenAgain</c> for that same pass is one of the 4) = 80 MiB, independent of drive
///         size. Each pass costs one extra full sequential scan of the name column: up to 5
///         scans total (pass 0, up to 3 refinement passes, one materialization pass) versus the
///         2 scans of the single-pass design.
///     </para>
/// </remarks>
internal static class DuplicateNameFinder
{
    /// <summary>Hard cap on refinement passes after pass 0, independent of how slowly candidates shrink.</summary>
    internal const int MaximumRefinementPassCount = 3;

    // A pass stops refining once its candidate count is at most bucketCount / 256 of the total
    // buckets, i.e. the next pass's expected false-positive rate would already be under roughly
    // 0.4 percent; running another pass past that point buys little for the cost of a full
    // rescan.
    const int CandidateThresholdDivisor = 256;

    internal static List<DuplicateGroup> Find(Snapshot snapshot, DuplicateNameSieveOptions options,
        out DuplicateNameRefinementStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var bucketCount = options.BucketCountOverride ?? NameHashTable.ComputeBucketCount(SumRowCounts(snapshot));
        var bitmaps = new List<MayRepeatBitmap>();
        var candidatesPerPass = new List<long>();
        var previousCandidateCount = long.MaxValue;

        for (var passIndex = 0; passIndex <= MaximumRefinementPassCount; passIndex++)
        {
            var table = NameHashTable.ForBucketCount(bucketCount, passIndex);
            long candidateCount = 0;
            ForEachEligibleHash(snapshot, hash =>
            {
                if (!PassesEarlierPasses(bitmaps, hash))
                {
                    return;
                }

                table.Increment(hash);
                candidateCount++;
            });

            candidatesPerPass.Add(candidateCount);
            bitmaps.Add(table.ToMayRepeatBitmap());

            var stop = ShouldStopRefining(candidateCount, previousCandidateCount, bucketCount, passIndex);
            previousCandidateCount = candidateCount;

            if (stop)
            {
                break;
            }
        }

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        long namesMaterialized = 0;
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal);
            while (scanner.MoveNext())
            {
                ref readonly var row = ref scanner.Current;
                var name = scanner.CurrentName;
                if (!row.IsInUse || row.IsDeleted || row.IsDirectory || name.IsEmpty)
                {
                    continue;
                }

                var hash = NameMatching.GetNameHashCode(name, caseSensitive: false);
                if (!PassesEarlierPasses(bitmaps, hash))
                {
                    continue;
                }

                AddCandidate(byName, snapshot, driveBlock.DriveOrdinal, scanner.CurrentRowIndex, name);
                namesMaterialized++;
            }
        }

        statistics = new DuplicateNameRefinementStatistics(candidatesPerPass, namesMaterialized);

        var groups = new List<DuplicateGroup>();
        foreach (var (name, entries) in byName)
        {
            if (entries.Count > 1)
            {
                groups.Add(new DuplicateGroup(name, entries));
            }
        }

        return groups;
    }

    /// <summary>
    ///     Whether the pass that just finished (its candidate count and the count from the pass
    ///     before it) should be the last one in the chain. A pure predicate so the three stop
    ///     conditions in the class doc comment can each be tested directly rather than only
    ///     through the statistical behavior of a full end-to-end run.
    /// </summary>
    internal static bool ShouldStopRefining(long candidateCount, long previousCandidateCount, int bucketCount, int passIndex)
    {
        var candidateCountIsSmall = candidateCount <= bucketCount / CandidateThresholdDivisor;
        var candidateCountDidNotHalve = candidateCount >= previousCandidateCount / 2;
        var atHardMaximum = passIndex == MaximumRefinementPassCount;
        return candidateCountIsSmall || candidateCountDidNotHalve || atHardMaximum;
    }

    /// <summary>
    ///     One scanner pass per drive over every live, non-directory row's case-insensitive name
    ///     hash, shared by pass 0 and every refinement pass. No name string is allocated here,
    ///     only the hash.
    /// </summary>
    static void ForEachEligibleHash(Snapshot snapshot, Action<int> onEligibleHash)
    {
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal);
            while (scanner.MoveNext())
            {
                ref readonly var row = ref scanner.Current;
                var name = scanner.CurrentName;
                if (!row.IsInUse || row.IsDeleted || row.IsDirectory || name.IsEmpty)
                {
                    continue;
                }

                onEligibleHash(NameMatching.GetNameHashCode(name, caseSensitive: false));
            }
        }
    }

    static bool PassesEarlierPasses(List<MayRepeatBitmap> bitmaps, int hash)
    {
        foreach (var bitmap in bitmaps)
        {
            if (!bitmap.Contains(hash))
            {
                return false;
            }
        }

        return true;
    }

    static long SumRowCounts(Snapshot snapshot)
    {
        long total = 0;
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            total += driveBlock.Block.Header.RowCount;
        }

        return total;
    }

    static void AddCandidate(Dictionary<string, List<FileEntry>> byName, Snapshot snapshot,
        ushort driveOrdinal, uint rowIndex, ReadOnlySpan<char> name)
    {
        var key = new string(name);
        if (!byName.TryGetValue(key, out var entries))
        {
            entries = [];
            byName[key] = entries;
        }

        entries.Add(FileEntry.Create(snapshot, driveOrdinal, rowIndex));
    }
}

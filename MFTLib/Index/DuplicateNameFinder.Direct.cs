namespace MFTLib.Index;

/// <summary>Spike control: the library hash-pass loop with the Action delegate replaced by inline sieve work.</summary>
internal static partial class DuplicateNameFinder
{
    internal static List<DuplicateGroup> FindDirect(Snapshot snapshot, out int scanPassCount,
        CancellationToken cancellationToken = default)
    {
        var bucketCount = NameHashTable.ComputeBucketCount(SumRowCounts(snapshot));
        var bitmaps = new List<MayRepeatBitmap>();
        var previousCandidateCount = long.MaxValue;
        scanPassCount = 0;
        for (var passIndex = 0; passIndex <= MaximumRefinementPassCount; passIndex++)
        {
            var table = NameHashTable.ForBucketCount(bucketCount, passIndex);
            long candidateCount = 0;
            scanPassCount++;
            foreach (var driveBlock in snapshot.DriveBlocks)
            {
                var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal, cancellationToken);
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

                    table.Increment(hash);
                    candidateCount++;
                }
            }

            bitmaps.Add(table.ToMayRepeatBitmap());
            var stop = ShouldStopRefining(candidateCount, previousCandidateCount, bucketCount, passIndex);
            previousCandidateCount = candidateCount;
            if (stop)
            {
                break;
            }
        }

        var (byName, _) = MaterializeCandidateEntries(snapshot, bitmaps, cancellationToken);
        scanPassCount++;
        return AssembleDuplicateGroups(byName, cancellationToken);
    }
}

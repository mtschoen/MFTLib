namespace MFTLib.Index;

/// <summary>
///     The exact-name half of the engine. An exact query has no row scan: it gathers the candidate rows of the matching
///     name identifiers from the posting regions (<see cref="PostingCandidates" />), then reads each candidate row once,
///     in ascending row order, and keeps it only if the row still names one of the identifiers and passes the column and
///     subtree predicates. That is the same acceptance test the scan applies, so the answers are the same.
/// </summary>
static partial class SearchEngine
{
    static void SearchExactRoot(RootScan scan, NameQueryMatches names, List<FileEntry> results,
        CancellationToken cancellationToken)
    {
        using var candidates = PostingCandidates.Gather(scan.RootBlock.Block, PostingCandidates.IdentifiersOf(names),
            cancellationToken);
        if (candidates.Count <= PostingCandidates.ParallelVerificationThreshold)
        {
            VerifyCandidates(scan, names, candidates, candidates.Rows, results, cancellationToken);
            return;
        }

        VerifyCandidatesInParallel(scan, names, candidates, results, cancellationToken);
    }

    /// <summary>Verifies contiguous slices of the sorted candidates on several threads and concatenates them, keeping ascending order.</summary>
    static void VerifyCandidatesInParallel(RootScan scan, NameQueryMatches names, PostingCandidates candidates,
        List<FileEntry> results, CancellationToken cancellationToken)
    {
        var partitions = ScanPartitioning.Partition((uint)candidates.Count, Environment.ProcessorCount);
        var perPartition = new List<FileEntry>[partitions.Count];
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, partitions.Count, options, index =>
        {
            var (start, end) = partitions[index];
            var local = new List<FileEntry>();
            VerifyCandidates(scan, names, candidates, candidates.Rows.Slice((int)start, (int)(end - start)), local,
                cancellationToken);
            perPartition[index] = local;
        });

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var local in perPartition)
        {
            results.AddRange(local);
        }
    }

    static void VerifyCandidates(RootScan scan, NameQueryMatches names, PostingCandidates candidates,
        ArraySegment<uint> slice, List<FileEntry> destination, CancellationToken cancellationToken)
    {
        var (rootBlock, query, membership) = scan;
        var rows = rootBlock.Block.Rows;
        var checkpoint = candidates.BeginVerification();
        foreach (var rowIndex in slice)
        {
            checkpoint.Step();
            if (rowIndex >= candidates.RowCount || !RowMatches(in rows[(int)rowIndex], names, query))
            {
                continue;
            }

            if (membership is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!membership.Contains(rowIndex))
                {
                    continue;
                }
            }

            destination.Add(FileEntry.Create(rootBlock, rowIndex));
        }
    }

    /// <summary>
    ///     The streaming form of <see cref="SearchExactRoot" />. The candidate buffer is owned by the iterator and
    ///     returned when it completes, is disposed or faults.
    /// </summary>
    static IEnumerable<FileEntry> EnumerateExactRoot(RootScan scan, NameQueryMatches names,
        CancellationToken cancellationToken)
    {
        var (rootBlock, query, membership) = scan;
        var block = rootBlock.Block;
        using var candidates = PostingCandidates.Gather(block, PostingCandidates.IdentifiersOf(names), cancellationToken);
        var checkpoint = candidates.BeginVerification();
        foreach (var rowIndex in candidates.Rows)
        {
            checkpoint.Step();
            if (rowIndex >= candidates.RowCount || !RowMatches(block, rowIndex, names, query))
            {
                continue;
            }

            if (membership is not null && !membership.Contains(rowIndex))
            {
                continue;
            }

            yield return FileEntry.Create(rootBlock, rowIndex);
        }
    }

    /// <summary>
    ///     One exact query per requested name, answered with one gather and one verification of each candidate row. Result
    ///     <c>i</c> belongs to <c>names[i]</c> and equals the single-name <see cref="Search" /> for it: the same rows in
    ///     ascending row order. <paramref name="filter" /> carries every predicate except the name.
    /// </summary>
    internal static List<FileEntry>[] SearchExactNames(RootBlock? rootBlock, IReadOnlyList<string> names,
        SearchQuery filter, uint? admittedViewRow = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var results = Enumerable.Range(0, names.Count).Select(_ => new List<FileEntry>()).ToArray();

        var subtree = SubtreeAnchor.Resolve(rootBlock, filter);
        if (rootBlock is not null && names.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var membership = SubtreeAnchor.CreateMembership(rootBlock, subtree, admittedViewRow);
            GatherExactNames(new RootScan(rootBlock, filter, membership), names, results, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    static void GatherExactNames(RootScan scan, IReadOnlyList<string> requested, List<FileEntry>[] results,
        CancellationToken cancellationToken)
    {
        var (rootBlock, filter, _) = scan;
        var block = rootBlock.Block;
        var passes = new List<NameQueryMatches>(requested.Count);
        try
        {
            // An identifier two requests resolve to (HEAD and head under ignore case) is gathered once and answers both.
            var requestsByIdentifier = new Dictionary<uint, List<int>>();
            for (var index = 0; index < requested.Count; index++)
            {
                var pass = new NameQueryMatches(block,
                    filter with { NamePattern = requested[index], MatchMode = NameMatchMode.Exact }, cancellationToken);
                passes.Add(pass);
                for (var candidate = 0; candidate < pass.CandidateCount; candidate++)
                {
                    var identifier = pass.CandidateAt(candidate);
                    if (!requestsByIdentifier.TryGetValue(identifier, out var indices))
                    {
                        requestsByIdentifier[identifier] = indices = [];
                    }

                    indices.Add(index);
                }
            }

            var identifiers = new uint[requestsByIdentifier.Count];
            requestsByIdentifier.Keys.CopyTo(identifiers, 0);
            using var candidates = PostingCandidates.Gather(block, identifiers, cancellationToken);
            VerifyExactNames(scan, candidates, requestsByIdentifier, results, cancellationToken);
        }
        finally
        {
            foreach (var pass in passes)
            {
                pass.Dispose();
            }
        }
    }

    static void VerifyExactNames(RootScan scan, PostingCandidates candidates,
        Dictionary<uint, List<int>> requestsByIdentifier, List<FileEntry>[] results, CancellationToken cancellationToken)
    {
        var (rootBlock, filter, membership) = scan;
        var rows = rootBlock.Block.Rows;
        var checkpoint = candidates.BeginVerification();
        foreach (var rowIndex in candidates.Rows)
        {
            checkpoint.Step();
            if (rowIndex >= candidates.RowCount)
            {
                continue;
            }

            ref readonly var row = ref rows[(int)rowIndex];
            if (!RowColumnsMatch(in row, filter) ||
                !requestsByIdentifier.TryGetValue(FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in row)),
                    out var requests))
            {
                continue;
            }

            if (membership is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!membership.Contains(rowIndex))
                {
                    continue;
                }
            }

            var entry = FileEntry.Create(rootBlock, rowIndex);
            foreach (var request in requests)
            {
                results[request].Add(entry);
            }
        }
    }
}

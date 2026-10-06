using System.Text;
using MFTLib.Index;

namespace TestProgram;

// The query verbs: search (with --stream and --exact), tree, open. One query builder serves search and largest.
partial class DriveScanner
{
    static string Describe(FileEntry entry)
    {
        if (!entry.IsValid || entry.IsDisposed)
        {
            return $"(entry unreadable: valid {entry.IsValid}, disposed {entry.IsDisposed})";
        }

        return $"{entry.Path} [{entry.Id.DriveLetter}:{entry.Id.RecordNumber}:{entry.Id.ProducerKind} " +
               $"{(entry.IsDirectory ? "directory" : "file")} {entry.Attributes} size {(entry.SizeKnown ? entry.Size : "unknown")} " +
               $"modified {entry.Modified:u} parent {entry.Parent?.Name} deleted {entry.IsDeleted}]";
    }

    static string Describe(JournalCheckpointLoss? loss)
    {
        return loss is null
            ? "none"
            : $"{loss.Cause} found during {loss.DetectedDuring} on {loss.DriveLetter}: maximum {loss.MaximumSize}, " +
              $"delta {loss.AllocationDelta}, behind {loss.BytesBehind}, would have retained {loss.SizeThatWouldHaveRetained}";
    }

    static string Describe(DriveStatus status)
    {
        return $"Drive {status.DriveLetter}: {status.State} {status.FailureKind}, {status.BlockSource}, slot {status.CacheSlot}, " +
               $"{status.LiveRowCount} rows, {status.SkippedRecordCount} skipped, {status.AccessDeniedSubtreeCount} denied, " +
               $"compaction {status.CompactionNeeded}, scanned {status.ScanTimestamp:u}; watch supported {status.WatchSupported} " +
               $"requested {status.WatchRequested} {status.WatchCatchUp} v{status.WatchStateVersion}, lost catch-ups " +
               $"{status.ConsecutiveLostCatchUps} of {FileIndex.LostCatchUpRecoveryLimit}; {status.MftProducerFailureMessage} " +
               $"{status.WatchFailureMessage}; checkpoint loss {Describe(status.CheckpointLoss)}";
    }

    void Show(IEnumerable<FileEntry> entries, IndexVerbArguments verb)
    {
        var all = entries.ToList();
        all.Take(Limit(verb)).ToList().ForEach(entry => _writeLine($"  {Describe(entry)}"));
        _writeLine($"{all.Count} entries (limit {Limit(verb)})");
    }

    static FileEntry? UnderOf(FileIndex index, IndexVerbArguments verb)
    {
        return verb.Text("--under") is { } path
            ? index.Find(path, CancellationToken.None) ?? throw new ArgumentException($"{path} is not in the index.")
            : null;
    }

    const int MaximumPathDepth = 128;

    void Search(FileIndex index, IndexVerbArguments verb)
    {
        var query = new SearchQuery(verb.Text("--name"), verb.Has("--case-sensitive"), UnderOf(index, verb),
            verb.Has("--directories") ? true : verb.Has("--files") ? false : null,
            verb.Number("--min-size"), verb.Number("--max-size"), verb.Date("--after"), verb.Date("--before"));
        _writeLine($"Query: name {query.NamePattern} (a glob with * or ?, else a substring), case sensitive " +
                   $"{query.CaseSensitive}, under {query.Under?.Path}, directories {query.Directories}, size " +
                   $"{query.MinimumSize}..{query.MaximumSize}, modified {query.ModifiedAfter:u}..{query.ModifiedBefore:u}");

        if (verb.Has("--exact"))
        {
            var exactName = verb.Require("--name");
            if (verb.Has("--stream"))
            {
                // Enumerate already applied every query predicate, so --exact adds name equality only;
                // re-applying the subtree predicate here would repeat a walk the query just did.
                using var enumerator = index.Enumerate(query, CancellationToken.None)
                    .Where(entry => NameMatchesExactly(entry, query, exactName))
                    .GetEnumerator();
                var shownExact = 0;
                while (shownExact < Limit(verb) && enumerator.MoveNext())
                {
                    shownExact++;
                    _writeLine($"  {Describe(enumerator.Current)}");
                }

                _writeLine($"{shownExact} entries streamed (limit {Limit(verb)})");
                return;
            }

            var candidates = index.FindByName(exactName, CancellationToken.None)
                .Where(entry => MatchesExact(entry, query, exactName));
            Show(candidates, verb);
            return;
        }

        if (!verb.Has("--stream"))
        {
            Show(index.Search(query, CancellationToken.None), verb);
            return;
        }

        // Streaming stops at the limit; leaving the scope disposes the enumerator with matches unread.
        using var enumeratorDefault = index.Enumerate(query, CancellationToken.None).GetEnumerator();
        var shown = 0;
        while (shown < Limit(verb) && enumeratorDefault.MoveNext())
        {
            shown++;
            _writeLine($"  {Describe(enumeratorDefault.Current)}");
        }

        _writeLine($"{shown} entries streamed (limit {Limit(verb)})");
    }

    static bool MatchesExact(FileEntry entry, SearchQuery query, string exactName)
    {
        if (!NameMatchesExactly(entry, query, exactName))
        {
            return false;
        }

        if (query.Directories is { } directories && entry.IsDirectory != directories)
        {
            return false;
        }

        if (query.Under is { } under && !IsUnder(entry, under))
        {
            return false;
        }

        if (query.MinimumSize is { } minimumSize && (!entry.SizeKnown || entry.Size < minimumSize))
        {
            return false;
        }

        if (query.MaximumSize is { } maximumSize && (!entry.SizeKnown || entry.Size > maximumSize))
        {
            return false;
        }

        if (query.ModifiedAfter is { } after && entry.Modified < after)
        {
            return false;
        }

        if (query.ModifiedBefore is { } before && entry.Modified > before)
        {
            return false;
        }

        return true;
    }

    static bool NameMatchesExactly(FileEntry entry, SearchQuery query, string exactName)
    {
        var comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(entry.Name, exactName, comparison);
    }

    static bool IsUnder(FileEntry candidate, FileEntry ancestor)
    {
        if (candidate.Id.DriveLetter != ancestor.Id.DriveLetter)
        {
            return false;
        }

        // Mirrors IndexNavigation.IsUnder: the ancestor reached by the final supported hop is
        // checked too, so a candidate exactly MaximumPathDepth hops below still matches.
        var visited = new HashSet<IndexRecordKey> { candidate.Id };
        var current = candidate;
        for (var hops = 0; ; hops++)
        {
            if (current.Id == ancestor.Id)
            {
                return true;
            }

            if (hops == MaximumPathDepth || current.Parent is not { } parent || !visited.Add(parent.Id))
            {
                return false;
            }

            current = parent;
        }
    }

    void ShowTree(FileIndex index, IndexVerbArguments verb)
    {
        FileEntry? start;
        if (verb.Text("--record-key")?.Split(':') is [var drive, var row, var producer])
        {
            var key = new IndexRecordKey(char.ToUpperInvariant(drive[0]), ulong.Parse(row, System.Globalization.CultureInfo.InvariantCulture), Enum.Parse<ProducerKind>(producer, true));
            start = index.Enumerate(new SearchQuery(null), CancellationToken.None).Where(entry => entry.Id == key)
                .Select(entry => (FileEntry?)entry).FirstOrDefault();
        }
        else
        {
            start = verb.Text("--path") is { } path
                ? index.Find(path, CancellationToken.None)
                : index.Root(verb.OpenedDrives[0], CancellationToken.None);
        }

        if (start is not { } entryAtStart)
        {
            _writeLine("Nothing found at that path or key.");
            return;
        }

        _writeLine($"Start: {Describe(entryAtStart)}");
        var visitedAncestors = new HashSet<IndexRecordKey> { entryAtStart.Id };
        var hops = 0;
        for (var parent = entryAtStart.Parent;
             parent is { } ancestor && hops < MaximumPathDepth && visitedAncestors.Add(ancestor.Id);
             parent = ancestor.Parent, hops++)
        {
            _writeLine($"  parent: {Describe(ancestor)}");
        }

        var depth = (int)(verb.Number("--depth") ?? 1);
        var directChildren = entryAtStart.Children(CancellationToken.None);
        if (depth <= 1)
        {
            foreach (var child in directChildren)
            {
                _writeLine($"  {Describe(child)}");
            }

            return;
        }

        // Group every live row by parent in one pass, without an Under predicate: the subtree
        // walk behind it throws on a parent chain past MaximumPathDepth, which would abort a
        // shallow listing over an undisplayed deep descendant. Traversal below reaches only rows
        // connected under the start entry, bounded by the requested depth and the visited set.
        Dictionary<IndexRecordKey, List<FileEntry>> groupedByParent = [];
        foreach (var entry in index.Enumerate(new SearchQuery(null), CancellationToken.None))
        {
            if (entry.Parent is { } parentEntry)
            {
                if (!groupedByParent.TryGetValue(parentEntry.Id, out var list))
                {
                    list = [];
                    groupedByParent[parentEntry.Id] = list;
                }

                list.Add(entry);
            }
        }

        var visited = new HashSet<IndexRecordKey> { entryAtStart.Id };
        foreach (var child in directChildren)
        {
            _writeLine($"  {Describe(child)}");
            if (child.IsDirectory)
            {
                PrintSubtree(child, depth, 1, groupedByParent, visited);
            }
        }
    }

    void PrintSubtree(FileEntry parent, int maximumDepth, int currentLevel,
        Dictionary<IndexRecordKey, List<FileEntry>> groupedByParent, HashSet<IndexRecordKey> visited)
    {
        if (currentLevel >= maximumDepth || !visited.Add(parent.Id))
        {
            return;
        }

        if (groupedByParent.TryGetValue(parent.Id, out var children))
        {
            foreach (var child in children)
            {
                _writeLine($"{new string(' ', (currentLevel + 1) * 2)}{Describe(child)}");
                if (child.IsDirectory)
                {
                    PrintSubtree(child, maximumDepth, currentLevel + 1, groupedByParent, visited);
                }
            }
        }
    }

    void OpenEntry(FileIndex index, IndexVerbArguments verb)
    {
        var path = verb.Require("--path");
        var entry = index.Find(path, CancellationToken.None) ?? throw new ArgumentException($"{path} is not in the index.");
        using var stream = entry.Open(FileAccess.Read);
        var buffer = new byte[(int)(verb.Number("--count") ?? 64)];
        var read = stream.Read(buffer, 0, buffer.Length);
        _writeLine($"{Describe(entry)}: first {read} of {stream.Length} bytes: {Encoding.UTF8.GetString(buffer, 0, read)}");
    }
}

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

    void Search(FileIndex index, IndexVerbArguments verb)
    {
        if (verb.Has("--exact"))
        {
            Show(index.FindByName(verb.Require("--name"), CancellationToken.None), verb);
            return;
        }

        var query = new SearchQuery(verb.Text("--name"), verb.Has("--case-sensitive"), UnderOf(index, verb),
            verb.Has("--directories") ? true : verb.Has("--files") ? false : null,
            verb.Number("--min-size"), verb.Number("--max-size"), verb.Date("--after"), verb.Date("--before"));
        _writeLine($"Query: name {query.NamePattern} (a glob with * or ?, else a substring), case sensitive " +
                   $"{query.CaseSensitive}, under {query.Under?.Path}, directories {query.Directories}, size " +
                   $"{query.MinimumSize}..{query.MaximumSize}, modified {query.ModifiedAfter:u}..{query.ModifiedBefore:u}");
        if (!verb.Has("--stream"))
        {
            Show(index.Search(query, CancellationToken.None), verb);
            return;
        }

        // Streaming stops at the limit; leaving the scope disposes the enumerator with matches unread.
        using var enumerator = index.Enumerate(query, CancellationToken.None).GetEnumerator();
        var shown = 0;
        while (shown < Limit(verb) && enumerator.MoveNext())
        {
            shown++;
            _writeLine($"  {Describe(enumerator.Current)}");
        }

        _writeLine($"{shown} entries streamed (limit {Limit(verb)})");
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
        for (var parent = entryAtStart.Parent; parent is { } ancestor; parent = ancestor.Parent)
        {
            _writeLine($"  parent: {Describe(ancestor)}");
        }

        PrintChildren(entryAtStart, (int)(verb.Number("--depth") ?? 1), 1);
    }

    void PrintChildren(FileEntry parent, int depth, int level)
    {
        foreach (var child in parent.Children(CancellationToken.None))
        {
            _writeLine($"{new string(' ', level * 2)}{Describe(child)}");
            if (level < depth && child.IsDirectory)
            {
                PrintChildren(child, depth, level + 1);
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

using System.Globalization;
using MFTLib.Index;

namespace SampleProgram.Direct;

partial class SampleHost
{
    Task RunVerbAsync(DirectArguments parsed, CancellationToken cancellationToken)
    {
        var source = CreateSourceNative(parsed);
        var root = parsed.Source is SourceKind.Dump ? null : ResolveRootNative(parsed.Drive);
        return RunSourceAsync(parsed, source, root, cancellationToken);
    }

    internal async Task RunSourceAsync(DirectArguments parsed, MftIndexSource source,
        IndexedRoot? root, CancellationToken cancellationToken)
    {
        await using var index = await OpenIndexAsync(source, root, cancellationToken).ConfigureAwait(false);
        WriteVerb(index, parsed, cancellationToken);
    }

    void WriteVerb(FileIndex index, DirectArguments parsed, CancellationToken cancellationToken)
    {
        switch (parsed.Verb)
        {
            case DirectVerb.Scan:
                WriteStatus(index.Status);
                WriteInventory(index, cancellationToken);
                break;
            case DirectVerb.Search:
                Search(index, parsed, cancellationToken);
                break;
            case DirectVerb.Tree:
                var start = parsed.Path is null ? index.Root(cancellationToken) : Resolve(index, parsed.Path, cancellationToken);
                _output.WriteLine(start.Path);
                WriteTree(start.RecordKey.RecordNumber, ChildLookup(index.Enumerate(new SearchQuery(null), cancellationToken)), 1, parsed.Depth);
                break;
            case DirectVerb.Children:
                var parent = parsed.Path is null ? index.Root(cancellationToken) : Resolve(index, parsed.Path, cancellationToken);
                WriteRows(parent.Children(cancellationToken).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase), parsed);
                break;
            case DirectVerb.Open:
                OpenFile(Resolve(index, parsed.Path, cancellationToken));
                break;
            case DirectVerb.Largest:
                var under = ResolveOptional(index, parsed.Under, cancellationToken);
                WriteRows(LargestFiles.Find(index, parsed.Count, under, cancellationToken), parsed);
                break;
            case DirectVerb.DuplicateNames:
                foreach (var group in DuplicateNameSieve.Find(index, cancellationToken).Take(parsed.Count))
                {
                    _output.WriteLine($"{group.Name}: {group.Entries.Count} entries, first {group.Entries[0].Path}");
                }

                break;
        }
    }

    // One mixed-row pass explains what the scan retained without materializing names or entry lists.
    void WriteInventory(FileIndex index, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<RootKey, (long Files, long Directories, long Deleted)>();
        var freshness = new Dictionary<RootKey, (long UnknownSize, long UnknownLastWrite, long Hidden, DateTime Newest)>();
        foreach (var row in index.EnumerateRows(new SearchQuery(null, IncludeDeleted: true), cancellationToken))
        {
            counts.TryGetValue(row.Root, out var count);
            if (row.IsDeleted)
            {
                count.Deleted++;
            }
            else if (row.IsDirectory)
            {
                count.Directories++;
            }
            else
            {
                count.Files++;
                freshness.TryGetValue(row.Root, out var seen);
                seen.UnknownSize += row.IsSizeKnown ? 0 : 1;
                seen.UnknownLastWrite += row.IsLastWriteTimeKnown ? 0 : 1;
                seen.Hidden += (row.Attributes & FileAttributes.Hidden) != 0 ? 1 : 0;
                seen.Newest = row.IsLastWriteTimeKnown && row.LastWriteTime > seen.Newest ? row.LastWriteTime : seen.Newest;
                freshness[row.Root] = seen;
            }

            counts[row.Root] = count;
        }

        var root = index.Status;
        cancellationToken.ThrowIfCancellationRequested();
        counts.TryGetValue(root.Root, out var rootCount);
        _output.WriteLine($"{root.Root}: {rootCount.Files} live files, {rootCount.Directories} live directories, {rootCount.Deleted} retained deleted rows");
        freshness.TryGetValue(root.Root, out var rootSeen);
        _output.WriteLine($"{root.Root}: {rootSeen.UnknownSize} files with unknown size, {rootSeen.UnknownLastWrite} with unknown last-write time, {rootSeen.Hidden} hidden, newest known last write {rootSeen.Newest:u}");
    }

    /// <summary>The query a search runs: every flag lands on the matching <see cref="SearchQuery" /> member.</summary>
    internal static SearchQuery BuildQuery(DirectArguments parsed, FileEntry? under)
    {
        var mode = parsed.Exact ? NameMatchMode.Exact
            : parsed.Name?.IndexOfAny(['*', '?']) >= 0 ? NameMatchMode.Glob : NameMatchMode.Substring;
        return new SearchQuery(parsed.Name, mode, parsed.CaseSensitive, under, parsed.Directories, parsed.MinimumSize,
            parsed.MaximumSize, parsed.After, parsed.Before, parsed.IncludeFreed);
    }

    void Search(FileIndex index, DirectArguments parsed, CancellationToken cancellationToken)
    {
        var under = ResolveOptional(index, parsed.Under, cancellationToken);
        var query = BuildQuery(parsed, under);
        // The streaming form holds a snapshot borrow until it is disposed; Take stops it as soon as the limit is met.
        var matches = parsed.Stream ? index.Enumerate(query, cancellationToken) : index.Search(query, cancellationToken);
        WriteRows(matches.Take(parsed.Limit), parsed);
    }

    static FileEntry? ResolveOptional(FileIndex index, string? path, CancellationToken cancellationToken)
    {
        return path is null ? null : Resolve(index, path, cancellationToken);
    }

    static FileEntry Resolve(FileIndex index, string? path, CancellationToken cancellationToken)
    {
        return index.Find(path ?? string.Empty, cancellationToken) ?? throw new FileNotFoundException($"No entry at {path}.");
    }

    void WriteRows(IEnumerable<FileEntry> entries, DirectArguments parsed)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            var size = entry.IsDirectory ? "<dir>" : entry.IsSizeKnown ? entry.Size.ToString(CultureInfo.InvariantCulture) : "?";
            _output.WriteLine(parsed.IncludeFreed ? $"{entry.Path}  {size}  deleted={entry.IsDeleted}" : $"{entry.Path}  {size}");
            count++;
        }

        _output.WriteLine($"{count} entries");
    }

    /// <summary>Groups every row under its parent's record number in one pass, so a tree never rescans the drive per directory.</summary>
    internal static Dictionary<ulong, List<FileEntry>> ChildLookup(IEnumerable<FileEntry> rows)
    {
        var lookup = new Dictionary<ulong, List<FileEntry>>();
        foreach (var row in rows)
        {
            if (row.Parent is not { } parent || parent.RecordKey.RecordNumber == row.RecordKey.RecordNumber)
            {
                continue;
            }

            if (!lookup.TryGetValue(parent.RecordKey.RecordNumber, out var siblings))
            {
                lookup[parent.RecordKey.RecordNumber] = siblings = [];
            }

            siblings.Add(row);
        }

        return lookup;
    }

    void WriteTree(ulong directory, Dictionary<ulong, List<FileEntry>> lookup, int level, int depth)
    {
        if (level > depth || !lookup.TryGetValue(directory, out var children))
        {
            return;
        }

        foreach (var child in children.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            _output.WriteLine($"{new string(' ', level * 2)}{child.Name}{(child.IsDirectory ? "/" : string.Empty)}");
            if (child.IsDirectory)
            {
                WriteTree(child.RecordKey.RecordNumber, lookup, level + 1, depth);
            }
        }
    }

    // Describes the entry, then opens it: a live entry opens through the volume, a dump entry cannot be opened and the library says so.
    void OpenFile(FileEntry entry)
    {
        var key = entry.RecordKey;
        var lastWriteTime = entry.IsLastWriteTimeKnown
            ? entry.LastWriteTime.ToString("u", CultureInfo.InvariantCulture)
            : "<unknown>";
        _output.WriteLine($"{entry.ToString()}: record {key.RecordNumber} on {key.Root} ({key.ProducerKind}), {entry.Attributes}, modified {lastWriteTime}, parent {entry.Parent?.Path}, valid {entry.IsValid}, disposed {entry.IsDisposed}");
        using var stream = entry.Open(FileAccess.Read);
        var buffer = new byte[16];
        var read = stream.Read(buffer, 0, buffer.Length);
        _output.WriteLine($"{stream.Length} bytes, first {read}: {Convert.ToHexString(buffer, 0, read)}");
    }
}

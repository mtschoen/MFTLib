using System.Globalization;
using MFTLib.Index;

namespace SampleProgram.Direct;

partial class SampleHost
{
    async Task RunVerbAsync(DirectArguments parsed, CancellationToken cancellationToken)
    {
        await using var index = await OpenIndexAsync(parsed, cancellationToken).ConfigureAwait(false);
        var letter = char.ToUpperInvariant(parsed.Drive[0]);
        switch (parsed.Verb)
        {
            case DirectVerb.Scan:
                WriteStatus(index.Drives.Single());
                break;
            case DirectVerb.Search:
                Search(index, parsed, cancellationToken);
                break;
            case DirectVerb.Tree:
                var start = parsed.Path is null ? index.Root(letter, cancellationToken) : Resolve(index, parsed.Path, cancellationToken);
                _writeLine(start.Path);
                WriteTree(start, 1, parsed.Depth, cancellationToken);
                break;
            case DirectVerb.Open:
                OpenFile(Resolve(index, parsed.Path!, cancellationToken));
                break;
            case DirectVerb.Largest:
                var under = ResolveOptional(index, parsed.Under, cancellationToken);
                WriteRows(index.Largest(parsed.Count, under, cancellationToken), parsed);
                break;
            case DirectVerb.DuplicateNames:
                foreach (var group in index.DuplicateNames(cancellationToken).Take(parsed.Count))
                {
                    _writeLine($"{group.Name}: {group.Entries.Count} entries, first {group.Entries[0].Path}");
                }

                break;
        }
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

    static FileEntry Resolve(FileIndex index, string path, CancellationToken cancellationToken)
    {
        return index.Find(path, cancellationToken) ?? throw new FileNotFoundException($"No entry at {path}.");
    }

    void WriteRows(IEnumerable<FileEntry> entries, DirectArguments parsed)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            var size = entry.IsDirectory ? "<dir>" : entry.IsSizeKnown ? entry.Size.ToString(CultureInfo.InvariantCulture) : "?";
            _writeLine(parsed.IncludeFreed ? $"{entry.Path}  {size}  deleted={entry.IsDeleted}" : $"{entry.Path}  {size}");
            count++;
        }

        _writeLine($"{count} entries");
    }

    void WriteTree(FileEntry directory, int level, int depth, CancellationToken cancellationToken)
    {
        if (level > depth)
        {
            return;
        }

        foreach (var child in directory.Children(cancellationToken).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            _writeLine($"{new string(' ', level * 2)}{child.Name}{(child.IsDirectory ? "/" : string.Empty)}");
            if (child.IsDirectory)
            {
                WriteTree(child, level + 1, depth, cancellationToken);
            }
        }
    }

    // Describes the entry, then opens it: a live entry opens through the volume, a dump entry cannot be opened and the library says so.
    void OpenFile(FileEntry entry)
    {
        var key = entry.RecordKey;
        _writeLine($"{entry.Path}: record {key.RecordNumber} on {key.DriveLetter} ({key.ProducerKind}), {entry.Attributes}, modified {entry.LastWriteTime:u}, parent {entry.Parent?.Path}, valid {entry.IsValid}, disposed {entry.IsDisposed}");
        using var stream = entry.Open(FileAccess.Read);
        var buffer = new byte[16];
        var read = stream.Read(buffer, 0, buffer.Length);
        _writeLine($"{stream.Length} bytes, first {read}: {Convert.ToHexString(buffer, 0, read)}");
    }
}

using MFTLib.Index;

namespace TestProgram;

// The query verbs: search, enumerate, largest and duplicate-names. search and enumerate build the same
// SearchQuery from the same options and print it before they use it.
partial class DriveScanner
{
    const int DefaultLargestCount = 10;

    static int LimitOf(IndexVerbArguments verb)
    {
        return (int)(verb.Number("--limit") ?? IndexVerbSpecifications.DefaultLimit);
    }

    SearchQuery BuildSearchQuery(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        bool? directories = verb.Has("--directories") ? true : verb.Has("--files") ? false : null;
        var query = new SearchQuery(
            verb.Text("--name"),
            verb.Has("--case-sensitive"),
            FindUnder(index, verb, cancellationToken),
            directories,
            verb.Number("--min-size"),
            verb.Number("--max-size"),
            verb.Date("--after"),
            verb.Date("--before"));
        PrintQuery(query);
        return query;
    }

    static FileEntry? FindUnder(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        if (verb.Text("--under") is not { } path)
        {
            return null;
        }

        return index.Find(path, cancellationToken) ??
               throw new InvalidOperationException($"--under {path} is not in the index.");
    }

    void PrintQuery(SearchQuery query)
    {
        var pattern = query.NamePattern switch
        {
            null => "none (every name)",
            var text when text.Contains('*') || text.Contains('?') => $"{text} (a glob that must match the whole name)",
            var text => $"{text} (a substring)"
        };
        var kind = query.Directories switch
        {
            true => "directories only",
            false => "files only",
            null => "files and directories"
        };
        _writeLine("Effective query:");
        _writeLine($"  name pattern: {pattern}");
        _writeLine($"  case sensitive: {query.CaseSensitive}");
        _writeLine($"  under: {(query.Under is { } under ? under.Path : "the whole index")}");
        _writeLine($"  kind: {kind}");
        _writeLine($"  size: {Describe(query.MinimumSize, "any")} to {Describe(query.MaximumSize, "any")} bytes");
        _writeLine($"  modified: after {query.ModifiedAfter?.ToString("u") ?? "any time"}, " +
                   $"before {query.ModifiedBefore?.ToString("u") ?? "any time"}");
    }

    void SearchIndex(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        if (verb.Has("--exact"))
        {
            var name = verb.Text("--name")!;
            _writeLine($"Exact name match through FindByName({name}).");
            PrintFileEntries(index.FindByName(name, cancellationToken), LimitOf(verb));
            return;
        }

        var query = BuildSearchQuery(index, verb, cancellationToken);
        PrintFileEntries(index.Search(query, cancellationToken), LimitOf(verb));
    }

    void EnumerateIndex(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var query = BuildSearchQuery(index, verb, cancellationToken);
        var limit = LimitOf(verb);
        var kept = new List<(FileEntry Entry, string Path)>();
        var truncated = false;
        var enumerator = index.Enumerate(query, cancellationToken).GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                if (kept.Count == limit)
                {
                    truncated = true;
                    break;
                }

                var entry = enumerator.Current;
                // The path is copied while the enumeration is live, so the line below stays printable later.
                kept.Add((entry, entry.Path));
                _writeLine($"  {FormatFileEntry(entry)}");
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        _writeLine(truncated
            ? $"Stopped early at the limit of {limit}; the enumerator was disposed with matches unread."
            : $"Enumeration finished with {kept.Count} entries; the enumerator was disposed.");
        foreach (var (entry, path) in kept)
        {
            _writeLine($"  after disposal: {path} valid {entry.IsValid} disposed {entry.IsDisposed}");
        }
    }

    void ShowLargest(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var count = (int)(verb.Number("--count") ?? DefaultLargestCount);
        var under = FindUnder(index, verb, cancellationToken);
        _writeLine($"Largest {count} files under {(under is { } root ? root.Path : "the whole index")}:");
        PrintFileEntries(index.Largest(count, under, cancellationToken), count);
    }

    void ShowDuplicateNames(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var groups = index.DuplicateNames(cancellationToken);
        var limit = LimitOf(verb);
        _writeLine($"{groups.Count} names occur more than once; {Math.Min(limit, groups.Count)} shown (limit {limit}).");
        foreach (var group in groups.Take(limit))
        {
            _writeLine($"  {group.Name}: {group.Entries.Count} entries");
            foreach (var entry in group.Entries.Take(3))
            {
                _writeLine($"    {FormatFileEntry(entry)}");
            }
        }
    }
}

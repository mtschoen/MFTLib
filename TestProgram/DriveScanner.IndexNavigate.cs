using System.Text;
using MFTLib.Index;

namespace TestProgram;

// The navigation verbs: find-path, tree and open.
partial class DriveScanner
{
    const int DefaultTreeDepth = 1;
    const int DefaultOpenBytes = 64;
    const int MaximumParentChain = 256;

    void FindPathInIndex(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        foreach (var status in index.Drives)
        {
            _writeLine($"Root of drive {status.DriveLetter}: {FormatFileEntry(index.Root(status.DriveLetter, cancellationToken))}");
        }

        if (verb.Text("--path") is not { } path)
        {
            return;
        }

        var found = index.Find(path, cancellationToken);
        _writeLine(found is { } entry
            ? $"Find({path}) found {FormatFileEntry(entry)}"
            : $"Find({path}) found nothing.");
    }

    void ShowTree(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var start = SelectTreeStart(index, verb, cancellationToken);
        _writeLine($"Start: {FormatFileEntry(start)}");
        var parent = start.Parent;
        for (var step = 0; parent is { } ancestor && step < MaximumParentChain; step++)
        {
            _writeLine($"  parent: {FormatFileEntry(ancestor)}");
            parent = ancestor.Parent;
        }

        PrintChildren(start, (int)(verb.Number("--depth") ?? DefaultTreeDepth), LimitOf(verb), 1, cancellationToken);
    }

    FileEntry SelectTreeStart(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        if (verb.RecordKey() is { } key)
        {
            _writeLine($"Selecting record key {FormatKey(key)} from the inventory.");
            return index.Enumerate(new SearchQuery(null), cancellationToken).FirstOrDefault(entry => entry.Id == key) is
            { IsValid: true } selected
                ? selected
                : throw new InvalidOperationException($"No entry in the index has the key {FormatKey(key)}.");
        }

        if (verb.Text("--path") is { } path)
        {
            return index.Find(path, cancellationToken) ??
                   throw new InvalidOperationException($"{path} is not in the index.");
        }

        return index.Root(verb.Drives[0], cancellationToken);
    }

    void PrintChildren(FileEntry parent, int depth, int limit, int level, CancellationToken cancellationToken)
    {
        if (!parent.IsDirectory)
        {
            return;
        }

        var children = parent.Children(cancellationToken);
        var indent = new string(' ', level * 2);
        _writeLine($"{indent}{children.Count} children of {parent.Name}; {Math.Min(limit, children.Count)} shown (limit {limit}).");
        foreach (var child in children.Take(limit))
        {
            _writeLine($"{indent}- {FormatFileEntry(child)}");
            if (level < depth)
            {
                PrintChildren(child, depth, limit, level + 1, cancellationToken);
            }
        }
    }

    void OpenEntry(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var path = verb.RequiredText("--path");
        var entry = index.Find(path, cancellationToken) ?? throw new InvalidOperationException($"{path} is not in the index.");
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException($"{path} is a directory; open reads files.");
        }

        _writeLine($"Opening {FormatFileEntry(entry)}");
        using var stream = entry.Open(FileAccess.Read);
        var buffer = new byte[(int)(verb.Number("--bytes") ?? DefaultOpenBytes)];
        var read = stream.Read(buffer, 0, buffer.Length);
        _writeLine($"Stream length {stream.Length}; read the first {read} bytes:");
        _writeLine($"  hex  {Convert.ToHexString(buffer, 0, read)}");
        _writeLine($"  text {Encoding.UTF8.GetString(buffer, 0, read).ReplaceLineEndings(" ")}");
    }
}

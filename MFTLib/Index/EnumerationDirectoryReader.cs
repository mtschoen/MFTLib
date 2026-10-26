using System.IO.Enumeration;

namespace MFTLib.Index;

sealed class EnumerationDirectoryReader
{
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? _retainedFileNames;
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _prunedNames;
    readonly HashSet<string> _prunedPaths;
    readonly FileAttributes _prunedAttributes;
    readonly bool _pruneDotDirectories;
    readonly EnumerationOptions _enumerationOptions;

    public EnumerationDirectoryReader(EnumerationProducerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var walk = options.DirectoryWalk;
        _retainedFileNames = options.RetainedFileNames is null
            ? null
            : new HashSet<string>(options.RetainedFileNames, StringComparer.OrdinalIgnoreCase)
                .GetAlternateLookup<ReadOnlySpan<char>>();

        _prunedNames = new HashSet<string>(walk.PrunedDirectoryNames, DirectoryWalkOptions.PathComparer)
            .GetAlternateLookup<ReadOnlySpan<char>>();

        _prunedPaths = new HashSet<string>(walk.PathsWithin(options.RootDirectory), DirectoryWalkOptions.PathComparer);
        _prunedAttributes = walk.PrunedDirectoryAttributes;
        _pruneDotDirectories = walk.PruneDotDirectories;
        _enumerationOptions = EnumerationProducer.CreateDirectoryEnumerationOptions();
    }

    internal Action<string, int>? BeforeMoveNext { get; set; }

    public EnumerationDirectoryBuffer Read(EnumerationDirectoryWork directory, CancellationToken cancellationToken)
    {
        var result = new EnumerationDirectoryBuffer(directory);
        var observed = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enumerable = new FileSystemEnumerable<bool>(directory.Path,
                (ref entry) =>
                {
                    observed++;
                    var isDirectory = entry.IsDirectory;
                    if (!isDirectory && _retainedFileNames is { } retained && !retained.Contains(entry.FileName))
                    {
                        return false;
                    }

                    var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var canDescend = false;
                    if (isDirectory && !isReparsePoint && !IsPrunedByName(ref entry))
                    {
                        if (_prunedPaths.Count == 0)
                        {
                            canDescend = true;
                        }
                        else
                        {
                            var fullPath = entry.ToFullPath();
                            canDescend = !_prunedPaths.Contains(DirectoryWalkOptions.NormalizePath(fullPath));
                        }
                    }

                    var offset = result.Names.WrittenCount;
                    entry.FileName.CopyTo(result.Names.GetSpan(entry.FileName.Length));
                    result.Names.Advance(entry.FileName.Length);

                    var columns = new RowColumns(directory.RowIndex,
                        RowFlags.InUse | (isDirectory ? RowFlags.Directory : RowFlags.None),
                        (uint)entry.Attributes, isDirectory ? 0 : entry.Length,
                        entry.LastWriteTimeUtc.UtcDateTime.Ticks, SequenceNumber: 0);

                    result.Entries.Add(new EnumerationBufferedEntry(offset, entry.FileName.Length,
                        columns, canDescend));
                    return true;
                },
                _enumerationOptions);

            using var enumerator = enumerable.GetEnumerator();
            do
            {
                BeforeMoveNext?.Invoke(directory.Path, observed);
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (enumerator.MoveNext());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or DirectoryNotFoundException or IOException)
        {
            result.SubtreeSkipped = true;
        }

        return result;
    }

    bool IsPrunedByName(ref FileSystemEntry entry) =>
        (entry.Attributes & _prunedAttributes) != 0 ||
        (_pruneDotDirectories && entry.FileName is ['.', ..]) ||
        _prunedNames.Contains(entry.FileName);
}

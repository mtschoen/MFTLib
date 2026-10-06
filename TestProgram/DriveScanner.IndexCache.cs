using MFTLib.Index;

namespace TestProgram;

// The cache verbs: cache-inspect lists what is on disk without opening an index; cache-clear deletes
// from an explicitly named cache directory and says what happened to every file.
partial class DriveScanner
{
    static HashSet<char>? DriveFilter(IndexVerbArguments verb)
    {
        return verb.Drives.Count == 0 ? null : verb.Drives.ToHashSet();
    }

    void InspectCache(IndexVerbArguments verb)
    {
        var path = verb.Text("--cache-directory") ?? _cacheDirectory;
        if (path is null)
        {
            path = CacheDirectory.ResolveDefaultPath();
            _writeLine($"No directory given; using the default cache path {path}");
        }

        _writeLine($"Inspecting {path}");
        if (verb.Has("--ensure-created"))
        {
            var created = CacheDirectory.EnsureCreated(path);
            _writeLine($"EnsureCreated returned {created.FullName}; it exists: {Directory.Exists(created.FullName)}");
        }

        var drives = DriveFilter(verb);
        var plain = CacheDirectory.InspectCached(path, drives);
        var rejections = new List<CachedBlockRejection>();
        var statuses = CacheDirectory.InspectCached(path, drives, rejections.Add);
        _writeLine($"InspectCached over {(drives is null ? "every drive" : $"drives [{string.Join(", ", drives)}]")} " +
                   $"returned {plain.Count} blocks; with the rejection callback {statuses.Count} blocks and " +
                   $"{rejections.Count} rejected files.");
        var expected = verb.Tag();
        foreach (var status in statuses)
        {
            PrintCachedStatus(status, expected);
        }

        foreach (var rejection in rejections)
        {
            _writeLine($"  rejected {rejection.Path}: {rejection.Reason}");
        }
    }

    void PrintCachedStatus(CachedBlockStatus status, CacheTag? expected)
    {
        _writeLine($"  {FormatCachedFile(status.File)}");
        _writeLine($"    availability {status.Availability}; validation " +
                   (status.Validation is { } validation ? $"{validation} ({DescribeValidation(validation)})" : "not checked") +
                   $"; producer {status.ProducerKind?.ToString() ?? "unknown"}; root {status.RootDirectory ?? "unknown"}");
        if (status.CacheTag is not { } tag)
        {
            _writeLine("    cache tag: none readable");
            return;
        }

        if (expected is not { } wanted)
        {
            _writeLine($"    cache tag {tag}");
            return;
        }

        _writeLine($"    cache tag {tag} against expected {wanted}: == {tag == wanted}; != {tag != wanted}; " +
                   $"Equals {tag.Equals((object)wanted)}");
    }

    internal static string FormatCachedFile(CachedBlockFile file)
    {
        return $"{file.Path} [drive {file.DriveLetter}; volume serial {file.VolumeSerial}; {file.SizeBytes} bytes; " +
               $"last written {file.LastWriteTimeUtc:u}]";
    }

    /// <summary>The reason a block failed validation, in words.</summary>
    internal static string DescribeValidation(BlockValidationResult result)
    {
        return result switch
        {
            BlockValidationResult.Valid => "the block is complete and consistent",
            BlockValidationResult.WrongMagic => "the file does not start with the block signature",
            BlockValidationResult.WrongFormatVersion => "the block was written in a different format version",
            BlockValidationResult.Incomplete => "the block was never finished or is truncated",
            BlockValidationResult.WrongVolumeSerial => "the block belongs to another volume",
            BlockValidationResult.InconsistentRegions => "the block regions disagree about their sizes",
            BlockValidationResult.WrongRootDirectory => "the block indexes a different root directory",
            BlockValidationResult.InvalidNameDescriptor => "a name descriptor points outside the name pool",
            BlockValidationResult.WrongCacheTag => "the block carries another consumer cache tag",
            _ => "unrecognized result"
        };
    }

    void ClearCache(IndexVerbArguments verb)
    {
        var path = verb.Text("--cache-directory") ?? _cacheDirectory ?? CacheDirectory.ResolveDefaultPath();
        var drives = DriveFilter(verb);
        _writeLine($"Deleting cached blocks under {path} for " +
                   (drives is null ? "every drive." : $"drives [{string.Join(", ", drives)}]."));
        var results = CacheDirectory.DeleteCached(path, drives, line => _writeLine($"  deletion diagnostics: {line}"));
        foreach (var result in results)
        {
            _writeLine($"  {FormatCachedFile(result.File)}: {result.Outcome}; failure {result.FailureReason ?? "none"}");
        }

        foreach (var group in results.GroupBy(result => result.Outcome))
        {
            _writeLine($"{group.Count()} files: {group.Key}");
        }

        _writeLine($"{results.Count} cached blocks examined.");
    }
}

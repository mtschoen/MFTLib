namespace MFTLib;

/// <summary>
///     While broker diagnostics are enabled, drops journal entries for the diagnostics log
///     files - the broker's own and the client's, whose path the client forwards with
///     <c>--diag-log</c> - before JournalBatch frames are built. Without this filter every
///     logged frame line produces a USN record for the log file, which ships as another
///     JournalBatch, which gets logged again: a self-sustaining loop that drowns the watched
///     volume's real traffic (file-wizard#299, git-wizard#143). Matching is by file reference
///     number (record number plus sequence number), which survives renames of the log file.
///     The filter is inactive when diagnostics are off or when the consumer opted the entries
///     back in with <c>MFTLIB_BROKER_DIAG_INCLUDE_SELF=1</c>. Configured log paths are re-checked
///     on each batch so that a recreated or replaced log file's new file reference is tracked
///     while retaining prior references for rename handling.
/// </summary>
sealed class BrokerDiagnosticsLogFilter
{
    // Swappable for tests, matching the Kernel32 seam pattern: production resolves a path
    // to its 64-bit NTFS file reference number; null means unresolvable (the file does not
    // exist yet, the host is not Windows, or the open/query failed).
    internal static Func<string, ulong?> _resolveFileReference = ResolveFileReference;

    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;

    // Paths to resolve to file reference numbers. Resolution is evaluated on each
    // filtered batch, so a log file created after the watch armed or replaced mid-session
    // starts filtering from the next batch on while preserving rename handling for prior files.
    readonly List<string> _logPaths = [];

    // Watched drive letter (bare uppercase, as watch specs carry it) -> references to drop.
    readonly Dictionary<string, HashSet<ulong>> _referencesByDrive = new(StringComparer.OrdinalIgnoreCase);

    internal BrokerDiagnosticsLogFilter(string ownLogPath, string? clientLogPath)
    {
        var normalizedOwn = NormalizePath(ownLogPath);
        _logPaths.Add(normalizedOwn);
        if (!string.IsNullOrEmpty(clientLogPath))
        {
            var normalizedClient = NormalizePath(clientLogPath);
            if (!string.Equals(normalizedClient, normalizedOwn, StringComparison.OrdinalIgnoreCase))
            {
                _logPaths.Add(normalizedClient);
            }
        }
    }

    internal UsnJournalEntry[] Filter(string driveLetter, UsnJournalEntry[] entries)
    {
        ResolvePathsFor(driveLetter);
        if (entries.Length == 0 ||
            !_referencesByDrive.TryGetValue(driveLetter, out var references) ||
            references.Count == 0)
        {
            return entries;
        }

        var dropped = 0;
        foreach (var entry in entries)
        {
            if (references.Contains(FileReferenceOf(entry)))
            {
                dropped++;
            }
        }

        // No match: return the incoming array untouched, so a batch without log traffic
        // allocates nothing on this hot path.
        if (dropped == 0)
        {
            return entries;
        }

        var kept = new UsnJournalEntry[entries.Length - dropped];
        var index = 0;
        foreach (var entry in entries)
        {
            if (!references.Contains(FileReferenceOf(entry)))
            {
                kept[index++] = entry;
            }
        }

        return kept;
    }

    void ResolvePathsFor(string driveLetter)
    {
        foreach (var path in _logPaths)
        {
            if (!TryGetDriveLetter(path, out var pathDrive) ||
                !string.Equals(pathDrive, driveLetter, StringComparison.OrdinalIgnoreCase))
            {
                continue; // rooted on another drive; that drive's watch resolves it
            }

            if (_resolveFileReference(path) is not { } reference)
            {
                continue; // not created yet or unresolvable; retried on the next batch
            }

            if (!_referencesByDrive.TryGetValue(driveLetter, out var set))
            {
                set = [];
                _referencesByDrive[driveLetter] = set;
            }

            set.Add(reference);
        }
    }

    internal static ulong? ResolveFileReference(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Desired access 0 still answers GetFileInformationByHandle; ShareAll keeps the
        // process appending to the log undisturbed.
        using var handle = Kernel32._createFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid || !Kernel32._getFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    internal static void ResetToDefaults()
    {
        _resolveFileReference = ResolveFileReference;
    }

    static ulong FileReferenceOf(UsnJournalEntry entry) =>
        ((ulong)entry.SequenceNumber << 48) | entry.RecordNumber;

    // Extract the bare drive letter from a rooted path ("C:\...", "C:...", or extended "\\?\C:\...");
    // anything else (UNC, non-Windows root) never matches a watched drive and stays pending.
    internal static bool TryGetDriveLetter(string path, out string driveLetter)
    {
        driveLetter = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Standard Windows drive-letter path: "C:\...", "C:/...", "C:file"
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            driveLetter = char.ToUpperInvariant(path[0]).ToString();
            return true;
        }

        // Extended-length or device path: "\\?\C:\...", "\\.\C:\...", "\??\C:\...", "//?/C:/...", "//./C:/..."
        if (path.Length >= 6 &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
             path.StartsWith(@"\??\", StringComparison.Ordinal) ||
             path.StartsWith("//?/", StringComparison.Ordinal) ||
             path.StartsWith("//./", StringComparison.Ordinal)) &&
            path[5] == ':' &&
            char.IsAsciiLetter(path[4]))
        {
            driveLetter = char.ToUpperInvariant(path[4]).ToString();
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.Length >= 2 && fullPath[1] == ':' && char.IsAsciiLetter(fullPath[0]))
                {
                    driveLetter = char.ToUpperInvariant(fullPath[0]).ToString();
                    return true;
                }
            }
            catch
            {
                // Invalid path format
            }
        }

        return false;
    }

    static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        return path;
    }
}

using MFTLib.Index;

namespace SampleProgram.Watch;

// The watch mode attaches per-root views and queries their outermost available members through FileIndexSet.
partial class SampleHost
{
    // Returns how many root operations failed.
    async Task<int> WatchIndexAsync(FileIndexSet indexSet, WatchArguments parsed, List<FileIndex> ownedIndices,
        Func<RootKey, CancellationToken, Task<FileIndex>> openRoot, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        var ready = new List<FileIndex>();
        foreach (var index in indexSet.Indices)
        {
            var status = index.Status;
            await _output.WriteLineAsync($"=== Root {status.Root}: ===").ConfigureAwait(false);
            try
            {
                ReportRoot(parsed, status);
                ready.Add(index);
            }
            catch (Exception exception)
            {
                failureCount++;
                await _output.WriteLineAsync($"Error on root {status.Root}: {exception.Message}").ConfigureAwait(false);
            }

            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        if (ready.Count == 0)
        {
            return failureCount;
        }

        var counter = ReportChanges(ready);
        var operations = new List<RootOperationResult>();
        operations.AddRange(Report("start", await Task.WhenAll(ready.Select(index => index.StartWatchingAsync(cancellationToken))).ConfigureAwait(false)));
        operations.AddRange(Report("catch-up", await Task.WhenAll(ready.Select(index => index.WaitForCatchUpAsync(cancellationToken))).ConfigureAwait(false)));

        // The initial scan and the catch-up have settled and nothing queries the indexes until the watch ends, so the
        // sample hands the scanned pages back; the first query afterwards refaults what it reads.
        foreach (var index in ready)
        {
            _ = index.ReleaseResidentPages();
        }

        var run = new WatchRun(ownedIndices, ready, counter, operations, openRoot);
        var waited = await WaitWhileWatchingAsync(parsed, run, cancellationToken).ConfigureAwait(false);
        operations.AddRange(Report("stop", await Task.WhenAll(ready.Select(index => index.StopWatchingAsync(cancellationToken))).ConfigureAwait(false)));
        var finalSet = new FileIndexSet(ready);
        var rows = finalSet.Enumerate(new SearchQuery(null), cancellationToken).Count();
        await _output.WriteLineAsync($"Saw {counter.Count} changes {waited}; the index now holds {rows} entries").ConfigureAwait(false);
        await ReportSetQueriesAsync(finalSet, ready[0], cancellationToken).ConfigureAwait(false);

        return failureCount + run.FailedAdds + operations.Count(result => result.Outcome is RootOperationOutcome.Failed);
    }

    // Queries every index through the set: Find routes the first ready root's own path back to its index, Search
    // materializes the directories, and EnumerateRows totals file sizes without allocating an entry per row.
    async Task ReportSetQueriesAsync(FileIndexSet indexSet, FileIndex selected, CancellationToken cancellationToken)
    {
        var rootPath = selected.Root(cancellationToken).Path;
        var resolved = indexSet.Find(rootPath, cancellationToken);
        await _output.WriteLineAsync($"Find resolved {rootPath} to {(resolved is { } entry ? entry.Path : "nothing")}").ConfigureAwait(false);
        var directories = indexSet.Search(new SearchQuery(null, IsDirectory: true), cancellationToken).Count;
        await _output.WriteLineAsync($"Search found {directories} directories").ConfigureAwait(false);
        long fileCount = 0;
        long fileBytes = 0;
        foreach (var row in indexSet.EnumerateRows(new SearchQuery(null, IsDirectory: false), cancellationToken))
        {
            fileCount++;
            fileBytes += row.IsSizeKnown ? row.Size : 0;
        }

        await _output.WriteLineAsync($"EnumerateRows totalled {fileCount} files, {fileBytes} bytes").ConfigureAwait(false);
    }

    bool IsInteractive => _interactive ?? !Console.IsInputRedirected;

    // Timed/unattended runs read no input. An attended console accepts root additions until an empty line or EOF.
    async Task<string> WaitWhileWatchingAsync(WatchArguments parsed, WatchRun run, CancellationToken cancellationToken)
    {
        var drives = string.Join(", ", run.Ready.Select(index => index.Status.Root.ToString()));
        if (drives.Length == 0)
        {
            return "with no drive watched";
        }

        var seconds = parsed.Seconds ?? (IsUnattended() || !IsInteractive ? WatchArguments.DefaultSeconds : null);
        if (seconds is { } length)
        {
            await _output.WriteLineAsync($"Watching {drives} for {length} seconds.").ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(length), _timeProvider, cancellationToken).ConfigureAwait(false);
            return $"in {length} seconds";
        }

        await _output.WriteLineAsync($"Watching {drives}. Type add-root <path> to watch another root, or press Enter to stop.").ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Console reads cannot be interrupted; cancellation ends this wait and leaves only the read behind.
            var line = await Task.Run(_input.ReadLine, CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return line is null ? "until input ended" : "until Enter was pressed";
            }
            if (line.StartsWith("add-root ", StringComparison.OrdinalIgnoreCase))
            {
                await AddRootAsync(line[9..].Trim(), run, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _output.WriteLineAsync($"Unknown command '{line}'. Type add-root <path>, or press Enter to stop.").ConfigureAwait(false);
            }
        }
    }

    sealed class RootChangeCounter
    {
        readonly Lock _gate = new();
        FileEntry? _previousEntry;
        int _count;

        internal int Count => Volatile.Read(ref _count);

        // Counts the change and says whether it happened to the same file incarnation as the previous one. A handle
        // never equals the file that reused its row slot, so a delete followed by a create of the same record number
        // reads as a different file.
        internal bool Add(FileEntry entry)
        {
            lock (_gate)
            {
                var isSameFile = _previousEntry is { } previous && previous.Equals(entry);
                _previousEntry = entry;
                Interlocked.Increment(ref _count);
                return isSameFile;
            }
        }
    }

    // Prints the watch states, faults and each change as they happen, and counts the changes.
    RootChangeCounter ReportChanges(IEnumerable<FileIndex> indices)
    {
        var counter = new RootChangeCounter();
        foreach (var index in indices)
        {
            ReportIndexChanges(index, counter);
        }

        return counter;
    }

    void ReportIndexChanges(FileIndex index, RootChangeCounter counter)
    {
        index.WatchStateChanged += state => _output.WriteLine($"  watch {state.Volume}: {state.Watch.State} (version {state.Watch.StateVersion}){(state.Fault is { } fault ? $", fault {fault.Kind}" : string.Empty)}");
        index.WatchFaulted += fault => _output.WriteLine($"  watch fault {fault.Volume}: {fault.Kind}: {fault.Exception.Message}{(fault.Exception is JournalCatchUpLostException lost ? $" (recovery stopped: {lost.IsRecoveryStopped})" : string.Empty)}");
        index.Changed += change =>
        {
            var isSameFile = counter.Add(change.Entry);
            // The entry handle is live, so its columns are unreadable once a later change reuses its row slot;
            // the change's own path is the fact of this event.
            _output.WriteLine($"  {change.Kind}: {change.Path}{(change.PreviousPath is { } previous ? $" (was {previous})" : string.Empty)} at {change.Timestamp:u}{(isSameFile ? ", same file as the previous change" : string.Empty)}, file id {change.Entry.GetHashCode():x8}, root {change.Entry.Root}, entry {Path.GetFileName(change.Path)}");
        };
    }

    List<RootOperationResult> Report(string operation, IEnumerable<RootOperationResult> results)
    {
        var all = results.ToList();
        WriteResults(operation, all);
        return all;
    }
}

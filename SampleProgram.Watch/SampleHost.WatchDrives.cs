using MFTLib.Index;

namespace SampleProgram.Watch;

// The watch mode: one index over every drive is watched together, so one wait covers the whole machine.
partial class SampleHost
{
    // Returns how many drive operations failed.
    async Task<int> WatchIndexAsync(FileIndex index, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        var ready = new List<DriveKey>();
        foreach (var status in index.Drives)
        {
            await _output.WriteLineAsync($"=== Drive {status.Drive}: ===").ConfigureAwait(false);
            try
            {
                ReportDrive(parsed, status);
                ready.Add(status.Drive);
            }
            catch (Exception exception)
            {
                failureCount++;
                await _output.WriteLineAsync($"Error on drive {status.Drive}: {exception.Message}").ConfigureAwait(false);
            }

            await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        }

        if (ready.Count == 0)
        {
            return failureCount;
        }

        var counter = ReportChanges(index);
        var operations = new List<DriveOperationResult>();
        operations.AddRange(Report("start", await index.StartWatchingAsync(ready, cancellationToken).ConfigureAwait(false)));
        operations.AddRange(Report("catch-up", await index.WaitForCatchUpAsync(ready, cancellationToken).ConfigureAwait(false)));

        var waited = await WaitWhileWatchingAsync(parsed, ready.Select(drive => drive.ToString()), cancellationToken).ConfigureAwait(false);
        operations.AddRange(Report("stop", await index.StopWatchingAsync(ready, cancellationToken).ConfigureAwait(false)));
        var rows = index.Enumerate(new SearchQuery(null), cancellationToken).Count();
        await _output.WriteLineAsync($"Saw {counter.Count} changes {waited}; the index now holds {rows} entries").ConfigureAwait(false);

        return failureCount + operations.Count(result => result.Outcome is DriveOperationOutcome.Failed);
    }

    bool IsInteractive => _interactive ?? !Console.IsInputRedirected;

    // Waits for --seconds, or for a key press when attended at a console and no length was given; says how it waited.
    async Task<string> WaitWhileWatchingAsync(WatchArguments parsed, IEnumerable<string> letters, CancellationToken cancellationToken)
    {
        var drives = string.Join(", ", letters);
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

        await _output.WriteLineAsync($"Watching {drives}. Press any key to stop.").ConfigureAwait(false);
        // A blocked read cannot be cancelled, so cancellation ends the wait and leaves the read behind.
        await Task.Run(() => _input.Read(), CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        return "until a key was pressed";
    }

    sealed class DriveChangeCounter
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
    DriveChangeCounter ReportChanges(FileIndex index)
    {
        var counter = new DriveChangeCounter();
        index.WatchStateChanged += state => _output.WriteLine($"  watch {state.Drive}: {state.Watch.State} (version {state.Watch.StateVersion}){(state.Fault is { } fault ? $", fault {fault.Kind}" : string.Empty)}");
        index.WatchFaulted += fault => _output.WriteLine($"  watch fault {fault.Drive}: {fault.Kind}: {fault.Exception.Message}{(fault.Exception is JournalCatchUpLostException lost ? $" (recovery stopped: {lost.IsRecoveryStopped})" : string.Empty)}");
        index.Changed += change =>
        {
            var isSameFile = counter.Add(change.Entry);
            // The entry handle is live, so its columns are unreadable once a later change reuses its row slot;
            // the change's own path is the fact of this event.
            _output.WriteLine($"  {change.Kind}: {change.Path}{(change.PreviousPath is { } previous ? $" (was {previous})" : string.Empty)} at {change.Timestamp:u}{(isSameFile ? ", same file as the previous change" : string.Empty)}, file id {change.Entry.GetHashCode():x8}, drive {change.Entry.Drive}, entry {Path.GetFileName(change.Path)}");
        };
        return counter;
    }

    List<DriveOperationResult> Report(string operation, IEnumerable<DriveOperationResult> results)
    {
        var all = results.ToList();
        WriteResults(operation, all);
        return all;
    }
}

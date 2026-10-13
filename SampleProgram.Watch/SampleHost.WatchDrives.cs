using MFTLib.Index;

namespace SampleProgram.Watch;

// The watch mode: every drive's index is opened first and watched together, so one wait covers the whole machine.
partial class SampleHost
{
    internal Func<bool> _isInteractive = () => !Console.IsInputRedirected;

    // Returns how many drive operations failed.
    async Task<int> WatchDrivesAsync(MftIndexSource source, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var openFailures = 0;
        var operations = new List<DriveOperationResult>();
        var watched = new List<(string Letter, FileIndex Index, DriveChangeCounter Counter)>();
        try
        {
            foreach (var drive in parsed.Drives)
            {
                var letter = drive.TrimEnd(':');
                _writeLine($"=== Drive {letter}: ===");
                try
                {
                    var index = await OpenDriveAsync(source, parsed, letter, cancellationToken).ConfigureAwait(false);
                    watched.Add((letter, index, ReportChanges(index)));
                }
                catch (Exception exception)
                {
                    openFailures++;
                    _writeLine($"Error on drive {letter}: {exception.Message}");
                }

                _writeLine(string.Empty);
            }

            foreach (var (_, index, _) in watched)
            {
                var keys = index.Drives.Select(drive => drive.Drive).ToArray();
                operations.AddRange(Report("start", await index.StartWatchingAsync(keys, cancellationToken).ConfigureAwait(false)));
                operations.AddRange(Report("catch-up", await index.WaitForCatchUpAsync(keys, cancellationToken).ConfigureAwait(false)));
            }

            var waited = await WaitWhileWatchingAsync(parsed, watched.Select(entry => entry.Letter), cancellationToken).ConfigureAwait(false);
            foreach (var (letter, index, counter) in watched)
            {
                operations.AddRange(Report("stop", await index.StopWatchingAsync(index.Drives.Select(drive => drive.Drive), cancellationToken).ConfigureAwait(false)));
                var rows = index.Enumerate(new SearchQuery(null), cancellationToken).Count();
                _writeLine($"=== Drive {letter}: ===");
                _writeLine($"Saw {counter.Count} changes {waited}; the index now holds {rows} entries");
                _writeLine($"=== Drive {letter}: done ===");
            }
        }
        finally
        {
            foreach (var (_, index, _) in watched)
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
        }

        return openFailures + operations.Count(result => result.Outcome is DriveOperationOutcome.Failed);
    }

    // Waits for --seconds, or for a key press when attended at a console and no length was given; says how it waited.
    async Task<string> WaitWhileWatchingAsync(WatchArguments parsed, IEnumerable<string> letters, CancellationToken cancellationToken)
    {
        var drives = string.Join(", ", letters);
        if (drives.Length == 0)
        {
            return "with no drive watched";
        }

        var seconds = parsed.Seconds ?? (IsUnattended() || !_isInteractive() ? WatchArguments.DefaultSeconds : null);
        if (seconds is { } length)
        {
            _writeLine($"Watching {drives} for {length} seconds.");
            await _delay(TimeSpan.FromSeconds(length), cancellationToken).ConfigureAwait(false);
            return $"in {length} seconds";
        }

        _writeLine($"Watching {drives}. Press any key to stop.");
        await Task.Run(_readKey, cancellationToken).ConfigureAwait(false);
        return "until a key was pressed";
    }

    sealed class DriveChangeCounter
    {
        int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Add() => Interlocked.Increment(ref _count);
    }

    // Prints the watch states, faults and each change as they happen, and counts the changes.
    DriveChangeCounter ReportChanges(FileIndex index)
    {
        var counter = new DriveChangeCounter();
        index.WatchStateChanged += state => _writeLine($"  watch {state.Drive}: {state.Watch.State} (version {state.Watch.StateVersion}){(state.Fault is { } fault ? $", fault {fault.Kind}" : string.Empty)}");
        index.WatchFaulted += fault => _writeLine($"  watch fault {fault.Drive}: {fault.Kind}: {fault.Exception.Message}{(fault.Exception is JournalCatchUpLostException lost ? $" (recovery stopped: {lost.RecoveryStopped})" : string.Empty)}");
        index.Changed += change =>
        {
            counter.Add();
            _writeLine($"  {change.Kind}: {change.Path}{(change.PreviousPath is { } previous ? $" (was {previous})" : string.Empty)} at {change.Timestamp:u}, entry {change.Entry.Name}");
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

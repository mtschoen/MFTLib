using System.Diagnostics.CodeAnalysis;
using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    internal Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    async Task WatchDriveAsync(FileIndex index, int seconds, CancellationToken cancellationToken)
    {
        var changes = 0;
        index.WatchStateChanged += state => _writeLine($"  watch {state.DriveLetter}: {state.WatchCatchUpState} (version {state.WatchStateVersion}){(state.Fault is { } fault ? $", fault {fault.Kind}" : string.Empty)}");
        index.WatchFaulted += fault => _writeLine($"  watch fault {fault.DriveLetter}: {fault.Kind}: {fault.Exception.Message}{(fault.Exception is JournalCatchUpLostException lost ? $" (recovery stopped: {lost.RecoveryStopped})" : string.Empty)}");
        index.Changed += change =>
        {
            Interlocked.Increment(ref changes);
            _writeLine($"  {change.Kind}: {change.Path}{(change.PreviousPath is { } previous ? $" (was {previous})" : string.Empty)} at {change.Timestamp:u}, entry {change.Entry.Name}");
        };
        WriteResults("start", await index.StartWatchingAsync(cancellationToken).ConfigureAwait(false));
        WriteResults("catch-up", await index.WaitForCatchUpAsync(cancellationToken).ConfigureAwait(false));
        await _delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        WriteResults("stop", await index.StopWatchingAsync(cancellationToken).ConfigureAwait(false));
        var rows = index.Enumerate(new SearchQuery(null), cancellationToken).Count();
        _writeLine($"Saw {changes} changes in {seconds} seconds; the index now holds {rows} entries");
    }

    void WriteResults(string operation, IEnumerable<DriveOperationResult> results)
    {
        foreach (var result in results)
        {
            _writeLine($"  {operation} {result.DriveLetter}: {result.Outcome}{(result.Failure is { } failure ? $" ({failure.Message})" : string.Empty)}");
        }
    }

    // A cached open adds where the block came from, the watch state and any lost checkpoint to the status line.
    void WriteDriveDetail(DriveStatus status)
    {
        WriteStatus(status);
        _writeLine($"Cache {status.DriveLetter}: block {status.BlockSource}, slot {status.CacheSlot}, scanned {status.ScanTimestamp:u}, compaction needed {status.CompactionNeeded}");
        _writeLine($"Watch: {status.WatchCatchUpState} v{status.WatchStateVersion}, requested {status.WatchRequested}; lost catch-ups {status.ConsecutiveLostCatchUps}; denied subtrees {status.AccessDeniedSubtreeCount}{(status.WatchFailureMessage is { } failure ? $"; {failure}" : string.Empty)}");
        if (status.CheckpointLoss is { } loss)
        {
            _writeLine($"Checkpoint lost on {loss.DriveLetter}: {loss.Cause} during {loss.DetectedDuring}; {loss.BytesBehind} bytes behind, journal {loss.MaximumSize}/{loss.AllocationDelta}, {loss.SizeThatWouldHaveRetained} would have kept it");
        }
    }

    // With both sizes it grows the journal through the broker; with neither it reports the settings the drive has.
    [SuppressMessage("Interoperability", "CA1416", Justification = "A broker session, which the journal needs first, is only created on Windows")]
    async Task WriteJournalAsync(BrokerSession session, FileIndex index, char letter, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var settings = parsed.MaximumSize is { } maximum && parsed.AllocationDelta is { } delta
            ? await session.GrowUsnJournalAsync(letter, maximum, delta, cancellationToken).ConfigureAwait(false)
            : index.QueryUsnJournalSettings(letter);
        _writeLine($"Journal {letter}: maximum size {settings.MaximumSize}, allocation delta {settings.AllocationDelta}");
    }

    // Lists the cached blocks of the drives named (all when none), and removes them with --clear.
    int RunCache(WatchArguments parsed)
    {
        var directory = parsed.CacheDirectory ?? _cacheDirectory ?? CacheDirectory.ResolveDefaultPath();
        var letters = parsed.Drives.Count == 0 ? null : parsed.Drives.Select(drive => char.ToUpperInvariant(drive[0])).ToHashSet();
        foreach (var block in CacheDirectory.InspectCached(directory, letters, rejection => _writeLine($"  rejected {rejection.Path}: {rejection.Reason}")))
        {
            _writeLine($"{block.File.DriveLetter}: {block.File.Path} (serial {block.File.VolumeSerial}, root {block.RootDirectory}), {block.Availability}, {block.File.SizeBytes} bytes, validation {block.Validation}, {block.ProducerKind}, tag {block.CacheTag}, written {block.File.LastWriteTime:u}");
        }

        if (parsed.Clear)
        {
            foreach (var result in CacheDirectory.DeleteCached(directory, letters, _writeLine))
            {
                _writeLine($"  {result.File.DriveLetter}: {result.Outcome}{(result.FailureReason is { } reason ? $" ({reason})" : string.Empty)}");
            }
        }

        return 0;
    }

    int WriteElevationStatus()
    {
        _writeLine($"Process: {_getProcessPath()}");
        _writeLine($"Elevated: {_isElevated()}; can self-elevate: {_canSelfElevate()}; unattended: {IsUnattended()}");
        return 0;
    }
}

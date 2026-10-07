using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    // Lets a test pass the watch window without waiting for it.
    internal Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    // Watches the drive for the window, printing each state change, fault and file change with its kind as it arrives.
    async Task WatchDriveAsync(FileIndex index, char letter, int seconds, CancellationToken cancellationToken)
    {
        var changes = 0;
        index.WatchStateChanged += state => _writeLine($"  watch {state.DriveLetter}: {state.WatchCatchUpState} (version {state.WatchStateVersion})");
        index.WatchFaulted += fault => _writeLine($"  watch fault {fault.DriveLetter}: {fault.Kind}: {fault.Exception.Message}");
        index.Changed += change =>
        {
            Interlocked.Increment(ref changes);
            _writeLine($"  {change.Kind}: {change.Path}{(change.PreviousPath is { } previous ? $" (was {previous})" : string.Empty)}");
        };
        await index.StartWatchingAsync(letter, cancellationToken).ConfigureAwait(false);
        await index.WaitForCatchUpAsync(letter, cancellationToken).ConfigureAwait(false);
        await _delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        await index.StopWatchingAsync(letter, cancellationToken).ConfigureAwait(false);
        var rows = index.Enumerate(new SearchQuery(null), cancellationToken).Count();
        _writeLine($"Saw {changes} changes in {seconds} seconds; the index now holds {rows} entries");
    }

    // With both sizes it grows the journal through the broker; with neither it reports the settings the drive has.
    async Task WriteJournalAsync(BrokerSession session, FileIndex index, char letter, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var settings = parsed.MaximumSize is { } maximum && parsed.AllocationDelta is { } delta
            ? await session.GrowUsnJournalAsync(letter, maximum, delta, cancellationToken).ConfigureAwait(false)
            : OperatingSystem.IsWindows()
                ? index.QueryUsnJournalSettings(letter)
                : throw new PlatformNotSupportedException("The journal settings are read on Windows only.");
        _writeLine($"Journal {letter}: maximum size {settings.MaximumSize}, allocation delta {settings.AllocationDelta}");
    }

    // Lists the cached blocks of the drives named (all when none), and removes them with --clear.
    int RunCache(WatchArguments parsed)
    {
        var directory = parsed.CacheDirectory ?? _cacheDirectory ?? CacheDirectory.ResolveDefaultPath();
        var letters = parsed.Drives.Count == 0 ? null : parsed.Drives.Select(drive => char.ToUpperInvariant(drive[0])).ToHashSet();
        foreach (var block in CacheDirectory.InspectCached(directory, letters, rejection => _writeLine($"  rejected {rejection.Path}: {rejection.Reason}")))
        {
            _writeLine($"{block.File.DriveLetter}: {block.Availability}, {block.File.SizeBytes} bytes, validation {block.Validation}, {block.ProducerKind}, tag {block.CacheTag}, written {block.File.LastWriteTime:u}");
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

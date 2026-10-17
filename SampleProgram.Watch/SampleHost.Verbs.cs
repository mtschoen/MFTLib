using System.Diagnostics.CodeAnalysis;
using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    internal Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    void WriteResults(string operation, IEnumerable<DriveOperationResult> results)
    {
        foreach (var result in results)
        {
            _writeLine($"  {operation} {result.Drive.ToString()}: {result.Outcome}{(result.Failure is { } failure ? $" ({failure.Message})" : string.Empty)}");
        }
    }

    // A cached open adds where the block came from, the watch state and any lost checkpoint to the status line.
    void WriteDriveDetail(DriveStatus status)
    {
        WriteStatus(status);
        var block = RequireReadyBlock(status);
        _writeLine($"Cache {status.Drive}: block {block.Source}, slot {block.CacheSlot?.ToString() ?? "none"}, scanned {block.ScanTimestamp:u}, capacity exhausted {block.IsCapacityExhausted}");
        _writeLine($"Watch: {status.Watch.State} v{status.Watch.StateVersion}, requested {status.Watch.IsRequested}; recovery stopped {status.Watch.IsRecoveryStopped}; denied subtrees {block.AccessDeniedSubtreeCount}{(status.Watch.FailureMessage is { } failure ? $"; {failure}" : string.Empty)}");
        if (status.Watch.CheckpointLoss is { } loss)
        {
            _writeLine($"Checkpoint lost on {loss.Drive}: {loss.Cause} during {loss.DetectedDuring}; {loss.BytesBehind} bytes behind, journal {loss.JournalSettings.MaximumSize}/{loss.JournalSettings.AllocationDelta}, {loss.SizeThatWouldHaveRetained} would have kept it");
            if (loss.GrowthTargetRefusalReason is { } reason)
            {
                _writeLine($"Journal growth target unavailable: {reason}");
            }
        }
    }

    // With both sizes it grows the journal through the broker; with neither it reports the settings the drive has.
    [SuppressMessage("Interoperability", "CA1416", Justification = "A broker session, which the journal needs first, is only created on Windows")]
    async Task WriteJournalAsync(BrokerSession session, FileIndex index, char letter, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var settings = parsed.MaximumSize is { } maximum && parsed.AllocationDelta is { } delta
            ? await session.GrowUsnJournalAsync(letter,
                new UsnJournalSettings { MaximumSize = maximum, AllocationDelta = delta }, cancellationToken).ConfigureAwait(false)
            : index.QueryUsnJournalSettings(letter);
        _writeLine($"Journal {letter}: maximum size {settings.MaximumSize}, allocation delta {settings.AllocationDelta}");
    }

    // Lists the cached blocks of the drives named (all when none), and removes them with --clear. Each scan policy keeps its
    // blocks in its own policy-* folder under the cache directory, so the root and every such folder are covered.
    int RunCache(WatchArguments parsed)
    {
        var root = ResolveCacheDirectory(parsed);
        var letters = parsed.Drives.Count == 0 ? null : parsed.Drives.Select(drive => new DriveKey(drive[0])).ToArray();
        IEnumerable<string> policies = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, WatchArguments.PolicyDirectoryPrefix + "*").Order(StringComparer.Ordinal)
            : [];
        foreach (var directory in policies.Prepend(root))
        {
            var origin = directory == root ? "root" : Path.GetFileName(directory);
            foreach (var block in CacheDirectory.InspectCached(directory, letters, rejection => _writeLine($"  rejected {rejection.Path}: {rejection.Reason}")))
            {
                _writeLine($"{block.File.Drive}: {block.File.Path} (serial {block.File.VolumeSerial}, root {block.RootDirectory}), {block.Availability}{(block.Validation is { } reason ? $" ({reason})" : "")}, {block.File.Size} bytes, {block.ProducerKind}, tag {block.CacheTag}, written {block.File.LastWriteTime:u}, policy {origin}");
            }

            if (parsed.Clear)
            {
                foreach (var result in CacheDirectory.DeleteCached(directory, letters))
                {
                    _writeLine($"  {result.File.Drive}: {result.Outcome}{(result.FailureReason is { } reason ? $" ({reason})" : string.Empty)}");
                }
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

using System.Diagnostics.CodeAnalysis;
using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

partial class SampleHost
{
    void WriteResults(string operation, IEnumerable<DriveOperationResult> results)
    {
        foreach (var result in results)
        {
            _output.WriteLine($"  {operation} {result.Drive.ToString()}: {result.Outcome}{(result.Failure is { } failure ? $" ({failure.Message})" : string.Empty)}");
        }
    }

    // A cached open adds where the block came from, the watch state and any lost checkpoint to the status line.
    void WriteDriveDetail(DriveStatus status)
    {
        WriteStatus(status);
        var block = RequireReadyBlock(status);
        _output.WriteLine($"Cache {status.Drive}: block {block.Origin}, location {block.CacheLocation?.ToString() ?? "none"}, scanned {block.ScanTimestamp:u}, capacity exhausted {block.IsCapacityExhausted}");
        _output.WriteLine($"Watch: {status.Watch.State} v{status.Watch.StateVersion}, requested {status.Watch.IsRequested}; recovery stopped {status.Watch.IsRecoveryStopped}; denied subtrees {block.AccessDeniedSubtreeCount}{(status.Watch.FailureMessage is { } failure ? $"; {failure}" : string.Empty)}");
        if (status.Watch.CheckpointLoss is { } loss)
        {
            _output.WriteLine($"Checkpoint lost on {loss.Drive}: {loss.Cause} during {loss.DetectedDuring}; {loss.BytesBehind} bytes behind, journal {loss.JournalSettings.MaximumSize}/{loss.JournalSettings.AllocationDelta}, {loss.SizeThatWouldHaveRetained} would have kept it");
            if (loss.GrowthTargetRefusalReason is { } reason)
            {
                _output.WriteLine($"Journal growth target unavailable: {reason}");
            }
        }
    }

    // With both sizes it grows the journal through the broker; with neither it reports the settings the drive has.
    [SuppressMessage("Interoperability", "CA1416", Justification = "A broker session, which the journal needs first, is only created on Windows")]
    async Task WriteJournalAsync(BrokerSession session, FileIndex index, DriveKey drive, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var settings = parsed.MaximumSize is { } maximum && parsed.AllocationDelta is { } delta
            ? await session.GrowUsnJournalAsync(drive,
                new UsnJournalSettings { MaximumSize = maximum, AllocationDelta = delta }, cancellationToken).ConfigureAwait(false)
            : index.QueryUsnJournalSettings(drive);
        await _output.WriteLineAsync($"Journal {drive}: maximum size {settings.MaximumSize}, allocation delta {settings.AllocationDelta}").ConfigureAwait(false);
    }

    // Lists the cached blocks of the drives named (all when none) in the cache directory, and removes them with --clear.
    int RunCache(WatchArguments parsed)
    {
        var exitCode = 0;
        var directory = parsed.CacheDirectory ?? string.Empty;
        var drives = parsed.Drives.Count == 0 ? null : parsed.Drives.Select(drive => new DriveKey(drive[0])).ToArray();
        foreach (var block in CacheDirectory.InspectCached(directory, drives, rejection => _output.WriteLine($"  rejected {rejection.Path}: {rejection.Reason}")))
        {
            _output.WriteLine($"{block.File.Drive}: {block.File.Path} (serial {block.File.VolumeSerial}, root {block.RootDirectory}), {block.Availability}{(block.Validation is { } reason ? $" ({reason})" : "")}, {block.File.Size} bytes, {block.ProducerKind}, tag {block.CacheTag}, written {block.File.LastWriteTime:u}");
        }

        if (parsed.Clear)
        {
            foreach (var result in CacheDirectory.DeleteCached(directory, drives))
            {
                _output.WriteLine($"  {result.File.Drive}: {result.Outcome}{(result.FailureReason is { } reason ? $" ({reason})" : string.Empty)}");
                if (result.Outcome != CachedBlockDeletionOutcome.Deleted)
                {
                    exitCode = 1;
                }
            }
        }

        return exitCode;
    }

    int WriteElevationStatus()
    {
        _output.WriteLine($"Process: {Environment.ProcessPath}");
        _output.WriteLine($"Elevated: {_isElevated()}; can self-elevate: {_canSelfElevate()}; unattended: {IsUnattended()}");
        return 0;
    }
}

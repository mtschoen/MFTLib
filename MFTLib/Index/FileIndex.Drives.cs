using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One entry per configured drive, keyed by upper-case drive letter, created with the index
    ///     and kept until it is disposed. Every watch and rescan operation on a drive acts on that
    ///     drive's entry only. The dictionary itself is never modified after construction.
    /// </summary>
    readonly Dictionary<DriveKey, DriveRuntime> _driveRuntimes = [];

    /// <summary>The drive's runtime, or <see cref="ArgumentException" /> for a drive not in this index.</summary>
    DriveRuntime GetDriveRuntime(DriveKey drive)
    {
        return _driveRuntimes.TryGetValue(drive.Validated(), out var runtime)
            ? runtime
            : throw new ArgumentException($"Drive {drive} is not part of this index.", nameof(drive));
    }

    /// <summary>A test seam: takes the drive's write gate, as a batch or a commit would.</summary>
    internal Task WaitForDriveWriteGateForTest(DriveKey drive, CancellationToken cancellationToken = default) =>
        GetDriveRuntime(drive).WriteGate.WaitAsync(cancellationToken);

    /// <summary>A test seam: releases the write gate <see cref="WaitForDriveWriteGateForTest" /> took.</summary>
    internal void ReleaseDriveWriteGateForTest(DriveKey drive) => GetDriveRuntime(drive).WriteGate.Release();

    /// <summary>
    ///     A test seam: true when neither the drive's lifecycle gate nor its write gate is held.
    ///     Takes each without waiting and releases it again.
    /// </summary>
    internal bool AreDriveGatesFreeForTest(DriveKey drive)
    {
        var runtime = GetDriveRuntime(drive);
        var free = true;
        foreach (var gate in new[] { runtime.LifecycleGate, runtime.WriteGate })
        {
            if (gate.Wait(0))
            {
                gate.Release();
            }
            else
            {
                free = false;
            }
        }

        return free;
    }

    /// <summary>
    ///     A test observation: the completion of the drive's current recovery ticket, when it has one.
    ///     Exposes exact recovery ticket completion identity under the state lock. Complements <see cref="IndexHooks" />.
    /// </summary>
    internal bool TryGetRecoveryCompletionForTest(DriveKey drive, [NotNullWhen(true)] out Task? completion)
    {
        var runtime = GetDriveRuntime(drive);
        lock (_stateLock)
        {
            completion = runtime.Recovery?.Completion;
            return completion is not null;
        }
    }

    /// <summary>A test seam: how many deliveries waited for another thread delivering the same drive.</summary>
    internal long ContendedWatchStateDeliveriesForTest =>
        _driveRuntimes.Values.Sum(runtime => runtime.ContendedWatchStateDeliveries);
}

using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The runtime for this index's configured root, created with the index and kept until it is disposed.
    /// </summary>
    readonly DriveRuntime _runtime;

    /// <summary>The root's runtime, or <see cref="ArgumentException" /> for a root not in this index.</summary>
    DriveRuntime GetDriveRuntime(RootKey drive)
    {
        return _runtime.Drive == drive.Validated()
            ? _runtime
            : throw new ArgumentException($"Drive {drive} is not part of this index.", nameof(drive));
    }

    /// <summary>A test seam: takes the drive's write gate, as a batch or a commit would.</summary>
    internal Task WaitForDriveWriteGateForTest(RootKey drive, CancellationToken cancellationToken = default) =>
        GetDriveRuntime(drive).WriteGate.WaitAsync(cancellationToken);

    /// <summary>A test seam: releases the write gate <see cref="WaitForDriveWriteGateForTest" /> took.</summary>
    internal void ReleaseDriveWriteGateForTest(RootKey drive) => GetDriveRuntime(drive).WriteGate.Release();

    /// <summary>
    ///     A test seam: true when neither the drive's lifecycle gate nor its write gate is held.
    ///     Takes each without waiting and releases it again.
    /// </summary>
    internal bool AreDriveGatesFreeForTest(RootKey drive)
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
    internal bool TryGetRecoveryCompletionForTest(RootKey drive, [NotNullWhen(true)] out Task? completion)
    {
        var runtime = GetDriveRuntime(drive);
        lock (_stateLock)
        {
            completion = runtime.Recovery?.Completion;
            return completion is not null;
        }
    }

    /// <summary>A test seam: how many deliveries waited for another thread delivering the same drive.</summary>
    internal long ContendedWatchStateDeliveriesForTest => _runtime.ContendedWatchStateDeliveries;
}

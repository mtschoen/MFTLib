using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     The runtime for this index's configured root, created with the index and kept until it is disposed.
    /// </summary>
    readonly IndexView _view;

    /// <summary>The root's runtime, or <see cref="ArgumentException" /> for a root not in this index.</summary>
    VolumeScan GetVolumeScan(RootKey root)
    {
        return _view.Root == root.Validated()
            ? _view.Scan
            : throw new ArgumentException($"Root {root} is not part of this index.", nameof(root));
    }

    /// <summary>A test seam: takes the root's write gate, as a batch or a commit would.</summary>
    internal Task WaitForRootWriteGateForTest(RootKey root, CancellationToken cancellationToken = default) =>
        GetVolumeScan(root).WriteGate.WaitAsync(cancellationToken);

    /// <summary>A test seam: releases the write gate <see cref="WaitForRootWriteGateForTest" /> took.</summary>
    internal void ReleaseRootWriteGateForTest(RootKey root) => GetVolumeScan(root).WriteGate.Release();

    /// <summary>
    ///     A test seam: true when neither the root's lifecycle gate nor its write gate is held.
    ///     Takes each without waiting and releases it again.
    /// </summary>
    internal bool AreRootGatesFreeForTest(RootKey root)
    {
        var runtime = GetVolumeScan(root);
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
    ///     A test observation: the completion of the root's current recovery ticket, when it has one.
    ///     Exposes exact recovery ticket completion identity under the state lock. Complements <see cref="IndexHooks" />.
    /// </summary>
    internal bool TryGetRecoveryCompletionForTest(RootKey root, [NotNullWhen(true)] out Task? completion)
    {
        var runtime = GetVolumeScan(root);
        lock (StateLock)
        {
            completion = runtime.Recovery?.Completion;
            return completion is not null;
        }
    }

    /// <summary>A test seam: how many deliveries waited for another thread delivering the same root.</summary>
    internal long ContendedWatchStateDeliveriesForTest => _view.ContendedWatchStateDeliveries;
}

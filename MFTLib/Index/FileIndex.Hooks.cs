namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Internal per-index hook container for test substitutions.
    /// </summary>
    internal IndexHooks Hooks { get; }

    /// <summary>
    ///     Per-instance test seams for <see cref="FileIndex" />.
    ///     Gate and recovery observation/control methods remain directly on <see cref="FileIndex" />:
    ///     <see cref="WaitForDriveWriteGateForTest" />, <see cref="ReleaseDriveWriteGateForTest" />,
    ///     <see cref="AreDriveGatesFreeForTest" />, <see cref="TryGetRecoveryCompletionForTest" />, and
    ///     <see cref="ContendedWatchStateDeliveriesForTest" />.
    /// </summary>
    internal sealed class IndexHooks
    {
        /// <summary>
        ///     Lists cache files for the stale ".retired-*" sweep; owner lock held, no state/write gate.
        ///     A hold must not wait for an operation needing the same ownership/lifecycle gate.
        /// </summary>
        internal Func<string, string, IEnumerable<string>> EnumerateCacheFiles { get; init; } =
            Directory.EnumerateFiles;

        /// <summary>
        ///     Invoked by the <see cref="Drives" /> getter before state-lock acquisition; no lifecycle/write/state lock held.
        ///     The caller can hold an outer lock, so a hold must not wait on one.
        /// </summary>
        internal Action? DrivesReadBeforeLock { get; set; }

        /// <summary>
        ///     Invoked after the counted borrow and state-lock release, before the first row is read.
        ///     Must not throw or wait for full disposal while holding the borrow.
        /// </summary>
        internal Action? ReaderAdmitted { get; set; }

        /// <summary>
        ///     Invoked with all lifecycle gates held, before any write gate or state lock; must not throw.
        ///     Cannot wait for an operation requiring those lifecycle gates.
        /// </summary>
        internal Action? LifecycleGatesTakenForDisposal { get; set; }

        /// <summary>
        ///     Invoked after the disposed flag is set and the state lock released, before the disposal token is cancelled.
        ///     Must not throw or recursively wait for disposal.
        /// </summary>
        internal Action? DisposedFlagSet { get; set; }

        /// <summary>
        ///     Invoked with own lifecycle and write gates held, before state lock and cancellation checkpoint.
        ///     Must not wait for own write/lifecycle gate.
        /// </summary>
        internal Action<DriveKey>? PublishInsideWriteGate { get; set; }

        /// <summary>
        ///     Invoked before lifecycle acquisition, no gates or state lock; awaited with the recovery token.
        ///     May park asynchronously, honoring cancellation.
        /// </summary>
        internal Func<DriveKey, CancellationToken, Task>? RecoveryBeforeLifecycleGate { get; set; }

        /// <summary>
        ///     Wraps ticket completion after lifecycle release, outside the state lock; must complete once.
        ///     Must not throw or wait for disposal, which waits for this ticket.
        /// </summary>
        internal Action<Action>? RecoveryCompletionWrapper { get; set; }

        /// <summary>
        ///     Invoked with the lifecycle gate held, no write/state lock, before disposed/request state is read.
        ///     Must not await disposal/start/rescan that needs the held lifecycle gate.
        /// </summary>
        internal Action<DriveKey>? BeforeRestartDecision { get; set; }

        /// <summary>
        ///     Invoked with the lifecycle gate held, after requested=true is read, before revalidation.
        ///     Same waiting restriction as <see cref="BeforeRestartDecision" />.
        /// </summary>
        internal Action<DriveKey>? RestartRequested { get; set; }

        /// <summary>
        ///     Invoked on restart with the lifecycle gate held, after predecessor drain, before registration takes the state lock.
        ///     Must not wait for operations needing that lifecycle gate.
        /// </summary>
        internal Func<DriveKey, Task>? RestartBeforeRegistration { get; set; }

        /// <summary>
        ///     Invoked on pump entry to apply, after validation, before write-gate acquisition; no gate or state lock held.
        ///     Must not wait for a stop that drains the pump.
        /// </summary>
        internal Action<DriveKey>? ApplyJournalEntriesEntered { get; set; }

        /// <summary>
        ///     Invoked with own write gate held, state lock released, no lifecycle gate acquired by apply.
        ///     Cannot wait for own publication or disposal.
        /// </summary>
        internal Action<DriveKey>? ApplyJournalEntriesInsideWriteGate { get; set; }

        /// <summary>
        ///     Wraps the Settle action in <see cref="RecordPumpFault" />; pump holds no gate or state lock at entry.
        ///     Cannot wait for pump drainage or full stop completion.
        /// </summary>
        internal Action<Action>? PumpFaultSettlementWrapper { get; set; }

        /// <summary>
        ///     Invoked after apply released the write gate, before <see cref="DriveRuntime.RaiseChanged" />; no lifecycle/write/state lock.
        ///     Cannot await a rescan that drains this same pump.
        /// </summary>
        internal Action<DriveKey>? BeforeWatchChanged { get; set; }
    }
}

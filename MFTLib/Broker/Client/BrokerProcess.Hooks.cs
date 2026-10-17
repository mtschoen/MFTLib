namespace MFTLib;

internal sealed partial class BrokerProcess
{
    /// <summary>Observations and substitutions owned by this process alone.</summary>
    internal ProcessHooks Hooks { get; }

    internal sealed class ProcessHooks(BrokerProcess process)
    {
        /// <summary>Reads the reservation table while holding the process state lock.</summary>
        internal int PendingRequestCount
        {
            get
            {
                lock (process._gate)
                {
                    return process._pending.Count;
                }
            }
        }

        /// <summary>
        /// Runs with the control write semaphore held, before the caller cancellation check;
        /// no process state lock is held. Must not throw or wait for another control writer.
        /// </summary>
        internal Action? AfterControlWriteLockAcquired { get; set; }
    }
}

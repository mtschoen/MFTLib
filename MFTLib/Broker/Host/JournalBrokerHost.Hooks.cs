using MFTLib.Index;

namespace MFTLib;

internal sealed partial class JournalBrokerHost
{
    /// <summary>Instance-local controls and observations configured before serving.</summary>
    internal HostHooks Hooks { get; } = new();

    internal sealed class HostHooks
    {
        /// <summary>
        /// Runs on the scan worker after admission and before its cursor query.
        /// No pipe state/write lock is held; the scan holds its parse registration.
        /// Observers must be thread-safe, nonblocking and nonthrowing.
        /// </summary>
        internal Action<DriveKey, IReadOnlyCollection<string>?>? ScanStarting { get; set; }

        /// <summary>
        /// Runs on the dedicated sender thread after visiting its pipe snapshot,
        /// outside the sender list lock and pipe locks. Writes may still be in flight.
        /// Must not block, throw, or wait for session disposal, which joins this thread.
        /// </summary>
        internal Action? HeartbeatVisited { get; set; }

        /// <summary>Minimum time between intermediate scan reports; set before serving.</summary>
        internal TimeSpan ProgressThrottleInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    }
}

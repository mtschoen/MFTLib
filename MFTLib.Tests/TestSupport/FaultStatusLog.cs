using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Keeps the root's status as it stood when each lost-catch-up fault was raised, which is
///     what a <see cref="FileIndex.WatchFaulted" /> handler can read. The fault's exception no
///     longer carries the root's count or report, and a status read after the fact reflects a
///     later retry.
/// </summary>
sealed class FaultStatusLog
{
    readonly Lock _gate = new();
    readonly List<RootStatus> _catchUpLossStatuses = [];

    /// <summary>Records the status of the fault's root when <paramref name="fault" /> is a lost catch-up.</summary>
    internal void Record(FileIndex index, WatchFault fault)
    {
        if (fault.Kind != WatchFaultKind.CatchUpLost)
        {
            return;
        }

        var status = index.Status;
        lock (_gate)
        {
            _catchUpLossStatuses.Add(status);
        }
    }

    /// <summary>The status of <paramref name="driveLetter" /> at each of its lost-catch-up faults, in raise order.</summary>
    internal RootStatus[] CatchUpLossStatuses(char driveLetter)
    {
        lock (_gate)
        {
            return [.. _catchUpLossStatuses.Where(status => status.Root == new VolumeKey(char.ToUpperInvariant(driveLetter)))];
        }
    }
}

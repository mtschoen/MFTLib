using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Keeps the drive's status as it stood when each lost-catch-up fault was raised, which is
///     what a <see cref="FileIndex.WatchFaulted" /> handler can read. The fault's exception no
///     longer carries the drive's count or report, and a status read after the fact reflects a
///     later retry.
/// </summary>
internal sealed class FaultStatusLog
{
    readonly Lock _gate = new();
    readonly List<DriveStatus> _catchUpLossStatuses = [];

    /// <summary>Records the status of the fault's drive when <paramref name="fault" /> is a lost catch-up.</summary>
    internal void Record(FileIndex index, WatchFault fault)
    {
        if (fault.Kind != WatchFaultKind.CatchUpLost)
        {
            return;
        }

        var status = index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(fault.DriveLetter));
        lock (_gate)
        {
            _catchUpLossStatuses.Add(status);
        }
    }

    /// <summary>The status of <paramref name="driveLetter" /> at each of its lost-catch-up faults, in raise order.</summary>
    internal DriveStatus[] CatchUpLossStatuses(char driveLetter)
    {
        lock (_gate)
        {
            return [.. _catchUpLossStatuses.Where(status => status.DriveLetter == char.ToUpperInvariant(driveLetter))];
        }
    }
}

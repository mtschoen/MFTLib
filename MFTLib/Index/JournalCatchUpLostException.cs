using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     A scan of one drive lost its journal catch-up: the journal proved that the cursor armed
///     before the scan could no longer be read when the scan finished, so the scan's block is
///     complete but cannot be watched from its cursor. Carried by a
///     <see cref="WatchFaultKind.CatchUpLost" /> fault after every such scan, and thrown by
///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> when the scan operation stops retrying.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "Every instance names its drive, its count and its report, which is what a consumer " +
                    "acts on; the standard overloads would construct one that carries none of them.")]
public sealed class JournalCatchUpLostException : Exception
{
    /// <summary>Records one lost catch-up.</summary>
    /// <param name="driveLetter">The drive whose scan lost its catch-up.</param>
    /// <param name="consecutiveLostCatchUps">The drive's count after this loss.</param>
    /// <param name="recoveryStopped">Whether the scan operation stopped retrying at this loss.</param>
    /// <param name="checkpointLoss">The journal's proof of the loss, recorded as the drive's report.</param>
    /// <param name="message">What was lost and what happens next.</param>
    public JournalCatchUpLostException(char driveLetter, int consecutiveLostCatchUps, bool recoveryStopped,
        JournalCheckpointLoss checkpointLoss, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(checkpointLoss);
        DriveLetter = driveLetter;
        ConsecutiveLostCatchUps = consecutiveLostCatchUps;
        RecoveryStopped = recoveryStopped;
        CheckpointLoss = checkpointLoss;
    }

    public char DriveLetter { get; }

    /// <summary>
    ///     The drive's <see cref="DriveStatus.ConsecutiveLostCatchUps" /> once this loss was
    ///     counted.
    /// </summary>
    public int ConsecutiveLostCatchUps { get; }

    /// <summary>
    ///     True when this loss brought the count to <see cref="FileIndex.LostCatchUpRecoveryLimit" />
    ///     or past it, so no further scan follows automatically: the drive keeps this block,
    ///     queryable but not watchable, until a consumer's <see cref="FileIndex.RescanAsync(char, CancellationToken)" />.
    /// </summary>
    public bool RecoveryStopped { get; }

    /// <summary>
    ///     The report the broker proved against the live journal, with
    ///     <see cref="JournalCheckpointLoss.DetectedDuring" /> reading
    ///     <see cref="JournalCheckpointLossDetection.ScanCatchUp" />. Its
    ///     <see cref="JournalCheckpointLoss.SizeThatWouldHaveRetained" /> is the journal size to grow
    ///     to when the journal was trimmed rather than recreated.
    /// </summary>
    public JournalCheckpointLoss CheckpointLoss { get; }
}

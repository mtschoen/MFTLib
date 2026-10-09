using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     A scan of one drive lost its journal catch-up: the journal proved that the cursor armed
///     before the scan could no longer be read when the scan finished, so the scan's block is
///     complete but cannot be watched from its cursor. Carried by a
///     <see cref="WatchFaultKind.CatchUpLost" /> fault after every such scan, and thrown by
///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> when the scan operation stops retrying.
///     The exception names no drive: <see cref="WatchFault.DriveLetter" /> does, and the drive's
///     <see cref="DriveWatchStatus.RecoveryStopped" /> and <see cref="DriveWatchStatus.CheckpointLoss" />
///     carry the recovery decision and the journal's proof.
/// </summary>
[SuppressMessage("Roslynator", "RCS1194",
    Justification = "Every instance says whether recovery stopped, which is what a consumer acts on; " +
                    "the standard overloads would construct one that carries no such classification.")]
public sealed class JournalCatchUpLostException : Exception
{
    /// <summary>Records one lost catch-up.</summary>
    /// <param name="recoveryStopped">Whether the scan operation stopped retrying at this loss.</param>
    /// <param name="message">What was lost and what happens next.</param>
    internal JournalCatchUpLostException(bool recoveryStopped, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RecoveryStopped = recoveryStopped;
    }

    /// <summary>
    ///     True when this loss exhausted automatic recovery from consecutive scan catch-up losses,
    ///     so no further scan follows automatically: the drive keeps this block,
    ///     queryable but not watchable, until a consumer's <see cref="FileIndex.RescanAsync(char, CancellationToken)" />.
    /// </summary>
    public bool RecoveryStopped { get; }
}

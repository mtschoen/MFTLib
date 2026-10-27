using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     A scan of one volume lost its journal catch-up: the journal proved that the cursor armed
///     before the scan could no longer be read when the scan finished, so the scan's block is
///     complete but cannot be watched from its cursor. Carried by a
///     <see cref="WatchFaultKind.CatchUpLost" /> fault after every such scan, and returned in
///     <see cref="RootOperationResult.Failure" /> by <see cref="FileIndex.RescanAsync" /> when the scan operation stops retrying.
///     The exception names no volume: <see cref="WatchFault.Volume" /> does, and the volume's
///     <see cref="RootWatchStatus.IsRecoveryStopped" /> and <see cref="RootWatchStatus.CheckpointLoss" />
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
        IsRecoveryStopped = recoveryStopped;
    }

    /// <summary>
    ///     True when this loss exhausted automatic recovery from consecutive scan catch-up losses,
    ///     so no further scan follows automatically: the volume keeps this block,
    ///     queryable but not watchable, until a consumer's <see cref="FileIndex.RescanAsync" />.
    /// </summary>
    public bool IsRecoveryStopped { get; }
}

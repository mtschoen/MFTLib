namespace MFTLib.Index;

/// <summary>Why a cached block's journal checkpoint could not be resumed.</summary>
public enum JournalCheckpointLossCause
{
    /// <summary>
    ///     The journal is the same one the checkpoint came from, and it has trimmed past it.
    ///     A larger journal would have kept the checkpoint, so
    ///     <see cref="JournalCheckpointLoss.SizeThatWouldHaveRetained" /> says how large.
    /// </summary>
    CheckpointTrimmed,

    /// <summary>
    ///     The journal was deleted and recreated, so it carries a different journal id and a
    ///     fresh USN space. The checkpoint names a record in a journal that no longer exists;
    ///     no journal size would have preserved it, and none is suggested.
    /// </summary>
    JournalRecreated
}

/// <summary>
///     What MFTLib found when a drive's cached block could not be resumed and the drive was
///     rescanned instead: the checkpoint that was lost, the journal as it stood when the loss
///     was detected, and, when a larger journal would have prevented it, how large.
///     <para>
///         Every number here is read off the volume, not inferred. USNs are byte offsets into
///         the journal, so the distance between two of them is a byte count.
///     </para>
/// </summary>
public sealed record JournalCheckpointLoss
{
    /// <summary>Upper case, matching the letter this drive was configured with.</summary>
    public required char DriveLetter { get; init; }

    /// <summary>
    ///     Which of the two situations this was, and therefore whether a journal size is
    ///     offered at all. MFTLib reports only causes it can detect from the journal itself.
    /// </summary>
    public required JournalCheckpointLossCause Cause { get; init; }

    /// <summary>
    ///     Where the cached block left off, and so the point the journal would have had to
    ///     still reach back to for this drive to be caught up instead of rescanned.
    /// </summary>
    public required long CheckpointUsn { get; init; }

    /// <summary>The oldest USN the journal still retained when the loss was detected.</summary>
    public required long FirstUsn { get; init; }

    /// <summary>The USN the journal's next record would be written at.</summary>
    public required long NextUsn { get; init; }

    /// <summary>The journal's allocation unit, which sizes are rounded up to.</summary>
    public required long AllocationDelta { get; init; }

    /// <summary>The journal's configured maximum size when the loss was detected.</summary>
    public required long MaximumSize { get; init; }

    /// <summary>
    ///     How far behind the journal the checkpoint had fallen, in bytes:
    ///     <see cref="FirstUsn" /> minus <see cref="CheckpointUsn" />. Null for
    ///     <see cref="JournalCheckpointLossCause.JournalRecreated" />, where the checkpoint and
    ///     the journal belong to different USN spaces and the difference would mean nothing.
    /// </summary>
    public long? BytesBehind { get; init; }

    /// <summary>
    ///     The journal maximum size in bytes that would have kept the checkpoint readable:
    ///     <see cref="NextUsn" /> minus <see cref="CheckpointUsn" />, rounded up to
    ///     <see cref="AllocationDelta" />. This is the number to offer the user alongside
    ///     <c>JournalBrokerClient.GrowUsnJournalAsync</c>. Null for
    ///     <see cref="JournalCheckpointLossCause.JournalRecreated" />, where no size would have
    ///     helped.
    /// </summary>
    public long? SizeThatWouldHaveRetained { get; init; }
}

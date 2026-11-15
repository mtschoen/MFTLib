namespace MFTLib.Index;

/// <summary>
///     The first journal record a batch could not store: its index in the batch, the region that refused it, and whether
///     a larger block could hold it. Nothing of the record was written.
/// </summary>
readonly record struct JournalRefusal
{
    /// <summary>Position of the refused record in the batch's entries.</summary>
    public required int Index { get; init; }

    /// <summary>The full region; never <see cref="WriteRefusal.None" />.</summary>
    public required WriteRefusal Reason { get; init; }

    /// <summary>False when no capacity could ever make the record fit (a record or parent number above 32 bits).</summary>
    public required bool Growable { get; init; }
}

/// <summary>
///     What <see cref="JournalMutator.ApplyUntilRefusal" /> did: the changes of the records it applied, and the refusal
///     it stopped at, or null when it reached the end of the batch. After a refusal the block holds exactly the records
///     before the refused index and its cursor names the refused record.
/// </summary>
/// <param name="Changes">Changes minted by the applied records, in order.</param>
/// <param name="Refusal">The record the apply stopped at, or null when the whole range applied.</param>
readonly record struct JournalApplyResult(IReadOnlyList<FileChange> Changes, JournalRefusal? Refusal);

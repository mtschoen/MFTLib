using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     One drive's finished scan. <see cref="Block" /> belongs to the caller, who disposes it.
///     <see cref="ArmedCursor" /> is the journal position armed before the scan, which a watch
///     resumes from. <see cref="AdvancedCursor" /> is null and <see cref="CatchUpEntries" /> empty
///     when <see cref="CatchUpLoss" /> is set; <see cref="CatchUpLoss" /> is the loss the host
///     proved against the live journal when catch-up after the scan failed, and the block is still
///     complete.
/// </summary>
public sealed record BrokerDriveScanResult(
    char DriveLetter,
    UsnJournalCursor ArmedCursor,
    UsnJournalCursor? AdvancedCursor,
    IReadOnlyList<UsnJournalEntry> CatchUpEntries,
    JournalCheckpointLoss? CatchUpLoss,
    BlockScanOutcome Block);

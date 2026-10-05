using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     One drive's finished scan. <see cref="Block" /> belongs to the caller, who disposes it.
///     <see cref="ArmedCursor" /> is the journal position armed before the scan, which a watch
///     resumes from. <see cref="AdvancedCursor" /> is where the host's catch-up read finished;
///     the entries it read stay with the host, and a watch from <see cref="ArmedCursor" /> replays
///     them, provided the journal still retains them when the watch starts. <see cref="AdvancedCursor" /> is null when <see cref="CatchUpLoss" /> is set;
///     <see cref="CatchUpLoss" /> is the loss the host proved against the live journal when
///     catch-up after the scan failed, and the block is still complete.
/// </summary>
internal sealed record BrokerDriveScanResult(
    char DriveLetter,
    UsnJournalCursor ArmedCursor,
    UsnJournalCursor? AdvancedCursor,
    JournalCheckpointLoss? CatchUpLoss,
    BlockScanOutcome Block);

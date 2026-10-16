using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     One drive's finished scan. <see cref="Block" /> belongs to the caller, who disposes it.
///     <see cref="ArmedCursor" /> is the journal position armed before the scan. A watch
///     resumes there to replay the scan window while the journal retains those entries.
///     <see cref="CatchUpLoss" /> is the loss the host proved against the live journal when
///     catch-up after the scan failed; the returned block is still complete.
/// </summary>
internal sealed record BrokerDriveScanResult(
    char DriveLetter,
    UsnJournalCursor ArmedCursor,
    JournalCheckpointLoss? CatchUpLoss,
    BlockScanOutcome Block);

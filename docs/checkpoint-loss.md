# Checkpoint loss

- Index contracts:
    - **Checkpoint loss**: opening a drive reads the live journal through the unelevated
      volume-root handle (`UsnJournalVolumeInterop`) before adopting a cached block. A block
      whose `BlockHeader.UsnNextUsn` is below the journal's `FirstUsn`, or whose
      `BlockHeader.UsnJournalId` no longer matches, cannot be resumed, so the drive cold-scans
      and `DriveStatus.CheckpointLoss` records the checkpoint, the journal window, and the size
      a journal would need to be at least to have kept it (`JournalSizeArithmetic`: the
      checkpoint-to-tip span rounded up to the allocation delta, plus one more allocation delta).
      The margin follows NTFS's documented trimming behavior in CREATE_USN_JOURNAL_DATA and
      USN_JOURNAL_DATA, not a live measurement. A volume that cannot answer the query warm-starts
      and reports nothing. A faulting MFT-backed drive asks the live journal about the cursor in
      its current block; the read runs outside `_stateLock`, and the result is recorded only while
      that block is still published. `JournalBrokerHost.DescribeWatchFailure` applies the same
      journal classification to a failed nonzero watch start. Neither path classifies exception
      wording, and a retained cursor or unavailable query records nothing new.
      `JournalCheckpointLoss.DetectedDuring` distinguishes `DriveOpening`, `LiveWatch`, and
      `ScanCatchUp`. A report remains until a newer report replaces it or a consumer rescan clears
      it; an unrelated watch fault does not clear it. A `LiveWatch` report is retained by its
      automatic recovery rescan, because it explains why the published replacement block exists.
      A cache-only open adopts an unresumable block as a `Ready` snapshot with its report attached,
      but `StartWatchingAsync` refuses that drive and leaves no watch request. After `RescanAsync`
      publishes a resumable block, the consumer calls `StartWatchingAsync` again.
      When catch-up after a scan fails, the host checks the armed cursor against the live journal.
      A proven loss travels through `BrokerDriveScanResult.CatchUpLoss` and
      `MftBlockProduceResult.CatchUpLoss`; the index publishes the complete block as unresumable,
      records a `ScanCatchUp` report (including `SizeThatWouldHaveRetained` when the journal was
      trimmed), and scans the drive again. `DriveStatus.ConsecutiveLostCatchUps` reaches
      `FileIndex.LostCatchUpRecoveryLimit` after three consecutive losses; a scan whose catch-up
      holds resets it to zero. At the limit an open settles the drive `Ready` with its last block,
      refuses its watch, and requires a consumer `RescanAsync` before another watch start.
      A catch-up failure the journal cannot prove is an ordinary failed scan and carries no report.
      A bounded catch-up read that returns entries without advancing its cursor fails the scan.

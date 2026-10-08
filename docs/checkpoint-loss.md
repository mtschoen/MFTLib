# Checkpoint loss

- Index contracts:
    - **Checkpoint loss**: opening a drive reads the live journal through the unelevated
      volume-root handle (`UsnJournalVolumeInterop`) before adopting a cached block. A block
      whose `BlockHeader.UsnNextUsn` is below the journal's `FirstUsn`, or whose
      `BlockHeader.UsnJournalId` no longer matches, cannot be resumed, so the drive cold-scans
      and `DriveWatchStatus.CheckpointLoss` records the cause, the journal's allocation delta and
      maximum size, how far behind the journal the checkpoint fell, and the size
      a journal would need to be at least to have kept it (`JournalSizeArithmetic`: the
      checkpoint-to-tip span rounded up to the allocation delta, plus one more allocation delta).
      The margin follows NTFS's documented trimming behavior in CREATE_USN_JOURNAL_DATA and
      USN_JOURNAL_DATA, not a live measurement. A volume that cannot answer the query warm-starts
      and reports nothing.
      A direct scan-only source also rejects a retained cursor when the live next USN differs,
      reporting `JournalAdvanced` without `BytesBehind` or `SizeThatWouldHaveRetained`. It cannot
      catch up. Recreation and trimming take precedence over movement; an unavailable or incoherent
      observation still adopts without a report. Cache-only opens keep the snapshot unresumable.
      A faulting MFT-backed drive asks the live journal about the cursor in its current block;
      the read runs outside `_stateLock`, and the result is recorded only while
      that block is still published. `JournalBrokerHost.DescribeWatchFailure` applies the same
      journal classification to a failed nonzero watch start. Neither path classifies exception
      wording, and a retained cursor or unavailable query records nothing new.
      `JournalCheckpointLoss.DetectedDuring` distinguishes `DriveOpening`, `LiveWatch`, and
      `ScanCatchUp`. A report remains until a newer report replaces it or a consumer rescan clears
      it; an unrelated watch fault does not clear it. A `LiveWatch` report is retained by its
      automatic recovery rescan, because it explains why the published replacement block exists.
      A cache-only open adopts an unresumable block as a `Ready` snapshot with its report attached,
      but `StartWatchingAsync` refuses that drive and retains the watch request. After `RescanAsync`
      publishes a resumable block, it clears the refusal and starts the requested watch.
      When catch-up after a scan fails, the host checks the armed cursor against the live journal.
      A proven loss travels through `BrokerDriveScanResult.CatchUpLoss` and
      `MftBlockProduceResult.CatchUpLoss`; the index publishes the complete block as unresumable,
      records a `ScanCatchUp` report (including `SizeThatWouldHaveRetained` when the journal was
      trimmed), and scans the drive again. `DriveWatchStatus.ConsecutiveLostCatchUps` reaches
      `FileIndex.LostCatchUpRecoveryLimit` after three consecutive losses; a scan whose catch-up
      holds resets it to zero. At the limit an open settles the drive `Ready` with its last block,
      refuses its watch, and requires a consumer `RescanAsync` to publish a resumable block
      and start the watch if it is requested.
      A catch-up failure the journal cannot prove is an ordinary failed scan and carries no report.
      `JournalCheckpointLoss` keeps the checkpoint, first and next USN positions internal: the broker
      protocol carries them, report equality covers them, and `BytesBehind` and
      `SizeThatWouldHaveRetained` derive from them. `JournalCatchUpLostException` carries only
      `RecoveryStopped` and its message; the drive is the fault's `WatchFault.DriveLetter`, and the
      consecutive count and the report are the drive's `DriveStatus` values, which a
      `WatchFaulted` handler reads when the fault is raised.
      A bounded catch-up read that returns entries without advancing its cursor fails the scan.

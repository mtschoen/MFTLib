# Checkpoint loss

- Index contracts:
    - **Checkpoint loss**: opening a drive reads the live journal through the unelevated
      volume-root handle (`UsnJournalVolumeInterop`) before adopting a cached block. A block
      whose `BlockHeader.UsnNextUsn` is below the journal's `FirstUsn`, or whose
      `BlockHeader.UsnJournalIdentifier` no longer matches, cannot be resumed, so the drive cold-scans
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
      trimmed), and scans the drive again. After three consecutive losses the internal retry
      policy stops automatic recovery and sets `DriveWatchStatus.IsRecoveryStopped` before the
      loss notification or open completion. A scan whose catch-up holds clears it; failed or
      cancelled production and stopping the watch preserve it. An exhausted open settles the drive `Ready` with its last block,
      refuses its watch, and requires a consumer `RescanAsync` to publish a resumable block
      and start the watch if it is requested.
      A manual or recovery rescan retries until catch-up succeeds or this limit is reached.
      At exhaustion, public `RescanAsync` returns `DriveOperationOutcome.Failed` for that drive
      with the last `JournalCatchUpLostException` in `DriveOperationResult.Failure`. Its
      complete block stays queryable, but a watch start fails and retains the watch request
      until a successful rescan. Each proven attempt raises `WatchFaultKind.CatchUpLost`.
      A catch-up failure the journal cannot prove is an ordinary failed scan and carries no report.
      `JournalCheckpointLoss` keeps the checkpoint, first and next USN positions internal: the broker
      protocol carries them, report equality covers them, and `BytesBehind` and
      `SizeThatWouldHaveRetained` derive from them. `JournalCatchUpLostException` carries only
       `IsRecoveryStopped` and its message; the drive is the fault's `WatchFault.Drive`, and the
      recovery-stopped flag and the report are the drive's `DriveStatus.Watch` values, which a
      `WatchFaulted` handler reads when the fault is raised.
      A stopped-loss message with a retained-size suggestion names the public
      `BrokerSession.GrowUsnJournalAsync` remedy, followed by `FileIndex.RescanAsync`.
      A bounded catch-up read that returns entries without advancing its cursor fails the scan.

## Reading a report

`DriveWatchStatus.CheckpointLoss` is null when no proven loss is recorded: the watch never
lost its position, the volume could not answer the journal query, or a loss was not proven.
It is not null merely because the drive warm-started: `CacheMode.ExistingOnly` adopts an
unresumable cached block with a `DriveOpening` report, and an ordinary open can retain one.
When it is non-null, read two fields together:

- `Cause` says whether a journal size would have helped. `CheckpointTrimmed` means the journal
  trimmed past the checkpoint, and `SizeThatWouldHaveRetained` gives the size that would have
  kept it, or null when that size does not fit in a `long`. `JournalRecreated` means the
  journal was deleted and recreated, so no size would have helped. `JournalAdvanced` means a
  scan-only cached source found the journal moved and cannot catch up; it carries no sizes.
  Decide from `Cause`, not from whether a size is null.
- `DetectedDuring` says which check found the report: `DriveOpening` (opening the cached
  block, or why a cache-only drive cannot be watched), `LiveWatch` (a live watch faulted with
  its position gone) or `ScanCatchUp` (the journal outran a scan). A report can predate the
  fault a handler is processing, so treat the label as the report's origin, not as the cause
  of that fault.

`BytesBehind` is the journal's oldest retained USN minus the checkpoint. Both sizes are exact
integer arithmetic on positions read from the volume, not live measurements. Windows creates a
journal at 32 MB, which a busy system drive can wrap in minutes. A rescan is an acceptable
outcome, so show the user the size and let them decide whether a larger journal is worth it.
MFTLib never changes a journal on its own. Growth is persistent and shared with every other
journal consumer on the volume, such as Windows Search and backup agents, so offer it as an
explicit user action.

## Journal growth target

`JournalCheckpointLoss.TryGetGrowthTarget(out UsnJournalSettings target)` projects
the report into sizing for an explicit journal growth request. It succeeds only
for `CheckpointTrimmed`, with a positive representable `SizeThatWouldHaveRetained`
greater than the report's `JournalSettings.MaximumSize`, and a positive recorded
`AllocationDelta`. On success the target contains exactly that retention size and
the recorded allocation delta. On failure the output is default, including when
the retained size is null because it cannot fit in a `long`.

`GrowthTargetRefusalReason` is a nullable `JournalGrowthTargetRefusalReason`: null
exactly when the projection can supply a target, otherwise the first applicable
reason in this order:

1. `CheckpointNotTrimmed`: the cause is not `CheckpointTrimmed`.
2. `RetentionSizeUnavailable`: no retained size was recorded, including overflow.
3. `NonPositiveRetentionSize`: the recorded retention size is zero or negative.
4. `NonPositiveAllocationDelta`: the recorded allocation delta is zero or negative.
5. `RetentionSizeNotGreaterThanMaximum`: the retained size is at or below the recorded maximum.

The reason is derived from current report values, including synthetic `with` copies.
The live checkpoint check rejects nonpositive allocation deltas before creating a loss,
and its representable retained sizes are positive. Those two nonpositive guards
are reachable through `SyntheticNotifications.CreateJournalCheckpointLoss` and
`with` expressions; the other reasons can arise from production observations.

The method performs no I/O, growth or new arithmetic. It does not round again or
invent a larger target. The library reports the journal size that would have retained
the checkpoint; the consumer owns the decision to grow. Pass the target struct to
`BrokerSession.GrowUsnJournalAsync(drive, target, cancellationToken)`.
Query current settings immediately before requesting
growth and refuse the request if the target maximum is no longer greater than
the current maximum. Growing cannot recover lost records; rescan afterward.

# Sizing broker scans

Reference material for [the broker integration guide](broker-integration.md):
planning a cold-scan block, understanding the broker's parse-thread budget, and
recovering when the journal outruns scan catch-up.

## Sizing the block

Before a cold scan, the producer queries the elevated broker
for the drive's MFT geometry and creates a file-backed named section at the
`BlockScanTarget` path. `BrokerMftBlockProducer` creates that target from the
path, volume serial, delete-on-close choice, and cache tag in the index's
`MftBlockProduceRequest`.

`MftBlockCapacity.Plan` estimates rows from `MftRecordCount`, with a minimum of
65,536 when information is absent or smaller. Slot capacity adds 25 percent or
65,536 rows, whichever is larger. The default name estimate is 48 bytes per
slot, then name-pool headroom adds 25 percent or one mebibyte, whichever is
larger. Capacity exhaustion marks the block for compaction and reports skipped
records. Treat that as a reason to rescan.

The broker protocol returns `MftValidDataLength`, `BytesPerFileRecordSegment`,
and the derived `MftRecordCount` for that query. Code that is already elevated can call
`NtfsVolumeInformation.Query` directly for the same values.

## Concurrent scans and parse threads

Every scan has its own drive channel. The host owns one process-wide parse-thread
budget equal to its processor count. At most that many scans run at once, so
every admitted scan has at least one parse thread; additional scan channels wait
in admission order and continue receiving heartbeats.

When `n` scans run, the host divides the budget among them. Each scan gets the
processor count divided by `n`, with the remainder assigned in admission order.
Whenever a scan starts, finishes, or is cancelled, the host rebalances every
running scan's `ParseThreadAllowance`.

The native parser reads its current allowance at the start of every MFT chunk
and before path resolution. A chunk already in progress finishes with the count
it started with, so admission can make the total briefly exceed the processor
count for at most one chunk. Callers do not choose a thread count:
`BrokerScanOptions`, `MftBlockProduceRequest`, and the broker wire request carry
none. The same allocator governs open scans, single-drive and batched rescans,
and automatic recovery scans.

## Lost scan catch-up

The host captures a journal cursor before scanning, completes the block, and
then reads catch-up from that cursor in bounded chunks. Each successful bounded
read that advances the cursor restarts the channel's progress clock. A read that
returns entries without advancing its cursor fails catch-up instead of accepting
duplicate entries.

When catch-up fails, the host asks the live journal whether the armed cursor is
gone. A proven trimmed or recreated journal produces a catch-up-loss result. A
cursor that is still retained, or a journal query that cannot answer, leaves the
failure as an ordinary scan error.

`FileIndex` publishes the complete block from a proven loss, marks it
unresumable, raises `WatchFaultKind.CatchUpLost` when handlers exist, and rescans
the drive. A successful catch-up resets
`DriveWatchStatus.ConsecutiveLostCatchUps`. After
`FileIndex.LostCatchUpRecoveryLimit` consecutive losses, automatic retries stop,
and the block stays queryable but cannot be watched. A manual or recovery rescan
at the limit leaves the drive `WatchCatchUpState.Faulted`. During
`FileIndex.OpenAsync`, the same retry loop settles the drive `DriveState.Ready`
with the last block at the limit rather than failing the whole open, while its
watch catch-up state is already `WatchCatchUpState.Faulted`. A later
`StartWatchingAsync` is refused until `RescanAsync` succeeds. The refusal retains
the watch request, so a successful rescan clears the refusal and starts the watch.

The `JournalCheckpointLoss` report in the drive's `DriveWatchStatus.CheckpointLoss` tells the
consumer what happened. For `JournalCheckpointLossCause.CheckpointTrimmed`, a non-null
`SizeThatWouldHaveRetained` is the minimum size that would have kept the cursor.
After user consent, grow the journal through
`BrokerSession.GrowUsnJournalAsync`, choosing a maximum greater than the current
`UsnJournalSettings.MaximumSize` and at least the suggested size. Then rescan the
drive. Journal growth cannot recover records already discarded, so the rescan is
required. If watching had been refused over the unresumable block, the successful
rescan clears the refusal and starts the requested watch.

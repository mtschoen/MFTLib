# Sizing blocks and customizing watch cursors

Reference material for [the broker integration guide](broker-integration.md): planning a
cold-scan block's capacity, and replacing the drive set a session watches while parked.

## Sizing the block

Before a cold scan, `JournalBrokerClient` queries the elevated broker for each volume's
MFT geometry, then creates a file-backed named section using the corresponding
`BrokerScanOptions.BlockTargets` destination. Targets are required for every drive.
`MftBlockCapacity.Plan` estimates rows from `MftRecordCount`, with a minimum of 65,536
when information is absent or smaller. Slot capacity adds 25 percent or 65,536 rows,
whichever is larger. The default name estimate is 48 bytes per slot, then name-pool
headroom adds 25 percent or one mebibyte, whichever is larger. A failed volume query
currently uses the minimum estimate; capacity exhaustion marks the block for compaction
and reports skipped records. Treat that as a reason to rescan.

A volume's NTFS geometry and MFT sizing can also be queried directly, without arming a
scan, via `JournalBrokerClient.QueryVolumesAsync` or (already elevated, no broker involved)
`NtfsVolumeInformation.Query`. Note that `QueryVolumesAsync` populates only MFT sizing
(`MftValidDataLength`, `BytesPerFileRecordSegment`, and derived `MftRecordCount`) for block
capacity planning; cluster and sector geometry fields (`BytesPerSector`, `BytesPerCluster`,
`TotalClusters`, `FreeClusters`) are not sent over the broker protocol and are set to zero.

## Customizing watch cursors: ReplaceWatchCursors and WatchCursors

By default, `StartWatchAsync` watches every drive armed during the latest scan (or
supplied at warm start) using its advanced cursor. `ReplaceWatchCursors` lets an
application replace the complete watch set while parked - for example, when a user
selects or deselects individual drives:

```csharp
// Read back what the session is currently configured to watch:
IReadOnlyDictionary<string, UsnJournalCursor> current = session.WatchCursors;

// Replace with a custom or narrowed set:
session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor>
{
    ["C"] = cachedCursorC,
    ["D"] = advancedCursorD
});

await session.StartWatchAsync(cancellationToken);
```

Keys passed to `ReplaceWatchCursors` are normalized (bare letter, case-insensitive) and
the call replaces rather than merges the previous set. An empty dictionary is accepted,
in which case `StartWatchAsync` will throw `InvalidOperationException` ("No drives to
watch"). Keys that do not represent a valid drive letter (e.g. malformed paths, null
keys, delimiters, non-letter strings) throw `ArgumentException`.

Note the interaction with rescans: `RescanAsync` overwrites the watch set with its own
scan result's `AdvancedCursors`. A consumer that maintains a narrowed or custom drive
selection should call `ReplaceWatchCursors` again after each rescan to preserve the
selection.

# Testing your integration

Reference **`MFTLib.TestExtensions`** to test drives, `BrokerScanProfile`, and keep-file
names without elevation or friend-listing your assembly. Build a fake
`JournalBrokerClient` constructed on an in-memory duplex stream. The client constructor
requires a block section factory immediately after the stream. A Windows test can supply
it with `NamedBlockSection`; here `clientSide` is your connected duplex stream endpoint:

```csharp
using MFTLibTestExtensions;
using MFTLib.Index;

var client = new JournalBrokerClient(clientSide, (drive, options) =>
{
    var sectionName = NamedBlockSection.BuildSectionName(drive[0]);
    var (block, lifetime) = NamedBlockSection.Create(options, sectionName);
    return (sectionName, block, lifetime);
});

await using var session = await ScanSessionTestHarness.StartScannedAsync(
    _ => Task.FromResult(client), drives, new BrokerScanOptions
    {
        BlockTargets = blockTargets,
        Profile = BrokerScanProfile.DirectoryIndex,
        KeepFileNames = keepFileNames
    });
```

`ScanSessionTestHarness.StartScannedAsync` and `StartFromCursorsAsync` mirror the
shipping `JournalBrokerScanSession.StartAsync` / `StartFromCursorsAsync` with an injected
client factory. The harness warm start takes an initial profile but no keep-file names;
rescans supply those in explicit options. The factory must yield a **fresh** client per
call - the session takes exclusive ownership and disposes it. From there, assert on the
frames your test reads off `serverSide` (arm-and-scan drives spec, keep-file names, watch
cursors) and on the session's `LatestScan` and `WatchCursors`. Scanned tests must answer
the volume query and fill the client-created blocks before sending scan-completion
frames.

For a portable in-process example, see
`MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs` and
`InProcessBlockBrokerHarness.cs` in this repository. `RecordingBlockSectionWriter`
implements `IBlockSectionWriter`, resolves section names to test blocks, and stamps an
injected completion timestamp after writing batches. These are repository test helpers,
not types shipped in `MFTLib.TestExtensions`; implement the same seam in your test
project. Warm tests need no block writer until a rescan. Your test assembly needs no
`InternalsVisibleTo` from MFTLib. Disposing the session disposes the blocks still held in
`LatestScan.BlockOutcomes`.

## Testing the FileIndex watch bridge

A consumer testing `FileIndex` itself, rather than a hand-rolled `JournalBrokerScanSession`
consumer, fakes `FileIndexOptions.WatchSource` directly: implement `IIndexWatchSource`
(`StartWatching`, `ArmDriveAsync`, `DisarmDriveAsync`) over an in-memory queue of
`WatchStreamItem` values (`JournalBatch` or `DriveWatchFailure`) instead of standing up a
broker connection at all. This is how this repository's own `FileIndex` watch tests avoid
elevation; there is no `MFTLib.TestExtensions` type for it because `IIndexWatchSource` is
already a small, public seam.

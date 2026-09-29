# Mutation check report

Scratch worktree: C:\Users\mtsch\MFTLib-worktrees\265-mutation, detached at 29b8a6a7d801dc954d3ca6bd1709730bdd8dbc58 (HEAD confirmed before starting).
Setup: `init.ps1 -Build` (restore + solution build Release|x64). Unmutated baseline: `Rescan_OfT_LeavesUsPumpRunning` passed (1/1); all of `BrokerProtocolTests*` plus that test passed (71/71).
Build per mutation: `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64`.
Test command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"`.
No edit was refused. Every mutation compiled. Each was reverted with `git checkout -- <file>` and `git status --short` was empty before the next.

## Set 1: `Rescan_OfT_LeavesUsPumpRunning` (filter `FullyQualifiedName~Rescan_OfT_LeavesUsPumpRunning`)

### 1a. rescan holds `_swapGate` across block production - KILLED
File `MFTLib\Index\FileIndex.Rescan.cs`, `SwapDriveBlockAsync`.
Before:
```
        var scanResult = await ProduceRescannedBlockAsync(drive, driveOrdinal, target, retiredPath,
            superseded, cancellationToken).ConfigureAwait(false);
        if (scanResult is not { } completedScan)
```
After:
```
        ScanDriveResult? scanResult;
        await _swapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            scanResult = await ProduceRescannedBlockAsync(drive, driveOrdinal, target, retiredPath,
                superseded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _swapGate.Release();
        }

        if (scanResult is not { } completedScan)
```
(The gate is released before `CommitBlockUnderSwapGateAsync`, which takes it itself, so this does not self-deadlock.)
Result: Failed [10 s]. `System.TimeoutException: The operation has timed out.` at `FileIndexWatchRescanTests.cs:line 117` (the `otherHandle.Publish(...)` for U, blocked behind the gate).

### 1b. rescan retires and stops every other drive's current watch - KILLED
File `MFTLib\Index\FileIndex.RescanRestart.cs`, `RetireWatchForRescanAsync`, inside the `lock (_stateLock)` before the rescanned drive's own retire. Added:
```
            foreach (var other in _driveRuntimes.Values)
            {
                if (!ReferenceEquals(other, runtime) &&
                    other.Current is { State: WatchInstanceState.Starting or WatchInstanceState.Running })
                {
                    RetireCurrentLocked(other)?.RequestStop();
                }
            }
```
Result: Failed [10 s]. `System.TimeoutException: The operation has timed out.` at `FileIndexWatchRescanTests.cs:line 117`.

## Set 2: `BrokerProtocolTests*` (filter `FullyQualifiedName~BrokerProtocolTests`, 70 tests)

Tests found: unknown kind = `ReadFrame_UnknownKind_ThrowsInvalidDataException` (data rows 0, 18, 99; `BrokerProtocolTests.cs:52`, takes a Heartbeat frame and overwrites the kind byte, asserts type only). Unknown profile = `ReadFrame_ArmAndScan_UnknownProfile_ThrowsInvalidDataException` (`BrokerProtocolTests.Scan.cs:49`, asserts type and `StringAssert.Contains(message, "99")`). Unknown cause = `ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException` (`BrokerProtocolTests.Scan.cs:207`, asserts type only). Golden = `WireBytes_Golden_CatchUpLostFrame` (`BrokerProtocolTests.Scan.cs:369`).

### 2a. reader accepts an unknown frame kind - KILLED
`MFTLib\Broker\Protocol\BrokerProtocol.cs` `ReadFrame` default arm.
Before: `_ => throw new InvalidDataException($"Unknown frame kind: {kind}")`
After: `_ => BrokerFrame.Heartbeat()`
Result: 3 failed (rows 0, 18, 99): `Assert.ThrowsException failed. No exception thrown. InvalidDataException exception was expected.`

### 2b. reader accepts an unknown profile - KILLED
`ReadArmAndScanFrame`. Before: `if (!Enum.IsDefined(profile))`. After: `if (false)`.
Result: 1 failed, `ReadFrame_ArmAndScan_UnknownProfile_...`: `No exception thrown. InvalidDataException exception was expected.`

### 2c. reader accepts an unknown cause - KILLED
`ReadCatchUpLostFrame`. Before: `if (!Enum.IsDefined(cause))`. After: `if (false)`.
Result: 1 failed, `ReadFrame_CatchUpLost_UnknownCause_...`: `No exception thrown. InvalidDataException exception was expected.`

### 2d. `CatchUpLost` writer swaps firstUsn and nextUsn (both i64) - KILLED
`MFTLib\Broker\Protocol\BrokerProtocol.Write.cs` `WriteCatchUpLost`.
Before: `.Int64(loss.FirstUsn)` then `.Int64(loss.NextUsn)`. After: `.Int64(loss.NextUsn)` then `.Int64(loss.FirstUsn)`.
Result: 3 failed. `WireBytes_Golden_CatchUpLostFrame`: `CollectionAssert.AreEqual failed. (Element at index 17 do not match.)`. Also `CatchUpLostFrame_RoundTrips_EveryLossFieldAndMessage` (`Expected:<5000>. Actual:<9000>.`) and `CatchUpLostFrame_RoundTrips_NullBytesBehindAndSizeThatWouldHaveRetained` (`Expected:<9000>. Actual:<5000>.`).

### 2e-i. reader throws `ArgumentException` instead of `InvalidDataException` at all three rejection sites - KILLED (all three)
Same three throw lines, `new InvalidDataException(` replaced with `new ArgumentException(` (message text unchanged).
Result: 5 failed (kind rows 0, 18, 99; profile; cause), each `Assert.ThrowsException failed. Threw exception ArgumentException, but exception InvalidDataException was expected.` So the tests do check the exception type.

### 2e-ii. right type, wrong reason at the three rejection sites - kind SURVIVED, cause SURVIVED, profile KILLED
Same three throw lines, message replaced with `"wrong reason"` (still `InvalidDataException`).
Result: 1 failed: the profile test, `StringAssert.Contains failed. String 'wrong reason' does not contain string '99'.` The unknown-kind test (3 rows) and unknown-cause test PASSED. They check only the exception type, not the offending value, so they cannot tell a correct rejection from any other `InvalidDataException`. The profile test is the only one that pins the value.

### 2e-iii. right type thrown for a different reason on a VALID frame of another kind - rejection tests unaffected (all three PASS)
`ReadFrame`. Before: `BrokerFrameKind.Heartbeat => BrokerFrame.Heartbeat(),`. After: `BrokerFrameKind.Heartbeat => throw new InvalidDataException("Unknown frame kind: Heartbeat"),`
Result: 1 failed, `HeartbeatFrame_RoundTrips_NoPayload`. The three rejection tests passed: none exercises the mutated arm (the kind test starts from Heartbeat bytes but overwrites the kind byte, so it reaches the default arm). No rejection test wrongly failed, and none is sensitive to a different-reason throw on a valid frame; valid frames are protected by their own round-trip tests.

## Summary of test strength
- Rescan test pins both claims (gate not held, other drive not retired): both mutations killed via the bounded timeout at line 117.
- Kind and cause rejection tests pin type only. Profile test pins type and value.

## Final `git status --short` of the scratch worktree
(empty)

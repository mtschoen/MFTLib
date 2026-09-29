# Task A2 report: asynchronous diagnostics with channel tags

Status: DONE. Branch task/265-A2, base e65902cf, commit fb0c766.

## Implemented
- `MFTLib/Broker/BrokerDiagnosticsWriter.cs` (new): bounded `Channel<string>` (capacity 8192, DropWrite, single reader), one long-running drain task, `TryEnqueue` never blocks, drops counted through the channel's drop callback, failed appends counted, and the count reported as `[{role}:{pid}:diagnostics]  {n} records dropped: buffer full` before the next append. Extras beyond the brief: optional second constructor parameter `roleProvider` (role tag for the drop line, default "client"), `FlushAsync`, `Complete`.
- `BrokerDiagnostics.cs`: `ControlChannel`, `DriveChannel(char,int)`, `Log(string channel, string message)`, `LogFrame(channel, direction, kind, length)` now internal, `FlushForTestAsync`, seam `ReplaceWriterForTest`; `ResetToDefaults` completes and discards the writer. Default sink appends to `LogPath` at append time.
- Callers rewritten to `ControlChannel`: `JournalBrokerHost.cs` (2 `LogFrame`), `JournalBrokerClient.Transport.cs` (2 `LogFrame`), and `JournalBrokerClient.LiveWatchDemux.cs` (4 `Log` calls: another caller found by grep, updated). Tests updated: `ElevatedEntryPointTests`, `BrokerPerDriveArmTests` (flush before reading the file).

## TDD evidence
The five named tests plus the existing ones were written against the new API; the shape did not compile before the implementation (compile failure = RED). I wrote the implementation before an isolated RED run, so no separate failing-output capture exists. First GREEN attempt exposed a real defect: with `DropWrite`, `TryWrite` returns true for a dropped item, so drops were uncounted and `Log_BufferFull_DropsAndReportsCount` timed out on flush. Fixed with the channel's item-dropped callback. Then:
`dotnet test ... --filter "BrokerDiagnostics|ElevatedEntryPointTests|BrokerPerDriveArmTests"`: Passed 52, Failed 0.

## Verification
- `.\scripts\run-coverage.ps1 -NonInteractive`: Passed 1818 (0 failed), line coverage 98.27%. (Run before a final doc/format-only edit; the targeted classes were re-run after it.)
- `aislop scan .`: 99/100, 0 errors, 4 warnings, all pre-existing and outside my files (`NativeSeamIsolationFixtures.cs` x2, `CachedBlockDeletionOutcome.cs` x2). My files' findings (formatting, blocking call in async test, null-forgiving, doc comments) were fixed.

## Files changed
MFTLib/Broker/BrokerDiagnostics.cs, MFTLib/Broker/BrokerDiagnosticsWriter.cs (new), MFTLib/Broker/Host/JournalBrokerHost.cs, MFTLib/Broker/Client/JournalBrokerClient.Transport.cs, MFTLib/Broker/Client/JournalBrokerClient.LiveWatchDemux.cs, MFTLib.Tests/BrokerDiagnosticsTests.cs (class now `[DoNotParallelize]`), MFTLib.Tests/ElevatedEntryPointTests.cs, MFTLib.Tests/BrokerPerDriveArmTests.cs.

## Concerns / notes
- Working tree files are LF but `.editorconfig` demands CRLF for C#; aislop's formatter flagged touched files until I converted them to CRLF (git normalizes to LF in the index).
- The elevated broker child is short-lived: lines still queued at process exit are lost (previously synchronous). Later broker tasks may want a bounded flush on shutdown.
- A flush racing a drop while the buffer is full can wait until the next record arrives (documented in the code); only reachable under overflow.
- The drop-report line has no timestamp, matching the brief's format verbatim.
- `dotnet build` on the whole solution fails on the native vcxproj by design; I built managed projects individually plus `init.ps1 -Build`.

# Context common to the index test ports (B2, B3, B4)

Task B1 replaced the shared watch session in `FileIndex` with a per-drive watch-instance state
machine and deleted every index watch test file that no longer compiled. Your task ports a named
set of those files onto the new contract. The old files are read with
`git show c1d43784:MFTLib.Tests/Index/<file>`; the new contract is the code in your worktree.

## What B1 built (read the code; these are pointers, not a substitute)

- `MFTLib/Index/IIndexWatchSource.cs`: a source starts ONE drive and returns an
  `IIndexDriveWatch` handle. There is no merged stream, no arm or disarm, no readiness report.
- `MFTLib/Index/FileIndex.WatchDrive.cs`, `FileIndex.WatchPump.cs`, `FileIndex.DriveRuntime.cs`,
  `FileIndex.RescanRestart.cs`: the per-drive machine (`DriveRuntime`, `WatchInstance`).
- `WatchFaultKind` is `{ Subscriber, Drive, Apply, Channel }`. There is no `Source` kind and no
  null-drive fault. Later tasks add `CatchUpLost` and `Recovery`; do not use them.
- `StartWatchingAsync`, `StopWatchingAsync` and `WaitForCatchUpAsync` exist only in their
  single-drive forms in this wave. The no-list and list forms arrive in task B7.
- `StopWatchingAsync(X)` on a drive that is not watching throws `InvalidOperationException`
  (no watch request and no current or retiring instance). The first stop after a fault rethrows
  that fault once; a second stop then throws `InvalidOperationException`.
- A rescan holds the drive's lifecycle gate from entry to exit, retires the current healthy
  instance and awaits its drain before production, keeps a `Faulted` instance in place, and
  restarts the watch afterwards only while the watch is still requested. A rescan whose scan
  fails or is cancelled restarts a previously healthy watch from its old cursor.
- There is NO automatic recovery yet: a `Drive` fault leaves the drive `Faulted` until a manual
  `RescanAsync(X)`. Task B6 adds recovery later and will change those assertions itself.
- From inside a `Changed` or `WatchFaulted` handler, a blocking call to a lifecycle operation
  (stop, rescan, start, dispose) is not yet rejected and can deadlock on the handler's own pump.
  Task B8 adds the guard. Do not port or write a test that blocks inside a handler on such a call.
- Test support: `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs` and `WatchHarness.cs`, and
  the existing `MFTLib.Tests/Index/FileIndexPerDriveWatchTests*.cs` as the model for style,
  gates and timeouts. Extend the test support only if a ported case cannot be written without
  it, keep the extension minimal, and list it in your report.

## Porting rules

- Port every case that still describes the per-drive contract. A `DriveWatchFailure` item becomes
  `FailDrive`; `WatchFaultKind.Source` with a drive becomes `Drive` or `Channel` by what the case
  exercises; a null-drive case is dropped. Session, readiness, reclaim and ledger cases are
  dropped.
- The commit message lists every dropped method, one per line, with the reason. The tranche
  audit after this wave checks that every base-commit test method in your files is either ported
  or named there. Build that list mechanically: list the `[TestMethod]` names in each base file,
  list them in each new file, and account for every difference.
- Keep the method name when the case is unchanged in meaning; rename only when the old name
  states something that is no longer true, and record the rename in the commit message.
- A ported test that fails against the code is information, not an obstacle. Do not weaken the
  assertion to make it pass and do not change production code. If a case that should hold per
  the spec fails, stop and report it as a suspected B1 defect with the test name, the assertion,
  the observed value, and the spec line.
- You change only test files (your named files and, minimally, test support). Any production
  change is out of scope: report instead.
- Ports are not written test-first; the failing-first rule does not apply to a ported case. It
  does apply to any NEW test your brief names: show it failing by a scratch mutation of the
  production behavior it pins, not committed, and record the mutation in your report.
- Other lanes port other files in parallel in their own worktrees. Create only the files your
  brief names.

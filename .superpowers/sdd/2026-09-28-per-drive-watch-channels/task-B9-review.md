### Plan Compliance
- Issues found:
  - `SettledCount` and report delivery are not ordered by the settlement linearization point. `AddDriveAsync` has already adopted the result and assigned its ordinal when it returns at `MFTLib/Index/FileIndex.Scanning.cs:39`, but the task does not claim its count until `FileIndex.Scanning.cs:45` and does not call the reporter until `FileIndex.Scanning.cs:47`. A task can be preempted in either gap, letting a later-adopted drive receive count 1 or invoke its callback before the count-1 callback. That contradicts the settle-order contract at `MFTLib/Index/IndexDriveOpened.cs:5` and `:18`, and can make block ordinal order disagree with `SettledCount`. The test at `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:55-75` releases T only after U has fully reported, so it does not exercise either race window.
  - Cancellation does not have the required precedence over another settle failure. `SettleDrivesAsync` returns plain `Task.WhenAll` at `MFTLib/Index/FileIndex.Scanning.cs:27`, and `OpenAsync` simply awaits and rethrows it at `MFTLib/Index/FileIndex.cs:140-148`. If the token is cancelled while another settle faults, for example because `OpenProgress.Report` throws at `FileIndex.Scanning.cs:47`, `WhenAll` is faulted and the open surfaces that fault instead of the required `OperationCanceledException`. `Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows` covers cancellation alone at `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:293-300`, not cancellation plus a fault.
  - The binding W40-R1 RED record is incomplete for both new lost-catch-up tests. The command in `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-B9-report.md:67` contains `dotnet test ...` rather than the exact literal command required by the ruling. The failing output is present at `task-B9-report.md:68-69`, but the evidence gate explicitly requires the exact command per new test.
  - Four awaits added to the new test class are not bounded by `HangGuard`: `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:106`, `:132`, `:285`, and `:350`. Passing `TestContext.CancellationTokenSource.Token` into `OpenAsync` does not establish a deadline, so a deadlock in these scenarios can hang the test. This violates the binding test rule that every await be bounded. This is plan-mandated.
- Cannot verify from diff: the reported whole-suite, coverage, and aislop runs. Per the review instructions I did not rerun them; the controller should retain or check the reported artifacts and confirm the five warnings are exactly the four baseline warnings plus the ruled `JournalBrokerHost` constructor warning.
- Focused reads beyond the supplied diff: the diff cut `AddDriveAsync`, `ScanOpenedDriveAsync`, and cleanup functions mid-body, so I read `MFTLib/Index/FileIndex.Scanning.cs`, `FileIndex.CatchUp.cs`, `FileIndex.ScanCleanup.cs`, and `FileIndex.cs` to check the named adoption, cleanup, and lock-order risks.

### Strengths

- The implementation does start one settle task per configured drive and waits for all of them through `Task.WhenAll` (`MFTLib/Index/FileIndex.Scanning.cs:13-27`), so the ordinary single-failure cleanup path cannot race a still-running settle.
- Open adoption remains under `_stateLock`, with ordinal assignment and pending-result state recorded in the same critical section (`MFTLib/Index/FileIndex.Scanning.cs:130-149`). `Drives` continues to rebuild statuses in options order (`MFTLib/Index/FileIndex.cs:98-113`). No gate is acquired under `_stateLock` in the changed path.
- Enumeration walks acquire the process-wide semaphore before the walk and return it through `using` on normal completion, cancellation, and producer exceptions (`MFTLib/Index/FileIndex.Scanning.cs:175-187`; `MFTLib/Index/EnumerationWalkLimit.cs:21-30`). The process-global seam's test class is correctly `[DoNotParallelize]` (`MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:18-20`).
- Both lost-catch-up cases use fake producer results rather than journal windows (`MFTLib.Tests/Index/OpenScenario.cs:40-52`, `:140-163`), matching W5-2, and they assert three per-drive productions, one final progress report, retained status, cleanup, and watch refusal (`FileIndexConcurrentOpenTests.cs:101-159`).
- All nine base `FileIndexOpenProgressTests` methods are represented: seven retain their names and two are renamed to remove configured-order wording (`MFTLib.Tests/Index/FileIndexOpenProgressTests.cs:129-317`). The `ConcurrentQueue` conversion is required for concurrent callbacks. The `FileIndexWatchTests` root split is also forced rather than weakening the journal tests: E still covers the non-MFT status and rejection paths at `MFTLib.Tests/Index/FileIndexWatchTests.cs:99-129`, while the two path lookups now deterministically target T at `:163` and `:184`.
- The README and broker integration edits accurately replace the configured ordinal contract with `SettledCount` and document concurrent open (`README.md:504-520`; `docs/broker-integration.md:307-314`).

### Issues
#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. **Settlement order is not linearized with `SettledCount` or callback order.** See Plan Compliance issue 1.
2. **Cancellation can be masked by another settle fault.** See Plan Compliance issue 2.
3. **Two new tests lack the exact RED command required by W40-R1.** See Plan Compliance issue 3.
4. **New tests contain unbounded awaits.** See Plan Compliance issue 4.

#### Minor (Nice to Have)

1. In the enumeration-limit test's failure cleanup, `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs:203-205` swallows the opening task's result with a continuation. If the open succeeds after an earlier assertion fails, the returned `FileIndex` is never disposed, leaving mappings and owner locks alive for the rest of the process. Observe and dispose a successful result while still swallowing the original test-cleanup exception.
2. `README.md:516` joins the lost-catch-up sentence and the null-default sentence on one long line. This is only presentation polish, but splitting the paragraph would make the newly added contract easier to read.

### Assessment
Task quality: Needs fixes
Reasoning: The main concurrent-open structure, cleanup wait, per-drive publication, port accounting, walk limit, and documentation are solid. The settlement-order race and mixed cancellation/fault behavior violate the public contract, and two explicit test-process gates are not yet satisfied.

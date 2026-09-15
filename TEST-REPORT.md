# MFTLib - Test Report

2026-09-14

| Field | Value |
| --- | --- |
| Status | PASS: every executable line this branch added is covered, the managed suite is green, and the aislop gate reports a clean run at 100/100 with zero findings. |
| Mode | Windows gate run: `scripts/run-coverage.ps1 -NonInteractive` (native Release build, managed build, full non-admin managed suite under coverlet) plus `aislop scan .`. |
| Git | feat/query-borrow-and-cancellation at HEAD, branched from main at bc8c6b5 (HEAD confirmed before the first edit and again before this run). Eighteen commits: eight for the feature, four for the first review wave, five for the second, one for the residual gap the re-review found. |
| Tests | `Test Run Successful. Total tests: 1332, Passed: 1327, Skipped: 5, Total time: 24.0946 Seconds`. The 5 skips are the script's standing non-admin exclusions, not new. 30 of the passing tests are new on this branch (30 added `[TestMethod]` entries, none removed). |
| Coverage | Managed total 98.4% lines, 95.9% branches, 99.6% methods (`MFTLib.Tests/coverage.xml`; the reportgenerator summary reads 5201/5282 lines, 1720/1792 branches, 909/912 methods). New and rewritten types: `SnapshotBorrow` 100%, `SnapshotRelease` 100%, `RowScanner` 100%, `LookupEngine` 100%, `AggregateEngine` 100%, `DuplicateNameFinder` 100%. `SnapshotRelease` reached 100% by deleting the synchronous release rather than by testing it; see the review wave below. |
| Coverage limits | Global coverage is below 100%; the gate that matters is per-changed-line. Every line added by `git diff -U0 main...HEAD -- MFTLib/` was cross-referenced against the cobertura report: across the 11 changed production files, zero added executable lines have a zero hit count. Nine lines in touched files remain uncovered, all pre-existing and unchanged here: `Snapshot.cs`'s finalizer catch body and the `Snapshot.Create` unwind that releases already-taken blocks when a later one is dead; three branches of `IndexNavigation.IsUnder`, which this branch did not touch; `FileIndex.cs`'s throw for a drive with neither an online nor a blockless status; and `SearchEngine.cs`'s empty-partition early return. |
| Lint | aislop 0.16.0: `Clean run - 100 / 100 - Healthy - no issues`, 0 errors, 0 warnings, 328 files, 5 engines. Formatting, AI Slop, Code Quality and Security each reported 0 issues; Linting reported 4 findings, all suppressed by pre-existing `aislop-ignore` directives. Two scans during this branch returned findings, 15 the first time and 7 after the release rewrite, every one in a file this branch changed; all were fixed at the cause rather than suppressed (see Findings fixed). |
| Lint environment | Roslynator cannot open `MFTLib.sln` on this host (`Method not found: FrameworkErrorUtilities.VerifyThrowInternalRooted`, an MSBuild and Roslyn version mismatch) and falls back to per-project analysis; every project it loaded reported 0 diagnostics, matching jb inspectcode. `MFTLibTestExtensions` analyzed cleanly but wrote no report file and was skipped by the aggregator. Both conditions predate this branch and are host conditions rather than defects. |
| Em-dashes | None. Every file in `git diff main...HEAD --name-only` was scanned for U+2014, with zero hits. `scripts/check-em-dashes.ps1` does not exist in this repository. |
| Native | The native DLL was built Release x64 by the coverage script so the managed interop tests could load it. No file under `MFTLibNative/` changed, so native coverage was not re-measured. |
| Platform | Windows only. `scripts/coverage-linux.sh` was not run on this branch; nothing in the change is platform-specific, and the Linux CI job is the confirmation. Admin tests were skipped by `-NonInteractive`; no admin-only path was touched. |

## What this branch changed

A query could read a block mapping while disposal unmapped it. `CurrentSnapshot` handed
out the live snapshot with no reference taken, `RowScanner` captured its spans once and
never rechecked, and `DisposeAsync` force-released every snapshot with no reader gate. A
duplicate-name scan on a worker thread therefore killed a consumer process with an access
violation while its user-interface thread disposed the index.

- `Snapshot.Borrow` hands out a reader's claim, refused with `ObjectDisposedException`
  once a release has begun. `SnapshotRelease` counts borrows under the gate that also
  carries the release flag, so a borrow is either counted and waited for or refused.
- `ReleaseNow`, `ReleaseAsync` and the retired-snapshot release begin the release, drain
  the readers already inside, and only then unmap. The finalizer path keeps its
  non-waiting shape: a borrow holds the snapshot, so a borrowed snapshot is never
  collected.
- `DisposeAsync` cancels a disposal token before it waits for anything, then awaits the
  release instead of blocking a thread on it.
- Every public query (`Find`, `FindByName`, `Search`, `Largest`, `DuplicateNames`,
  `Root`) takes an optional `CancellationToken`, links it to the disposal token, and holds
  a borrow for its whole duration. `RowScanner` reads the token on its first row and then
  every 4096 rows, so every scanning engine inherits cancellation from one place.
- `FileEntry.Children` is the seventh scanning entry point and takes the same borrow and an
  optional token of its own. A handle holds no reference to its index, so it observes only
  its caller's token and disposal waits that listing out rather than cancelling it.

## Regression evidence

Each behaviour was written as a failing test first, and each failure was confirmed before
the implementation went in.

- **Borrow refused after release.** `SnapshotBorrowTests` did not compile against the
  pre-change library: `error CS1061: 'Snapshot' does not contain a definition for
  'Borrow'` and `error CS1061: 'SnapshotRelease' does not contain a definition for
  'OutstandingBorrowCount'`. All five tests passed once the borrow existed.
- **Disposal waits for a borrow.** With `DisposeAsync` still releasing the current
  snapshot through the blocking path, `DisposeAsync_WhileABorrowIsHeld_...` deadlocked:
  the test thread sat inside `ReleaseNow` waiting for the borrow it was about to return,
  and the run had to be killed after it passed its process timeout. Moving disposal to the
  asynchronous release made both borrow tests pass in 458 ms.
- **Scanner cancellation.** With the `ThrowIfCancellationRequested` call removed from
  `RowScanner.MoveNext` and everything else in place, both scanner tests failed with
  `Assert.ThrowsException failed. No exception thrown. OperationCanceledException exception
  was expected.` Restoring the check turned them green, and
  `Scanner_WithATokenCancelledMidScan_StopsAtTheNextCheckpoint` pins the interval exactly:
  a token cancelled on row 10 stops the scan after 4096 rows rather than at the end of the
  block.
- **Disposal cancels a running query.** Before the disposal token was wired,
  `DisposeAsync_WhileADuplicateNameScanIsRunning_EndsTheScanAndCompletes` failed with
  `Assert.IsNotNull failed. the scan finished before disposal reached it, so this run
  proves nothing about the race`: disposal waited for the borrow exactly as it should, and
  the scan ran to completion over all 600,000 rows. With the token wired the scan ends in
  the outcome set the test asserts, `OperationCanceledException` or
  `ObjectDisposedException`, disposal completes, and the test host survives, which is the
  other half of the assertion: an access violation would take the host with it. Run five
  times in a row for stability: 2 passed each time, 158 to 163 ms.
- **Caller cancellation per entry point.** `FileIndexQueryCancellationTests` covers all six
  public queries with a pre-cancelled token and with a live one, and checks the borrow
  accounting both after a query returns and after a query throws. None of it compiled
  before the parameters existed.

## Review wave

An independent review of the branch returned four findings, each fixed with its own test
and commit.

- **A borrow taken before anything that can throw** (`0d8a8da`). `BeginQuery` counted a
  borrow and only then built the token the query observes, so a throw in between would
  strand the count and every later release would wait for a reader that no longer exists.
  The linked source is now built first and given back if the borrow is refused, and
  `Snapshot.Borrow` allocates before it counts. The review reported this as live through
  `CancellationTokenSource.CreateLinkedTokenSource` throwing for a disposed source; that
  does not reproduce on this runtime, and a probe confirmed .NET 10 throws from neither
  `CreateLinkedTokenSource` nor `Register` for a token whose source was disposed without
  being cancelled. The first regression test failed because the query simply succeeded, so
  it was reshaped to pin the accounting rather than the exception.
- **The children listing** (`e26285c`). `FileEntry.Children` scanned every row of a block
  with no borrow, which left the original crash one call away. It now borrows for the scan
  and reads its rows through `RowScanner`.
- **Deterministic dispose-race tests** (`e26285c`). Both race tests opened their gate
  before the query had borrowed anything, so disposal could win and the query would be
  turned away with `ObjectDisposedException`, which the outcome set accepted. Each now
  waits until the snapshot reports an outstanding borrow before disposing, and asserts
  `OperationCanceledException` specifically. The search was widened to match every row,
  because a one-row match finished before the handshake could see its borrow. The
  handshake polls tightly under a bound rather than through
  `SpinWait.SpinUntil`, whose millisecond sleeps stepped straight over a scan that held
  its borrow for only a few of them. Six consecutive runs pass.
- **One release path instead of two** (`38a9973`). Nothing outside tests reached
  `Snapshot.ReleaseNow`, `SnapshotRelease.ReleaseAndWait`, the completion monitor or the
  synchronous borrow drain, and the competing-release wait inside that path was the one
  line no test could cover. All four are gone, the borrow gate is a `Lock` again, and the
  79 test call sites await `ReleaseNowAsync` instead, which is what turned their methods
  async and made `MutatorFixture` `IAsyncDisposable`.

## Second review wave

Four more findings, plus a nit, each with its own commit.

- **Cancel inside the region that releases** (`1e0f1c3`). `DisposeAsync` cancelled the
  queries in flight before the try that releases the mappings, so a throw from a callback
  on that token left every block mapped with the disposed flag already set, which the
  early return makes unrecoverable. The failure is now captured, the release runs, and it
  is raised afterwards with its original stack. The regression test registers a throwing
  callback on the disposal token and failed before the fix with the block still open.
- **No linked source when the caller cannot cancel** (`f34743f`). A query with no token of
  its own linked one anyway, allocating a source and registering on the one source every
  query shares, whose registration takes that source's lock. Such a query now observes the
  disposal signal directly. Both branches are covered: the race tests cancel a query that
  passed no token, a new race test cancels one that passed its own, and the
  started-after-disposal test asserts the refusal path for both shapes.
- **An empty block observes its token** (`27ee2c1`). `RowScanner.MoveNext` returned on the
  range test before the countdown, so a scanner over a block with no rows never read its
  token. The countdown moved ahead of the range test rather than the comment being
  weakened: the loop pays the same decrement and branch either way, and the only change is
  that the call which finds the range exhausted now counts too.
- **README wrap and changelog** (`this commit's sibling`). The paragraph that had run into
  the surviving sentence is wrapped, and `CHANGELOG.md`, which `scripts/release.ps1` uses
  as the release notes, gains the new parameters under Unreleased/Added and a
  recompile-required note under Unreleased/Changed: the optional parameters are source
  compatible but not binary compatible, so a consumer bound to the 0.3.0 assemblies fails
  with `MissingMethodException` until it is rebuilt.

## Residual gap from the re-review

The re-review's verdict was mergeable as is, with one gap left in the disposal failure
path: `DisposeAsync` captured a cancellation failure so the release could still run, and
then rethrew it after the release, but a release that failed too carried the only
exception out and the captured one was dropped without a trace. When both halves fail, the
two now come out together in an `AggregateException`, cancellation first because it
happened first; when only one fails, nothing changes, which an exception filter on the
catch is what guarantees. `DisposeAsync_WhenBothTheCancellationAndTheReleaseFail_ReportsBoth`
drives a throwing registration and a throwing release seam at once, and failed before the
fix with `Threw exception InvalidOperationException, but exception AggregateException was
expected`. Its assertions are on the direct inner exceptions rather than on
`Flatten()`, which collects leaves without preserving that order; membership after
flattening is asserted separately.

## Findings fixed rather than suppressed

The branch's first aislop scan returned 15 warnings, all in changed files:

- 11 `AccessToDisposedClosure`: assertion lambdas captured a `CancellationTokenSource` that
  the enclosing scope disposed. The lambdas now capture a token copy, and the one test
  whose loop body has to cancel the source scans inline in a `try`/`catch` rather than
  inside a lambda.
- 1 `CA1068`: `DuplicateNameFinder.ForEachEligibleHash` took its token before the callback.
  The token moved to last.
- 1 `complexity/too-many-params`: `LookupEngine.TryFindChild` reached seven parameters.
  What is fixed for a whole path descent (snapshot, drive ordinal, case rule, token) became
  one `PathDescent` value, leaving four.
- 1 `ai-slop/csharp-redundant-doc-comment`: the `Root` summary restated the member name. It
  now says which row it resolves and why that is not always the volume root.
- 1 `csharp-formatting`: an object initializer written inside a `using` header. Its options
  are hoisted to a local, matching the producer helper in
  `FileIndexProducerSelectionTests`.

The scan after the release rewrite returned 7 more, also all in changed files: two doc
comments still pointing at the release that was deleted, a timeout field the borrow tests
no longer read, three blocking file and reader calls in test methods that had become
async, and one broad `catch (Exception)` in `BeginQuery`, replaced by a flag and a
`finally` that hand the token source over for every exception type rather than the ones a
catch clause lists.

## Commands

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
aislop scan .
```

```bash
dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "Category!=RequiresAdmin"
```

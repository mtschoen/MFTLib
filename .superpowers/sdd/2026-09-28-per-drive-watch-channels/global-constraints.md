## NO BACKWARD COMPATIBILITY (owner directive)

On MFTLib, file-wizard and git-wizard there is NO compatibility surface of any kind. These have never shipped to an external consumer; the two wizards are dogfooding projects migrated in their pin-bump pull requests. Breaking changes are the purpose of 0.3.0.

- A breaking change is never a cost, a risk, a trade-off, or a reason to prefer one design over another.
- Forbidden: retained old overloads, `[Obsolete]` members, opt-in flags or enum members that preserve old behavior, transitional wire fields, dual paths, adapters from the old shape to the new one, version-bump ceremony, "keep X in case a caller needs it".
- Delete in place. The implementer traces call sites and rewrites the caller.
- Do not ask the owner about any of this. The answer is no.

This plan applies the rule to its own structure. No phase runs old and new watch paths side by side. Where a new layer replaces an old one that other code still compiles against, the task that changes the layer deletes the dependents it breaks, and a later task rebuilds them on the new shape. A dependent that is temporarily absent is not a shim; it is a gap on the integration branch that closes before the pull request. Every task in this plan is green (build, tests, aislop) at its gate except the git-wizard production task (G1), marked red by design; coverage is compared at the tranche ends defined under Waves.

## Per-drive state machine (amendment SM)

This restates spec 2.6 in the terms the tasks use. Everything below is per drive X; `_stateLock` guards every field named.

**Watch instance.** Each start creates a `WatchInstance` with its own identity (the object reference, plus a per-drive `long Generation` for diagnostics). It owns: the start cancellation source (linked to the disposal token), the `IIndexDriveWatch` handle once returned, the pump task and the pump's stop source, the catch-up slot for this instance, the instance's outstanding fault, the block it was armed against (`DriveBlock ArmedBlock`), and a `Drained` task that completes when teardown has finished (handle disposed, pump returned). `DriveRuntime.Current` holds at most one instance; `DriveRuntime.Retiring` holds the previous one until its `Drained` completes.

**Instance states:** `Starting` (source invoked, handle not yet returned), `Running` (handle published, pump running), `Faulted` (pump ended with a fault; recovery may be queued against this instance), `Retiring` (stop requested or superseded; teardown in progress), `Drained` (terminal).

**Drive states** (reported through `DriveStatus.WatchCatchUp`): `NotStarted` (no current instance), `CatchingUp` and `CaughtUp` (current instance `Running`), `Recovering` (a recovery is queued or running for the current faulted instance, or a lost catch-up is being rescanned), `Faulted` (current instance `Faulted` with no recovery pending, or a drive whose watch is refused because its block is unresumable).

**Scoping rule.** Every mutation a pump makes is conditioned on its instance: a batch takes X's write gate, then checks under `_stateLock` that `runtime.Current` is still this instance, still `Running`, and that `instance.ArmedBlock` is still X's published block; otherwise the batch is dropped, not applied. Catch-up completion, fault recording, checkpoint-loss recording and cleanup all check `ReferenceEquals(runtime.Current, instance)` the same way. A retiring pump's accepted batch therefore never reaches a successor's block.

**Linearization points** (each under `_stateLock`):

| Operation | Takes effect when | Before | After |
|---|---|---|---|
| `StartWatchingAsync(X)` | the returned handle is published and the instance moves `Starting` to `Running` | lifecycle gate taken; any `Retiring` instance's `Drained` awaited; the `Starting` instance and its cancellation source registered (R2); the source invoked outside the lock | pump started outside the lock; a handle returned after the instance stopped being `Current` is disposed by the start path, and the start throws `OperationCanceledException` |
| `StopWatchingAsync(X)` | `WatchRequested` is cleared and `Current` moves to `Retiring` (a `Starting` instance has its start source cancelled here) | no gate | teardown outside the lock; returns after `Drained`, bounded by the caller's token; the instance's catch-up slot is cancelled; its outstanding fault is rethrown once |
| `RescanAsync(X)` | the commit that publishes the new block (X's write gate, then `_stateLock`) | lifecycle gate held from entry to exit (R4); the current instance retired and its `Drained` awaited before production | restart through an internal helper that assumes the gate is held, only if `WatchRequested` is still set |
| Recovery | the same commit as a rescan | queued against a faulted instance and its block; after taking the lifecycle gate it revalidates that `Current` is still that instance, still `Faulted`, its block still published, and `WatchRequested` set; otherwise it is dropped (R3) | as rescan |
| `DisposeAsync` | `_disposed` set and the disposal token cancelled | nothing | every drive: current and starting instances retired and drained, queued recoveries cancelled and awaited, then lifecycle gates and write gates in ascending drive-letter order, then snapshot release |

**Lost catch-up (spec 2.6.6).** A scan whose catch-up is lost publishes its block, marks it unresumable and is retried by the same operation under X's lifecycle gate (B5); `Recovering` is published for it when `WatchRequested` is set, and X reads `Faulted` with `RecoveryStopped` after the third consecutive loss.

## Build and test (from AGENTS.md)

Windows, from the worktree root. `MSBuild.exe` is the 64-bit MSBuild that `init.ps1` resolves through `vswhere`.

```powershell
.\init.ps1                                                    # restore and generated agent files
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64
dotnet build -c Release -p:Platform=x64
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~<ClassName>"
.\scripts\run-coverage.ps1 -NonInteractive                   # whole suite plus managed coverage, no UAC
pwsh -NoProfile -File scripts/test-coverage-status.ps1        # offline coverage-publisher regression checks
.\scripts\native-coverage.ps1                                 # native coverage (Debug|x64 /PROFILE build)
aislop scan .                                                 # quality gate, failBelow: 100
```

Linux (tasks that touch native code or test platform exclusions):

```bash
./init.sh --build
scripts/coverage-linux.sh
```

The targeted `dotnet test` line is the iteration command. `run-coverage.ps1 -NonInteractive` is the one whole-suite run per task.

**Standard verification** (what "verify" means in every task below, in this order): rebuild what the task touched (the native line when native code changed, then `dotnet build -c Release -p:Platform=x64`); the targeted `dotnet test` line with a filter naming every test class the task created or ported (`--filter "FullyQualifiedName~ClassA|FullyQualifiedName~ClassB"`); `.\scripts\run-coverage.ps1 -NonInteractive` in the background; `aislop scan .`. Tasks name any extra command (native coverage, Linux build, namespace or seam guards) explicitly.

## Test rules every task inherits

- Tests that reference the process-wide native delegate seams in `MFTLibNative` or `FileUtilities`, including calls to either `ResetToDefaults`, carry class-level `[DoNotParallelize]`. `NativeSeamIsolationTests` enforces this from IL; do not add exclusions to hide a race. The same applies to any test that reads or writes a native test-hook global (`SetMaxThreads`, the new `GetChunkThreadCounts` and `GetResolveThreadCount`) and to `DefaultElevatedEntryRunner._exitProcess`.
- Never assert on wall-clock time. Heartbeat, stall, processing limit, reply timeout, connect timeout and grace period all read an injected `TimeProvider`; tests use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` and advance it. Ordering is proven with `TestSupport/TestGate.cs` signals.
- Async waits poll a condition with a timeout (`Task.WaitAsync(timeout)` on a signal task), never a fixed sleep.
- Every test that opens a `FileIndex` sets `FileIndexOptions.CacheDirectory` to an owned temporary directory; the module initializer rejects the default cache.
- Every bug fix gets a regression test that is seen failing before the fix and passing after.
- Test-driven order inside a task: write the named tests, run them and see each fail for the stated reason, implement, run them green.
- A task that deletes an old test file whose cases a later task ports writes nothing to replace it; the porting task reads the old file from the base commit with `git show c1d43784:<path>`.

## Lane rules every task inherits

- Work only in the task's own worktree `~/MFTLib-worktrees/<task-id>` (sibling of the checkout, never nested), branched from the integration branch head the orchestrator names. FIRST ACTION: confirm `git rev-parse HEAD` equals the named SHA; on mismatch stop and report, never merge, rebase or reset to self-correct.
- Do not commit on the integration branch; commit only on the task branch. The orchestrator merges.
- Any command over about 30 seconds (MSBuild, `dotnet build` of the solution, `run-coverage.ps1`, `aislop scan`) runs with `run_in_background`, teed to a scratch file; keep working and read the file on the completion notice. Never re-run a command to see more output.
- Targeted tests while iterating; one whole-suite run and one `aislop scan .` at the end; at most three fix-and-rerun rounds, then report.
- Do not launch GUI apps and do not use computer use. Do not run anything that needs elevation; the measurement tasks M1 to M3 are the owner's.
- Identifiers use full words (`maximum`, not `max`; `configuration`, not `config`).
- No em-dashes or en-dashes in anything written (code, comments, docs, commit messages). Use ` - `, colons or parentheses.
- Keep code files under about 500 lines; split partial classes by responsibility as the codebase already does.
- Native error text goes through `SetErrorMessage` in `MFTLibNative/internal.h`.
- Do not edit `.aislop/config.yml`. Fix aislop findings; explain false positives in the report.
- Describe the target state in comments and docs; no "no longer" history notes.

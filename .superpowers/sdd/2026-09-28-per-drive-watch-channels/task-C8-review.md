### Plan Compliance
- Issues found: N-2 and W40-R1 require a literal scratch-mutation command for every scenario. The report supplies only a placeholder command with `<file>`, `<old>`, `<new>`, `<filter>`, and `<label>`, while the per-test rows supply mutation labels and prose but no executable commands (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C8-report.md:17`, `:21-28`). The helper-level bounded-await rule is also violated by unbounded teardown awaits (`MFTLib.Tests/TestSupport/CrossDriveScenario.cs:201`, `:204`).
- Cannot verify from diff: the claimed mutation failures, green test runs, coverage result, and aislop result are execution claims in the report rather than static diff evidence (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C8-report.md:21-28`, `:36-38`). The controller should validate the archived command outputs if independent execution evidence is required.

### Strengths
- All scenarios named by the brief and W4-2 exist. The held-write case proves that only T gets a `Channel` fault while U remains watching, receives cadence heartbeats, and applies a later batch (`MFTLib.Tests/BrokerCrossDriveLivenessTests.cs:32`, `:76-90`); the idle no-channel case keeps `HasEnded` false past the stall limit and proves the control pipe still answers (`MFTLib.Tests/BrokerCrossDriveLivenessTests.cs:198`, `:205-214`).
- The three-loss scenario pins the exact three channel and fault counts, loss counts 1/2/3, `RecoveryStopped` only on the third, no `Recovery` fault, the retained third block, and the `JournalSizeArithmetic` result (`MFTLib.Tests/BrokerCrossDriveLivenessTests.CatchUp.cs:46-67`). It also proves U is unaffected and a fixed source resets the count and permits a new start (`MFTLib.Tests/BrokerCrossDriveLivenessTests.CatchUp.cs:70-82`).
- Both host and client use explicit fake clocks, cadence waits are bounded, and the byte-count derivation accounts for the host's skip-after-write rule before advancing the client clock (`MFTLib.Tests/TestSupport/CrossDriveScenario.cs:46-54`, `:156-194`). No real-time delay, polling sleep, or elapsed-time assertion was added.
- The partial test class is `[DoNotParallelize]`, covering the process-wide journal override (`MFTLib.Tests/BrokerCrossDriveLivenessTests.cs:14-17`). The changed harness exposes inputs and production surfaces, but no member that observes host failures (`MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs:29-57`, `:131-148`).

### Issues
#### Critical (Must Fix)
- None.

#### Important (Should Fix)
- N-2/W40-R1 evidence is incomplete. The generic invocation at report line 17 is a template, not the literal command required for each scenario, and rows 21-28 do not fill in its file, replacement strings, filter, or label. The reported failure snippets therefore do not satisfy the binding command-plus-output evidence requirement (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C8-report.md:17`, `:21-28`).
- The new scenario helper can hang during cleanup. `DisposeAsync` directly awaits both index and broker disposal with no timeout or bounded token, and the open failure path directly awaits that same cleanup (`MFTLib.Tests/TestSupport/CrossDriveScenario.cs:88`, `:196-204`). A regression that strands a pump or host task can therefore wedge the test run after the test body, contrary to the explicit rule that helper awaits are bounded.

#### Minor (Nice to Have)
- None.

### Assessment
Task quality: Needs fixes
Reasoning: The scenario coverage and assertions are strong, but the binding RED-evidence format is not met and teardown is not hang-bounded. Both are explicit task gates and should be corrected before approval.

# Maintained developer tools

Only redundant code is deleted. A developer tool is not redundant because nothing
in production or CI calls it. If a tool has no CI use, CI should exercise it,
not delete it. Attended and release entry points are maintained alongside agent
and CI entry points.

The table is the maintained inventory. Add a row when adding a script, including
helper libraries and self-tests. Each row states its purpose, runner role and CI
coverage. `AgentInstructionsTests.MaintainedTools_ListEveryScript` checks the
inventory in the always-running docs job and both platform coverage suites.

**Syntax** below means the Windows job's always-running **Parse every maintained
script** step, `pwsh -NoProfile -File scripts/test-script-syntax.ps1`. It discovers
tracked and untracked/unignored files recursively under scripts/, parses
PowerShell with Parser.ParseFile and Bash with bash -n, and never executes the
target script. New script languages require an explicit parser; unsupported
languages fail. Generated ignored files are not maintained inputs. Stage intended
deletions before checking so Git's inventory reflects the change.

**Windows**, **Linux**, and **Linux package** refer to the corresponding jobs in
.gitea/workflows/test.yml. Behavioral steps retain their documented docs-only
skip conditions; Syntax runs even for docs-only changes. A syntax check establishes
parseability, not successful elevation, publication, WSL builds, or raw-volume I/O.
Pure helpers use the offline suites named below. Orchestration-only tools may
have Syntax as their baseline coverage.

| Tool | Purpose | Who runs it | CI coverage |
| --- | --- | --- | --- |
| `scripts/Test-ReleasePackages.ps1` | Validate release ZIP assets, symbols and Windows runtime | release, agent via callers | Syntax; Windows test-release-package-validation.ps1 |
| `scripts/WindowsNativeInstructions.ps1` | Validate disassembly attribution and x64 instruction baseline | release, agent via package validation | Syntax; Windows test-release-package-validation.ps1, including synthetic instruction cases |
| `scripts/build-linux-native.ps1` | Build and validate Linux release native library through WSL | release | Syntax; WSL orchestration is hand-run |
| `scripts/build-linux.sh` | Build Release Linux native library and managed Smoke tier | agent, release, CI | Syntax; Linux package build step |
| `scripts/build-windows.ps1` | Restore and build native and managed Windows projects | attended, agent, release, CI | Syntax; Windows build-windows.tests.ps1 and coverage build |
| `scripts/build-windows.tests.ps1` | Check build discovery, callers and project outputs | agent, CI | Syntax; Windows Test Windows build script |
| `scripts/changed-files.sh` | Classify docs and pin-only changes | agent, CI via preflight | Syntax; Windows and Linux ci-preflight.tests.sh |
| `scripts/check-linux-native.sh` | Validate ELF architecture, exports, paths and runtime floors | agent, release, CI | Syntax; Linux package check-linux-native.tests.sh and real library check |
| `scripts/check-linux-native.tests.sh` | Exercise native acceptance rules with offline command doubles | agent, CI | Syntax; Linux package Test Linux native acceptance checks |
| `scripts/ci-aislop-evidence.ps1` | Write uploaded scanner evidence summary | CI, agent | Syntax; Windows ci-aislop.tests.ps1 |
| `scripts/ci-aislop-link-repair.ps1` | Repair restored scanner dependency links | CI, agent | Syntax; Windows ci-aislop.tests.ps1 |
| `scripts/ci-aislop.ps1` | Run pinned scanner and retain diagnostic evidence | agent, CI | Syntax; Windows ci-aislop.tests.ps1; aislop quality-gate workflow |
| `scripts/ci-aislop.tests.ps1` | Regress scanner execution and evidence | agent, CI | Syntax; Windows Test pinned aislop checkout gate |
| `scripts/ci-changed-files.sh` | Adapt CI event revisions to the changed-path classifier | CI | Syntax; Windows and Linux ci-preflight.tests.sh |
| `scripts/ci-post-clean-destructive.ps1` | Verify initialization in a destructively cleaned disposable checkout | CI, agent in disposable checkout | Syntax; Windows ci-preflight.tests.sh fixtures and main-push post-clean step |
| `scripts/ci-preflight.tests.sh` | Regress local dispatcher, classifier and workflow wiring | agent, CI | Syntax; Windows and Linux Test the CI preflight dispatcher |
| `scripts/coverage-child.ps1` | Run and monitor coverage children using explicit completion evidence | attended, agent via coverage callers | Syntax; Windows test-native-coverage-elevated.ps1 |
| `scripts/coverage-linux.sh` | Collect Linux native and managed coverage | agent, CI | Syntax; Linux Run coverage |
| `scripts/coverage-log.ps1` | Incrementally stream coverage logs, retaining partial lines | attended, agent via coverage children | Syntax; Windows test-native-coverage-elevated.ps1 |
| `scripts/coverage-status.ps1` | Validate measurements and choose coverage status | agent, CI via publisher | Syntax; Windows test-coverage-status.ps1 |
| `scripts/mftlib_submodules.sh` | Resolve consumer submodule repository identities | release, CI via sync | Syntax; Linux sync_consumers.tests.sh |
| `scripts/native-coverage-elevated.ps1` | Elevate native coverage and stream its result | attended, agent | Syntax; Windows test-native-coverage-elevated.ps1 |
| `scripts/native-coverage.ps1` | Build and instrument Debug native code and report coverage | attended, agent | Syntax; Windows build-windows.tests.ps1 caller fixtures |
| `scripts/post-coverage-status.ps1` | Publish coverage verdict to the forge | CI | Syntax; Windows test-coverage-status.ps1 tests delegated logic; Post coverage status runs the adapter |
| `scripts/release.ps1` | Build, validate, package and optionally publish a release | release | Syntax; Windows test-release-notes.ps1, test-release-package-validation.ps1 and build-windows.tests.ps1 |
| `scripts/run-coverage.ps1` | Collect managed coverage with optional elevated tests | attended, agent, release, CI | Syntax; Windows test-native-coverage-elevated.ps1 and Run coverage |
| `scripts/script-syntax.tests.ps1` | Prove script discovery, rejection and non-execution | agent, CI | Syntax; Windows Test script syntax gate |
| `scripts/smoke-package-linux.sh` | Verify Linux package-native resolution through a local consumer | agent, release, CI | Syntax; Linux package Smoke test the package |
| `scripts/smoke-package-windows.ps1` | Verify Windows build and RID-less publish package resolution | agent, release, CI | Syntax; Windows Smoke Windows NuGet native resolution |
| `scripts/sync_consumers.sh` | Publish consumer gitlink updates and pull requests | release, manually dispatched CI | Syntax; Linux sync_consumers.tests.sh |
| `scripts/sync_consumers.tests.sh` | Regress consumer discovery and pin updates offline | agent, CI | Syntax; Linux Test the consumer sync script |
| `scripts/test-agent-instructions.sh` | Restore, build and run managed instruction guards | agent, CI | Syntax; docs job and Linux initialization verification |
| `scripts/test-coverage-status.ps1` | Regress coverage validation and publication decisions offline | agent, CI | Syntax; Windows Test coverage status guard |
| `scripts/test-native-coverage-elevated.ps1` | Regress child protocol, streaming and coverage callers | agent, CI | Syntax; Windows Test shared coverage child runner |
| `scripts/test-release-notes.ps1` | Regress release URL, notes and publication failure handling offline | agent, release, CI | Syntax; Windows test-release-offline.sh |
| `scripts/test-release-offline.sh` | Run offline release validation suites | agent, release, CI | Syntax; Windows Test offline release guards |
| `scripts/test-release-package-validation.ps1` | Regress package requirements and native rejection controls | agent, release, CI | Syntax; Windows offline release guards and Windows package smoke native integration |
| `scripts/test-release-packaging.ps1` | Validate full two-platform release packages and package smoke | release, attended | Syntax; full WSL packaging is hand-run |
| `scripts/test-script-syntax.ps1` | Parse the discovered script set without running it | agent, CI | Syntax; Windows script-syntax.tests.ps1 |
| `scripts/windows-tools.ps1` | Discover checked Windows build and inspection tools | attended, agent, release via callers | Syntax; Windows build-windows.tests.ps1 |
| `scripts/optional-provisioning/OptionalProvisioning.csproj` | Dispatch optional checkout provisioning | attended, agent via init | Windows and Linux optional-provisioning adapter regression harness |
| `scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj` | Run offline provisioning and init regression fixtures | agent, CI | Windows and Linux Test optional provisioning and platform init steps |
| `scripts/package-smoke/PackageSmoke.csproj` | Load and validate an MFT dump as a package consumer | agent, release, CI via smoke scripts | Windows and Linux package smoke steps |
| `SampleProgram.Direct/SampleProgram.Direct.csproj` | Direct volume and dump command-line sample | attended, agent | Both platform coverage jobs: DirectArgumentsTests and DirectHostTests |
| `SampleProgram.Watch/SampleProgram.Watch.csproj` | Broker-backed index and watch command-line sample | attended, agent | Both platform coverage jobs: WatchArgumentsTests and WatchHostTests |
| `Benchmark/Benchmark.csproj` | `index`: packed-index queries; `compare`: saved medians; `native-timings`: native parse phases; `enumeration-walk`: sequential/parallel enumeration over a generated tree | developer, release, agent | Both platform coverage jobs: BenchmarkRunnerTests, including generated-tree rows, both walk modes, fake-clock reports and cleanup |

## Local checks

On Windows with PowerShell 7 and Git Bash:

    bash tools/ci-preflight.sh --only scripts --only preflight-tests

On Linux, run the offline native acceptance regression directly:

    bash scripts/check-linux-native.tests.sh

The existing linux-package preflight scope includes that regression before its
real build/check/smoke. The scripts preflight scope reports Windows parser gates
unavailable on Linux. A provisioned PowerShell host can also run the two syntax
commands directly. Missing parser prerequisites are failures, not passing checks.

Run the always-on inventory and instruction checks through:

    bash scripts/test-agent-instructions.sh

See [CI and quality gates](quality-gates.md), [coverage procedures](development-coverage.md),
[Linux package](linux-package.md), and [attended runs](elevation.md) for operational details.

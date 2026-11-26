# CI and quality gates

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` defines four shared validation jobs on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch (the merge train validates a batch there before landing it): `windows`, `linux`, `docs`, and `linux-package`. The report-specific jobs are described below. The coverage jobs (`windows` and `linux`) run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires the `test / windows`, `test / linux`, `test / linux-package` and `aislop / quality-gate` `(pull_request)` checks to pass before merge; the `test / docs (pull_request)` check runs on every pull request but is not a required check.

The Windows job first parses every maintained script and runs the syntax gate's
offline regression suite. Both steps run before `id: changed-files`, including
on docs-only changes. Windows, Linux and Linux-package use `id: changed-files`
to control their build and behavioral steps. All three call
`bash scripts/ci-changed-files.sh`; Windows launches it from
PowerShell using runner-provisioned Git Bash. The adapter writes
`skip_build=true|false` and one `run_<scope>=true|false` per
[path scope](#path-scopes) to `GITHUB_OUTPUT`. For a pull request it fetches the
base branch tip and compares `FETCH_HEAD` directly with `HEAD`, not their
merge base. On other events it selects the full run without inspecting Git.

`scripts/changed-files.sh` is the single executable classifier used by CI
and local preflight. It accepts newline-delimited repository-relative paths,
removes one terminal CR per record for CRLF input, and ignores empty records.
Matching is case-sensitive: only `.aislop/fork-commit`, nonempty paths under
`docs/`, and root filenames with a nonempty stem ending in `.md` qualify.
Literal spaces are
preserved. Backslashes, absolute paths, dot/dot-dot components, empty path
components, trailing separators, and control characters are rejected rather
than normalized into allowed paths. `README.md` and `docs/UPPER.MD` qualify;
`README.MD`, `Docs/page.md`, `nested/README.md`, and `docs\page.md` do not.
Whitespace-only records do not qualify. Git-quoted unusual names can
conservatively cause full execution.

Skipping requires a successfully inspected, nonempty, entirely allowed path
set on a pull request. Empty input, missing references, and failed
fetch/diff commands run the jobs in full. Output from
a failed inspection is never classified, even if it contains allowed paths.
An unwritable Actions output file fails the detector step. With a valid
skip decision, compilation, test, and package steps are skipped and the
three jobs finish green with their status checks present. Workflow-level
`paths` and `paths-ignore` filters are avoided because they leave required
status checks pending permanently on Gitea (issue 36895).

### Path scopes

On a pull request, tooling self-tests are selected by their inputs. The
same classifier, run as `bash scripts/changed-files.sh --path-scopes`, prints
the selected scopes one per line. Each rule in its
`changed_files_path_scope_rules` table pairs a fixed, case-sensitive path or
glob (`*` also matches `/`) with the scope it selects, `all`, or `none`. A
path collects the scopes of every rule it matches. A path that matches no rule
and is not a docs-only path selects every scope, as does an unsafe path (the
rejections above) or an empty path set. Docs-only paths select nothing, except
`CHANGELOG.md`, which selects `release-offline`.

The adapter writes `run_<scope>` with `-` replaced by `_`. The value is
`false` only when the inspection and both classifications succeeded and
the scope was not selected; `skip_build` never overrides a selected scope (a
tracked `docs/Example.csproj` still selects `build-script`). A failed fetch,
diff or classification writes
`skip_build=false` and every
`run_<scope>=true`. Tooling steps are guarded by
`steps.changed-files.outputs.run_<scope> != 'false'`, so a missing output
also runs the step. Main pushes, merge-train pushes and nightly runs execute
all tooling self-tests and product suites as a backstop for missed inputs.
Build, coverage, package, upload, status and post-clean steps keep the
`skip_build` guard, so every non-docs change still builds and runs the
managed and native test suites.

Changes to `.gitea/workflows/*`, `scripts/changed-files.sh`,
`scripts/ci-changed-files.sh`, `tools/ci-preflight.sh` and
`scripts/ci-preflight.tests.sh` select every scope. Paths under
`MFTLib/`, `MFTLib.Tests/`, `MFTLibTestExtensions/`, `MFTLibNative/`, the
three sample directories, `Benchmark/` and `ExternalAnnotations/`, plus
`scripts/package-smoke/Directory.Build.props`, `Program.cs` and
`packages.lock.json`, `Directory.Build.targets`, `MFTLib.sln.DotSettings`,
`LICENSE.txt`, `.plan` and `.maintenance.json`, are known and select no scope
by themselves. A project file under them still selects its scopes below; any
other file under `scripts/package-smoke/` (such as `Notes.txt`) is unknown
and selects every scope.

Each scope's inputs are the script under test, its test script, what it
dot-sources or runs, and the tracked files it reads, found by reading each
test script's `.`/`source` lines, invocations and `Join-Path $sourceRoot`
or `$PSScriptRoot` reads. Fixture files a test writes into a temporary
directory are not inputs.

| Scope (output) | CI steps | Inputs |
| --- | --- | --- |
| `preflight-tests` (`run_preflight_tests`) | Windows and Linux Test the CI preflight dispatcher | the all-scope files above (the suite sources the dispatcher and classifier and reads `test.yml` and `aislop.yml`), `scripts/ci-post-clean-destructive.ps1` |
| `build-script` (`run_build_script`) | Windows Test Windows build script | `scripts/build-windows.ps1`, `build-windows.tests.ps1`, `windows-tools.ps1`, `coverage-child.ps1`, `coverage-log.ps1`, `native-coverage.ps1`, `run-coverage.ps1`, `release.ps1`, `Test-ReleasePackages.ps1`; `init.ps1`, `init.sh`, `MFTLib.sln`, `Directory.Build.props`, `MFTLibNative.Linux.props`, `native-coverage.runsettings`, every `*.csproj` and `*.vcxproj` |
| `aislop-tests` (`run_aislop_tests`) | Windows Test pinned aislop checkout gate | `scripts/ci-aislop.ps1`, `ci-aislop.tests.ps1`, `ci-aislop-evidence.ps1`, `ci-aislop-link-repair.ps1`, `.gitignore` |
| `coverage-child` (`run_coverage_child`) | Windows Test shared coverage child runner | every `scripts/**` file ending `.ps1`, `.psm1`, `.sh`, `.bat` or `.cmd` in any letter case: the test sources `coverage-child.ps1`, `coverage-log.ps1`, `run-coverage.ps1` and `native-coverage-elevated.ps1`, and scans every such script for the `runas` spelling |
| `coverage-status` (`run_coverage_status`) | Windows Test coverage status guard | `scripts/coverage-status.ps1`, `test-coverage-status.ps1`, `run-coverage.ps1`, `coverage-linux.sh`; `MFTLib.Tests/MFTLib.Tests.csproj` |
| `generate-test-report` (`run_generate_test_report`) | Windows Test summary report generator | `scripts/generate-test-report.ps1`, `generate-test-report.tests.ps1`, `coverage-status.ps1` |
| `release-offline` (`run_release_offline`) | Windows Test offline release guards | `scripts/test-release-offline.sh`, `test-release-package-validation.ps1`, `test-release-notes.ps1`, `Test-ReleasePackages.ps1`, `WindowsNativeInstructions.ps1`, `windows-tools.ps1`, `release.ps1`; `CHANGELOG.md` |
| `init` (`run_init`) | Windows and Linux Test optional provisioning and platform init | `init.ps1`, `init.sh`, `init.bat`, `scripts/build-windows.ps1`, `windows-tools.ps1`, `build-linux.sh` (the files the harness copies into its fixture), `scripts/optional-provisioning/**` |
| `sync` (`run_sync`) | Linux Test the consumer sync script | `scripts/sync_consumers.sh`, `sync_consumers.tests.sh`, `mftlib_submodules.sh`, `auto_bump_pull_request.sh` |
| `linux-package-tests` (`run_linux_package_tests`) | Linux package Test Linux native acceptance checks | `scripts/check-linux-native.sh`, `check-linux-native.tests.sh` |

The script syntax steps run before the detector and stay unconditional. The
Linux initialization check, which runs a real `./init.sh --build`, keeps the
`skip_build` guard because it builds the product. No step exercises native code
alone: managed tests load the native library, Linux native line coverage comes
from the managed suite, and the package job runs the managed Smoke tier, so
native-relevant paths gate nothing separately.

The `docs` job always runs without an early exit. Running on the fleet Linux runner image, it restores `MFTLib.Tests.csproj` with `dotnet restore --locked-mode`, performs a managed-only build (`dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-restore`), and executes `AgentInstructionsTests` to verify character budgets, document link integrity, scanner and consumer-sync server context, maintained-tool inventory completeness and metadata, and tracked-content portability. Because `AgentInstructionsTests` requires no native code and `Directory.Build.targets` copies `MFTLibNative` only when present, the job remains fast and cheap while ensuring docs changes are validated.

The [maintained-tools inventory](maintained-developer-tools.md) names every
script's purpose, runner role and CI coverage. `scripts/test-script-syntax.ps1`
parses PowerShell and Bash without executing target bodies, and rejects unknown
script languages. `scripts/script-syntax.tests.ps1` proves invalid syntax fails,
new nested scripts are discovered and parse-only checks do not execute tools.
These steps use the Windows runner's existing PowerShell and Git Bash.

The `linux` job verifies the initialization output before coverage whenever
`skip_build` is false. It runs `./init.sh --build`, checks for
`MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll`, runs
`AgentInstructionsTests` with Release/x64 and `--no-build`, then runs
`bash scripts/test-agent-instructions.sh`. Checking before the script's own
build ensures initialization produced the expected output.

The `windows` job also runs `scripts/ci-post-clean-destructive.ps1`, the post-clean init verification (`git clean -ffxd`, `init.ps1` twice, `init.bat`, `init.ps1 -Build`, and a clean-tree check after each) described in [checkout maintenance](checkout-maintenance.md). It runs only on the push to `main`: the step is guarded by `github.event_name == 'push' && github.ref == 'refs/heads/main'` (plus the skip-build check), never by workflow-level `paths`, which leaves a required check pending on Gitea (issue 36895). The step stays in the same job and checkout, after the build and tests, because the first `git clean -ffxd` plus `git status --porcelain` assertion detects a build or test that modified a tracked file. A pull request therefore never proves the clean-tree invariant; the merge to `main` does.

The `windows` job also runs `scripts/test-init-pester.ps1` (Pester 6, one process, mocks) for the branch logic of `init.ps1`, guarded by the same skip-build condition. Pester 6 is a checked, never installed, Windows prerequisite: a runner without it fails that step and is fixed at the service.

Both platform test jobs also run the package-free init/provisioning regression harness before coverage, guarded by the `init` [path scope](#path-scopes). These offline checks exercise the actual entry points with command doubles, including absent and failed optional provisioning, and run on pull requests. They are separate from the Windows main-push-only post-clean verification and the Linux real-build initialization check.

The Windows job runs `bash scripts/test-release-offline.sh`, which runs
`scripts/test-release-package-validation.ps1` and `scripts/test-release-notes.ps1`,
before coverage when `run_release_offline` is not false. `ci-preflight.sh --all` runs the same bundle as `release-offline`.
These offline checks reject incomplete release packages and verify that a
failed GitHub tag push stops before GitHub release creation; they also retain
the release-note extraction and UTF-8 encoding checks.

After coverage has built Release/x64, the Windows job runs
`scripts/smoke-package-windows.ps1`. It verifies Windows package-native resolution in
ordinary build and RID-less publish outputs using an isolated PackageReference consumer
and an empty package cache. The step follows the existing `skip_build` condition.
The smoke also validates the selected package's Windows imports and plain-x64
instruction baseline, then runs the real AVX2 negative-control regression.

The `linux-package` job builds the Release linux-x64 library, runs `scripts/check-linux-native.sh` on it and smoke-tests the packed library through `scripts/smoke-package-linux.sh`; the release rebuilds the library in WSL from its validated release commit ([Linux package](linux-package.md)).

Windows jobs run on the host-mode `windows-latest` runner, where toolchains are provisioned on the host: no `actions/setup-*` step belongs in a job that runs on it (`microsoft/setup-msbuild` only locates MSBuild and is allowed). The `linux`, `docs` and `linux-package` jobs inherit the fleet image selected by the `ubuntu-latest` runner label. The image supplies CMake, Ninja, gcovr, .NET SDK 10, g++, gcov and make; workflows do not install those tools. The runner sets `--ulimit nofile=65536:65536` for each container.

Keep `pwsh` on the runner account's PATH and configure `DOTNET_INSTALL_DIR` at the runner service, never through workflow bootstrap steps. Use amd64 MSBuild and the shared Windows build script. Keep coverlet's comma-separated include filter in the project file rather than passing it through PowerShell. Compute file hashes in the shell because `hashFiles()` can evaluate empty on this runner. Every workflow upload uses the pinned `ChristopherHX/gitea-upload-artifact` action, because `actions/upload-artifact@v3` uploads are not listed by this Gitea deployment's artifacts REST API, which pr-crew reviews read.

## Native build cache

The Windows job, the Linux job (coverage build) and the Linux-package job cache their native build output with
`actions/cache@v4`, so a run whose native inputs are unchanged restores the output and skips only the native build.
Everything else runs: managed builds, tests, coverage, checks and artifacts. The cache applies to every event type
(pull requests, main pushes, merge train, nightly); it is not path filtering.

Each job computes its key with `bash scripts/native-build-cache-key.sh <flavor>` (`windows-release`,
`linux-coverage`, `linux-release`; `windows-debug` is also accepted) in one step, in the shell, because `hashFiles()`
can evaluate to an empty string on act_runner. The key hashes, in order: the flavor; the toolchain identity (the MSVC
toolsets and Windows SDKs found through `Get-WindowsVisualStudioInstallation` in `scripts/windows-tools.ps1`, or the
`g++ --version` and `cmake --version` banners); the tree hash `git rev-parse HEAD:MFTLibNative`; the blob hashes of
`Directory.Build.props`, `Directory.Build.targets`, `MFTLibNative.Linux.props`, `scripts/build-linux.sh`,
`scripts/build-windows.ps1`, `scripts/coverage-linux.sh`, `scripts/native-build-cache-key.sh` and
`scripts/windows-tools.ps1`; and, on Linux, the workspace path (CMake and gcov record absolute build paths, so the
coverage directory restores only at the same path). `vars.CACHE_EPOCH` is hashed for manual invalidation. The
list lives in `native_cache_key_files`; `scripts/native-build-cache.tests.sh` pins it, shows that each input, the flavor,
the toolchain and the epoch change the key, and, where `g++` exists, runs the compiler's dependency list over every
tracked native source. On Linux it also preprocesses the translation units with the compile commands CMake generates
for Release and Debug with coverage. Both checks reject non-system headers outside `MFTLibNative/`; neither inspects
Windows-only includes. Make-escaped dependency names (including names with spaces) fail these checks.

Windows Actions builds exclude `Microsoft.Cpp.x64.user.props` from `UserRootDir`: a runner's personal property sheet
is not a cache input. Local Debug and Release builds still import it when present. No Windows key is produced outside Actions
or when `CL` or `_CL_` supplies additional compiler options.

Linux cache keys are for the workflow's clean CMake configurations with `CXX=g++`. A different or unset `CXX`,
custom `CXXFLAGS` or `LDFLAGS`, GCC include/library/tool search overrides, or a CMake toolchain file or compiler/linker
launcher bypasses the cache. The key does not describe arbitrary local CMake arguments or system headers; retire keys
with `CACHE_EPOCH` when changing the runner image without changing the version banners.

The key is exact. The cache steps never use `restore-keys`: a prefix match would restore a binary built from different
sources and report `cache-hit` false while still leaving the files in place. Cached paths: Windows
`x64/Release/MFTLibNative.{dll,pdb,lib}`; Linux coverage `build/linux-coverage` (including any `*.gcda`, which the
script discards before testing); Linux release `build/linux/libMFTLibNative.so`. The post step saves only when the job succeeds, and an
exact hit is never saved again.

A successful restore with an exact hit passes one input to the build scripts: `-NativeOutputSupplied` for `run-coverage.ps1` and
`build-windows.ps1`, `--native-output-supplied` for `build-linux.sh` and `coverage-linux.sh`. The script validates the
restored files itself (Windows: dll, pdb and lib present and non-empty; coverage: library, `CMakeCache.txt` and a
non-empty `.gcno` note for every object in Ninja's library input list; release: the library) and skips only the native
step. Without a validated hit, Actions rebuilds coverage from an empty directory: a failed extraction can leave
up-to-date objects without their notes. Local builds without the supplied-output flag remain incremental. MSBuild's
incremental checks are not relied on, because restored timestamps and absent tlogs make them untrustworthy. A key
failure, a cache miss or a failed restore builds as before, and a bad cache entry can be retired by changing
`CACHE_EPOCH`. The Linux job's `init.sh --build` and the post-clean `init` step on main still build natively, because
they verify initialization itself.

## Generated test summary

`test / report-guard` runs on every pull request and rejects any PR diff touching
`TEST-REPORT.md`, including additions and deletions. The report is generated on main
by CI, never edited in a PR or written by local coverage scripts.

The `test` workflow also runs nightly at 03:23 UTC. After successful `windows` and
`linux` coverage jobs, `publish-report` runs only on main pushes and scheduled main
runs. A push by `test-report-bot` runs the full validation suite but skips
`publish-report`, so publishing a report cannot trigger another publication.
Nightly runs collect fresh results even when main has not changed.

The publisher downloads the current run's `windows-coverage` and `linux-coverage`
artifacts with the pinned `ChristopherHX/gitea-download-artifact` action, paired with
the upload pin, verifies their `commit.txt` markers against the
source commit, and runs `scripts/generate-test-report.ps1`. The summary stays below
4096 UTF-8 bytes: per-project TRX Counters totals, passed, failed and skipped counts,
managed Cobertura line and branch coverage, optional Linux native line coverage,
source commit and workflow run id as plain text (no host links, so the tracked file passes the portability guard), and the run's creation timestamp. Skipped counts come from the per-result NotExecuted outcomes, because the producer leaves the Counters notExecuted attribute at zero. Full TRX
and coverage output remain CI artifacts. The generator reuses the coverage status
helper's invariant percentage validation. Identical inputs produce identical bytes.

Publication uses `CI_REPORT_TOKEN`, the `test-report-bot` credential (the only bot allowed to
push to a default branch), for the checkout, the push and the workflow-run lookup, and commits
as `test-report-bot` (address `test-report-bot@<domain>`, the server host without its first label) with `chore: regenerate TEST-REPORT.md from <run id>`. An identical
file is not committed. A run whose source is no longer main's tip does not publish;
a concurrent main update makes the ordinary non-force push fail safely. The first
report is created by CI after this workflow reaches main.

Run the offline fixtures with `pwsh -NoProfile -File scripts/generate-test-report.ps1`
or `pwsh -NoProfile -File scripts/generate-test-report.tests.ps1`. To generate a local
sample, supply `-WindowsTrx`, `-WindowsCobertura`, `-LinuxTrx`, `-LinuxCobertura`,
`-Commit`, `-RunId`, `-GeneratedAt`, and an ignored `-OutputPath`;
`-LinuxNativeSummary` is optional. Do not generate the root report locally.

### Windows outage switch

While no Windows runner is available, the repository variable `WINDOWS_CI` set to `off`
skips the `windows` job in `test.yml` and the `quality-gate` job in `aislop.yml`, for pull
requests, merge-train pushes and main. The variable has no expiry. While it is `off`,
Windows coverage and the Windows aislop gate are not validated, and `publish-report` (which
needs `windows`) does not run, so the nightly run does not refresh `TEST-REPORT.md`.
This is a temporary exception to the required checks above, not a change to them.

The repository owner suspends Windows CI by setting the repository Actions variable
`WINDOWS_CI` to `off` in the forge's repository Actions variables settings, and restores it by
deleting the variable. Do not restore it by setting some other value.

To confirm the restore, push to `main` after deleting the variable. The push must be eligible:
not a push that only changes `TEST-REPORT.md` (`aislop.yml` ignores it) and not a push by
`test-report-bot` (`publish-report` skips it). On that fresh `main` push, check that
`test / windows` and `aislop / quality-gate` execute their steps rather than showing as
skipped, and that `publish-report` then runs after `windows` and `linux` and either commits a
fresh `TEST-REPORT.md` or logs `Report is unchanged.`

The nightly run is not a receipt for both Windows checks. Only `test.yml` has a `schedule`;
`aislop.yml` runs on `pull_request`, `push` and `workflow_dispatch`, with no schedule. The
nightly confirms the Windows tests and report publication only, never the aislop gate.

## Local CI preflight

Run from Git Bash on Windows or Bash on Linux:

```bash
bash tools/ci-preflight.sh --changed-from gitea/main
bash tools/ci-preflight.sh --all
bash tools/ci-preflight.sh --only coverage-status
```

Scopes: `docs`, `scripts`, `preflight-tests`, `build-script`, `coverage-status`, `generate-test-report`, `release-offline`, `windows`,
`linux`, `linux-package`, `sync`, `aislop`, and `init`. Repeat `--only` to select several.
Unknown arguments, unknown scopes, missing values and mixed modes exit 2.
Every selected gate runs in the foreground, even after another gate fails.
Any gate failure exits 1; successful host-reproducible gates exit 0. `SKIP`
never means that the unavailable gate passed. The final verdict explicitly
covers only the selected host gates, not the complete remote CI verdict.

`--changed-from` keeps its local input adapter,
`preflight_changed_paths` in `tools/ci-preflight.sh`: it compares the supplied
base tip directly with the working tree, including staged and unstaged
changes, then enumerates untracked, unignored files. It makes no fetch;
refresh a stale local reference before running. Only after both Git commands
succeed does `preflight_main` source `scripts/changed-files.sh` and pass their
combined output to `changed_files_skip_build` and `changed_files_path_scopes`. The shared rule
above decides whether build, coverage, and package gates may be skipped, and
the [path scopes](#path-scopes) skip each tooling self-test gate
(`preflight-tests`, `build-script`, `aislop-tests`, `coverage-child`,
`coverage-status`, `generate-test-report`, `release-offline`, `init-windows`,
`init-linux`, `sync`, `linux-package-tests`) whose inputs did not change,
reported as `SKIP <gate>: changed paths exercise none of its inputs`. Empty
input, inspection failure (including a failed untracked-file enumeration with
partial output), and failure to load or run the classifier run all selected gates. `--all` and
`--only` do not load the classifier. Docs, script syntax gates, and aislop remain
unfiltered, subject to their existing host availability.

| Scope/gate | Windows | Linux | Shared CI implementation |
| --- | --- | --- | --- |
| `docs` | Run | Run | `scripts/test-agent-instructions.sh`: restore, managed build, AgentInstructionsTests (budgets, links, server context, maintained tools and portability) |
| `scripts` / `script-syntax`, `script-syntax-tests` | Run | Skip | `scripts/test-script-syntax.ps1` and `scripts/script-syntax.tests.ps1`, unfiltered syntax gate and offline regressions |
| `preflight-tests` | Run | Run | `scripts/ci-preflight.tests.sh`: dispatcher, shared classifier, CI adapter, and workflow delegation; also run by both Windows and Linux CI jobs |
| `build-script` / `build-script` | Run | Skip | `scripts/build-windows.tests.ps1` |
| `build-script` / `aislop-tests` | Run | Skip | `scripts/ci-aislop.tests.ps1`, independent scanner regression gate |
| `build-script` / `coverage-child` | Run | Skip | `scripts/test-native-coverage-elevated.ps1`, shared child protocol and both coverage callers |
| `release-offline` | Run (pwsh) | Run (requires pwsh) | `scripts/test-release-offline.sh`, offline package-validation and release-note regressions |
| `coverage-status` | Run (pwsh) | Run (requires pwsh) | `scripts/test-coverage-status.ps1`, offline publisher regression tests |
| `generate-test-report` | Run (pwsh) | Run (requires pwsh) | `scripts/generate-test-report.ps1` with no arguments, offline summary-generator regressions |
| `init` / `init-windows`, `init-linux` | Windows adapter matrix | Linux adapter matrix | `scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj --adapters`; also selected by the matching platform scope |
| `init` / `init-pester` | Run | Skip | `scripts/test-init-pester.ps1`, in-process Pester 6 tests of `init.ps1` branch logic; also selected by the Windows scope |
| `windows` | Run | Skip | `scripts/run-coverage.ps1 -NonInteractive`, including native and managed build, followed by `scripts/smoke-package-windows.ps1` |
| `linux` | Skip | Run | `scripts/coverage-linux.sh`, native and managed coverage |
| `linux-package` | Skip | Run | `check-linux-native.tests.sh`, `build-linux.sh` (builds, then runs the managed `TestCategory=Smoke` tier), `check-linux-native.sh`, `smoke-package-linux.sh` under `scripts/` |
| `sync` | Run | Run | `scripts/sync_consumers.tests.sh`, offline transport fixtures |
| `aislop` | Run | Skip | Windows build plus `scripts/ci-aislop.ps1`, shared with the quality workflow |

The `generate-test-report` scope runs the same no-argument offline regression as
Windows CI. Select it with `bash tools/ci-preflight.sh --only generate-test-report`;
`--all` includes it, and `--changed-from` skips it for docs-only/pin-only changes
and for changes that touch none of its path-scope inputs.
It requires PowerShell 7 but no publication credentials. Fixture output stays
under `.lane/fixtures`; the root `TEST-REPORT.md` is not generated. A failing
regression fails preflight while the remaining selected gates still run.

Install the same prerequisites as the selected CI jobs beforehand: .NET 10,
Windows MSVC/amd64 MSBuild, reportgenerator, pwsh, Git/Bash, and the C# scanner
tools; Linux coverage/package scopes need CMake, Ninja, g++, gcovr and Python.
The aislop scope also requires Node and the built pinned fork checkout, with no
global aislop launcher required.
Missing tools on a supported platform fail their gate rather than silently skip.
NuGet restore may need a populated offline cache. Preflight does not install tools.

Local preflight and the Windows quality workflow both execute
`scripts/ci-aislop.ps1`. It consumes an already-built fork checkout at
`AISLOP_HOME`, defaulting locally to `~/aislop`. It validates the lowercase
40-character pin in `.aislop/fork-commit`, the checkout's exact HEAD, clean
tracked source, and the presence of `dist/cli.js`. It runs that CLI's
`--version` command and prints its version, checkout commit, and required pin
before executing `ci . --json`. Provision and build the pinned checkout
before running locally; the shared gate does not acquire or install tools.

The default report is `.aislop/preflight/report.json` under the repository.
Use `-ReportPath` for another destination; relative paths are repository-relative.
Scanner stdout is preserved there even when JSON validation or the gate fails.
Empty, malformed, or null JSON and fewer than ten scanned files fail the gate.
A nonzero scanner exit remains a failure. When it leaves the score at or above `failBelow`, the gate prints the report's missing tools, engine failures and failed engines in one `aislop gate: exited nonzero` line, also appended to the evidence log. While waiting, the gate observes the
process every five seconds and emits a heartbeat every sixty seconds so a
quiet scan does not lose its CI log lease. It waits for redirected output to
finish before reading the report. On scanner failure it groups score-affecting
findings, reports the omitted report-only count, and explicitly says when no
score-affecting findings were reported. Full diagnostics remain in the JSON.

Run the gate regression checks with
`pwsh -NoProfile -File scripts/ci-aislop.tests.ps1` and the preflight caller
checks with `bash scripts/ci-preflight.tests.sh`. The gate tests include real
Git/Node fixtures and deterministic heartbeat observations.

The runtime also lists checks that cannot be reproduced offline on either host:
coverage status publication (Gitea token and historical main-line statuses),
consumer pin publication (workflow writing other repositories), and
Actions provisioning, cache transfers and artifact uploads. Windows post-clean
init verification is main-push-only and destructively cleans its CI checkout;
local PR preflight reports it skipped. `RequiresAdmin` tests remain excluded
by the same noninteractive coverage script CI uses. Linux-only gates require
a Linux run; Windows coverage and aislop require a Windows run.

The Linux initialization check is separate from the preflight `linux`
scope, which runs coverage. Reproduce it on Linux from a checkout without
build artifacts using:

```bash
set -euo pipefail
test ! -e MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll
./init.sh --build
test -f MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName~AgentInstructionsTests'
bash scripts/test-agent-instructions.sh
```

The command is not a pr-crew preflight-protocol opt-in, and the crew does not invoke it.

## Quality gate: aislop

This project uses **aislop** as a deterministic quality gate for AI-written code
(narrative comments, swallowed exceptions, `as any`, dead stubs, oversized
functions, etc.) across TS/JS, Python, Go, Rust, Ruby, PHP, Java, and C#.

Local development uses an installed C#-capable `aislop`, pinned to a **specific commit** of
the fork `mtschoen/aislop` (which adds the C# engine: roslynator + jb
inspectcode; upstream npm `aislop` is Python-only). Note that the local global
installation pin and the CI commit pin (`.aislop/fork-commit`) are separate
things to keep in rough sync. Call the installed binary directly - do NOT use
`npx aislop`, which pulls upstream from npm with no C# support:

- **Before declaring work complete**, run `aislop scan .` and address findings.
- **Before committing**, run `aislop scan --staged` (staged files only).
- `aislop fix` auto-clears mechanical issues (formatting, unused imports, dead
  code); `aislop fix --claude` hands the rest back with full context.
- `aislop ci .` is the gate - exits non-zero if the score drops below the
  threshold (`failBelow: 100`) in `.aislop/config.yml`. Treat a failing gate
  like a failing test.

### Independent InspectCode release verification

Before MFTLib 0.3.0 and subsequent releases ship, the whole solution must be verified clean at WARNING severity and above across all projects using JetBrains InspectCode directly:

```powershell
.\scripts\build-windows.ps1
jb inspectcode MFTLib.sln --severity=WARNING --no-updates --no-build '--properties=Configuration=Release;Platform=x64' --output=artifacts/inspectcode-report.sarif
```

- **Pre-build with `build-windows.ps1`**: `MFTLib.sln` contains the native C++ project `MFTLibNative.vcxproj` alongside managed projects. A bare `dotnet build` cannot compile `.vcxproj`, while InspectCode's internal build attempts to invoke MSBuild without the trailing-backslash `SolutionDir` and platform conventions needed by mixed solutions on Windows. Always pre-build with `scripts/build-windows.ps1` and pass `--no-build`.
- **Properties parameter**: In PowerShell, single-quote the `--properties` argument (`'--properties=Configuration=Release;Platform=x64'`) so semicolons are not parsed as command separators.
- **Severity threshold**: `--severity=WARNING` reports all WARNING and ERROR findings across both managed C# and native C++ code. The release floor is 0 findings across the solution.
- **Suppressions**: Any retained suppression directive must use the tool-specific mechanism (`// ReSharper disable once <rule>` or `// ReSharper disable <rule>`) accompanied by a clear, written reason (`-- <reason>`).

### CI gate (Windows)

`.gitea/workflows/aislop.yml` runs the gate on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch.
It runs on **windows-latest**, not Linux like the rest of the fleet: `MFTLib.sln`
includes the native `MFTLibNative.vcxproj`, which only loads/builds under
MSBuild + MSVC, and both jb inspectcode and roslynator load the full solution.
`lint.csharp.jbProjects` in `.aislop/config.yml` scopes the C# jb inspection to the six
C# projects, and `lint.cpp.jbProjects` scopes the native ReSharper C++ inspection to
`MFTLibNative`, which the same gate also scores with clang-tidy, cppcheck and clang-format.

`lint.jbTimeout` in `.aislop/config.yml` (aislop key, positive integer milliseconds, default 600000) is the
time limit of the shared C#/C++ jb inspectcode pass for `MFTLib.sln`. It is 1200000 (20 minutes) because CI
exceeded the 10-minute default (`Command timed out after 600000ms: jb`) at score 100 with no finding.
Runner contention is a suspected cause, not a measured diagnosis. The workflow's `timeout-minutes: 30`
allows one 20-minute pass plus 10 minutes for build and other work. The key needs the fork commit pinned
in `.aislop/fork-commit` to include it.

The workflow prepares the `schoen/aislop` fork at the commit in
`.aislop/fork-commit`, using a pin-keyed cache at the workspace-relative path `.aislop-fork`
(the cache version hash covers the raw path string, so it must be the same in
every job; actions/cache rejects `..` segments; and the act workspace is per-job,
so no job shares a checkout with another job, which matters because concurrency
is grouped per ref only). The directory is gitignored, which aislop honors, so
the scan never reads the fork's source. The workflow runs scripts/ci-aislop-link-repair.ps1 to repair restored
dependency links on cache hits (absolute links are retargeted; a relative directory link that tar restored as a file symlink is recreated as a directory symlink) and clones/builds with pnpm on misses.
The repair step is `continue-on-error`: when it fails, the cold build step runs as the fallback, and the evidence summary records the failed repair outcome, the cold build outcome and a `fallback:` line.
After preparation it resolves that checkout into `AISLOP_HOME` and
invokes `scripts/ci-aislop.ps1 -ReportPath` with
`$env:RUNNER_TEMP/aislop-report.json`. The shared script owns scanner execution,
heartbeats, report validation, diagnostic display, and the gate result.
Every run, including a failed one, uploads the `aislop-gate-evidence` artifact
(`if: always()`, `ChristopherHX/gitea-upload-artifact@81f940d004763f986ba3582c007fd842dd5cb0d7`) because pr-crew
reviews only uploaded, head-bound artifacts listed by the Gitea artifacts API.
`scripts/ci-aislop-evidence.ps1` writes its
`summary.txt` (cache-hit, link repair step outcome and counts, cold build step outcome,
the checkout version/commit/CI pin provenance line, gate step outcome, score, file count)
next to a copy of the JSON report; `scripts/ci-aislop.tests.ps1` covers each field.
Workflow preparation and host provisioning remain separate responsibilities.
It deliberately does NOT use `actions/setup-node`
(its 7zr extraction dies with exit code 2 on the host-mode act_runner). The
build step calls `scripts/build-windows.ps1`, the same Windows recipe used by coverage and `init.ps1 -Build`. The shared script requires amd64 MSBuild; a 32-bit binary cannot resolve legacy SYSTEM-profile checkout paths. `aislop / quality-gate
(pull_request)` is one of the four required status checks in the branch protection
rule on `main`, alongside `test / windows (pull_request)`, `test / linux (pull_request)` and `test / linux-package (pull_request)`, so a failing gate
blocks the merge. The `test / docs (pull_request)` check runs on every pull request but is not a required check.

The global pin is a commit-ish, not a version. Do not assume a tag: the `v0.14.1`
tag and the commit currently installed both report version `0.14.1` but are
different commits, so installing the tag would change the code that runs. Ask the
binary what it is rather than trusting this file:

```powershell
aislop --version
pnpm ls -g --depth 0            # or read the spec in pnpm's global package.json
```

To move the global binary to a different commit or tag of the fork:
`pnpm add -g --allow-build=aislop "github:mtschoen/aislop#<commit-ish>"`
(CI pin updates are separate and tracked in `.aislop/fork-commit`.)

## Performance claims in pull requests

A pull request that claims or risks a performance change in MFT enumeration, record parsing, name matching, or index
writes or queries pastes `Benchmark.exe compare` output for each affected scenario into its body. Compare against the
reference, the one committed report in `Benchmark/baselines/` (a scenario it
lacks has no reference yet: say so), measured on a quiet machine (no other builds,
tests or agent lanes running) with the same inputs and iteration count as that report. Scenarios by area:
`mft-enumeration-*` for enumeration, parsing and name matching (from `Benchmark.exe mft-enumeration`);
`exact-search` and `substring-search` for index queries (from `Benchmark.exe index`); and
`enumeration-walk-sequential` and `enumeration-walk-parallel` for the directory walk. Paste the comparison lines and the
reproduction command, not the raw report. A machine that cannot be made quiet makes the comparison unavailable: say so
in the body instead of pasting noisy numbers. Timing is evidence for review, never a CI gate. See the
[benchmark contract](architecture.md) and the [release benchmark checklist](handoff-release-0.3.0.md#benchmark-release-checklist).

## Public surface test

`MFTLib.Tests/PublicSurfaceTests.cs` compares the public surface with the approved files in `MFTLib.Tests/PublicSurface/` and runs in both the Windows and Linux test jobs; see `docs/architecture.md`.

## Agent instruction budgets

### Tracked-content portability

`AgentInstructionsTests.TrackedFiles_ContainNoPrivateDeploymentReferences` enumerates
`git ls-files -z` and reads each tracked working-tree file, including hidden configuration.
It rejects user-directory literals, private deployment hosts and workstation names,
and private checkout pointers. Git failures, missing metadata and unreadable files fail
the gate. There are no file exemptions; the detector scans its own source and has
negative controls. Stage intended additions and removals before local verification.
The same class checks the scanner and consumer-sync server context.
`.plan` and `.maintenance.json` remain tracked; the latter preserves actioned and rejected maintenance findings.

### Character budgets

`MFTLib.Tests/AgentInstructionsTests.cs` checks that the root `AGENTS.md` stays
strictly below 15000 Unicode characters and that every blank-line-delimited
block stays strictly below 3000. `.gitattributes` pins `AGENTS.md` to LF, so the
limits are identical on every platform and `wc -m AGENTS.md` matches the test.
`AgentInstructionsTests.InstructionFile_HasNoCarriageReturn` fails a checkout
that contains any carriage return. The test project copies the source document to
`repository-docs/AGENTS.md` in its output on every build. Missing input fails.

Run `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AgentInstructionsTests`.
Run `wc -m AGENTS.md` in a UTF-8 locale for the independent whole-file count.
After editing the document, build before using `--no-build` so the tested copy
is current. The `docs` job, as well as the Windows and Linux coverage jobs, run these tests.
Keep operational rules in the root file; move narrative unchanged into its
linked reference pages instead of raising the limits.

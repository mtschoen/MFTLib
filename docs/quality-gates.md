# CI and quality gates

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` defines four jobs on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch (the merge train validates a batch there before landing it): `windows`, `linux`, `docs`, and `linux-package`. The coverage jobs (`windows` and `linux`) run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires the `test / windows`, `test / linux`, `test / linux-package` and `aislop / quality-gate` `(pull_request)` checks to pass before merge; the `test / docs (pull_request)` check runs on every pull request but is not a required check.

The `windows`, `linux`, and `linux-package` jobs begin with `id: changed-files`.
All three call `bash scripts/ci-changed-files.sh`; Windows launches it from
PowerShell using runner-provisioned Git Bash. The adapter writes
`skip_build=true|false` to `GITHUB_OUTPUT`. For a pull request it fetches the
base branch tip and compares `FETCH_HEAD` directly with `HEAD`, not their
merge base. For a push it fetches the nonzero `before` commit and compares
the event's `before` and `after` commits.

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
set. Empty input, unknown events, missing references, missing/zero push
endpoints, and failed fetch/diff commands run the jobs in full. Output from
a failed inspection is never classified, even if it contains allowed paths.
An unwritable Actions output file fails the detector step. With a valid
skip decision, compilation, test, and package steps are skipped and the
three jobs finish green with their status checks present. Workflow-level
`paths` and `paths-ignore` filters are avoided because they leave required
status checks pending permanently on Gitea (issue 36895).

The `docs` job always runs without an early exit. Running on the configured Linux runner image, it restores `MFTLib.Tests.csproj`, performs a managed-only build (`dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-restore`), and executes `AgentInstructionsTests` to verify character budgets, document link integrity, CI input wiring and tracked-content portability. Because `AgentInstructionsTests` requires no native code and `Directory.Build.targets` copies `MFTLibNative` only when present, the job remains fast and cheap while ensuring docs changes are validated.

The `linux` job verifies the initialization output before coverage whenever
`skip_build` is false. It runs `./init.sh --build`, checks for
`MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll`, runs
`AgentInstructionsTests` with Release/x64 and `--no-build`, then runs
`bash scripts/test-agent-instructions.sh`. Checking before the script's own
build ensures initialization produced the expected output.

The `windows` job also runs the post-clean init verification (`git clean -ffxd`, `init.ps1` twice, `init.bat`, `init.ps1 -Build`, and a clean-tree check after each) described in [checkout maintenance](checkout-maintenance.md). It runs only on the push to `main`: each of those steps is guarded by `github.event_name == 'push' && github.ref == 'refs/heads/main'` (plus the skip-build check), never by workflow-level `paths`, which leaves a required check pending on Gitea (issue 36895). The steps stay in the same job and checkout, after the build and tests, because the first `git clean -ffxd` plus `git status --porcelain` assertion detects a build or test that modified a tracked file. A pull request therefore never proves the clean-tree invariant; the merge to `main` does.

Both platform test jobs also run the package-free init/provisioning regression harness before coverage, guarded by the existing skip-build condition. These offline checks exercise the actual entry points with command doubles, including absent and failed optional provisioning, and run on pull requests. They are separate from the Windows main-push-only post-clean verification and the Linux real-build initialization check.

The Windows job runs `bash scripts/test-release-offline.sh`, which runs
`scripts/test-release-package-validation.ps1` and `scripts/test-release-notes.ps1`,
before coverage when `skip_build` is false. `ci-preflight.sh --all` runs the same bundle as `release-offline`.
These offline checks reject incomplete release packages and verify that a
failed GitHub tag push stops before GitHub release creation; they also retain
the release-note extraction and UTF-8 encoding checks.

After coverage has built Release/x64, the Windows job runs
`scripts/smoke-package-windows.ps1`. It verifies Windows package-native resolution in
ordinary build and RID-less publish outputs using an isolated PackageReference consumer
and an empty package cache. The step follows the existing `skip_build` condition.
The smoke also validates the selected package's Windows imports and plain-x64
instruction baseline, then runs the real AVX2 negative-control regression.

The `linux-package` job builds the Release linux-x64 library, runs `scripts/check-linux-native.sh` on it and smoke-tests the packed library through `scripts/smoke-package-linux.sh`; the release rebuilds the library in WSL, since Gitea lists no CI artifact over REST ([Linux package](linux-package.md)).

Windows jobs run on the host-mode `windows-latest` runner, where toolchains are provisioned on the host: no `actions/setup-*` step belongs in a job that runs on it (`microsoft/setup-msbuild` only locates MSBuild and is allowed). The `linux`, `docs` and `linux-package` jobs read their container image from the repository Actions variable `MFTLIB_CI_IMAGE`. The repository operator provisions the image reference before enabling these jobs. The image supplies CMake, Ninja, gcovr, .NET SDK 10, g++, gcov and make; workflows do not install those tools. Each container retains `--ulimit nofile=65536:65536`.

Keep `pwsh` on the runner account's PATH and configure `DOTNET_INSTALL_DIR` at the runner service, never through workflow bootstrap steps. Use amd64 MSBuild and the shared Windows build script. Keep coverlet's comma-separated include filter in the project file rather than passing it through PowerShell. Compute file hashes in the shell because `hashFiles()` can evaluate empty on this runner. Every workflow upload uses the pinned `ChristopherHX/gitea-upload-artifact` action, because `actions/upload-artifact@v3` uploads are not listed by this Gitea deployment's artifacts REST API, which pr-crew reviews read.

## Local CI preflight

Run from Git Bash on Windows or Bash on Linux:

```bash
bash tools/ci-preflight.sh --changed-from gitea/main
bash tools/ci-preflight.sh --all
bash tools/ci-preflight.sh --only coverage-status
```

Scopes: `docs`, `preflight-tests`, `build-script`, `coverage-status`, `release-offline`, `windows`,
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
succeed does `preflight_main` call `bash scripts/changed-files.sh` with their
combined output. The shared rule above decides whether build, coverage, and
package gates may be skipped. Empty input, inspection failure (including a
failed untracked-file enumeration with partial output), and classifier
failure run all selected gates. Docs, the preflight regression suite, and
aislop remain unfiltered, subject to their existing host availability.

| Scope/gate | Windows | Linux | Shared CI implementation |
| --- | --- | --- | --- |
| `docs` | Run | Run | `scripts/test-agent-instructions.sh`: restore, managed build, AgentInstructionsTests (budgets, links, CI inputs and portability) |
| `preflight-tests` | Run | Run | `scripts/ci-preflight.tests.sh`: dispatcher, shared classifier, CI adapter, and workflow delegation; also run by both Windows and Linux CI jobs |
| `build-script` / `build-script` | Run | Skip | `scripts/build-windows.tests.ps1` |
| `build-script` / `aislop-tests` | Run | Skip | `scripts/ci-aislop.tests.ps1`, independent scanner regression gate |
| `build-script` / `coverage-child` | Run | Skip | `scripts/test-native-coverage-elevated.ps1`, shared child protocol and both coverage callers |
| `release-offline` | Run (pwsh) | Run (requires pwsh) | `scripts/test-release-offline.sh`, offline package-validation and release-note regressions |
| `coverage-status` | Run (pwsh) | Run (requires pwsh) | `scripts/test-coverage-status.ps1`, offline publisher regression tests |
| `init` / `init-windows`, `init-linux` | Windows adapter matrix | Linux adapter matrix | `scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj --adapters`; also selected by the matching platform scope |
| `windows` | Run | Skip | `scripts/run-coverage.ps1 -NonInteractive`, including native and managed build, followed by `scripts/smoke-package-windows.ps1` |
| `linux` | Skip | Run | `scripts/coverage-linux.sh`, native and managed coverage |
| `linux-package` | Skip | Run | `build-linux.sh` (builds, then runs the managed `TestCategory=Smoke` tier), `check-linux-native.sh`, `smoke-package-linux.sh` under `scripts/` |
| `sync` | Run | Run | `scripts/sync_consumers.tests.sh`, offline transport fixtures |
| `aislop` | Run | Skip | Windows build plus `scripts/ci-aislop.ps1`, shared with the quality workflow |

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
A nonzero scanner exit remains a failure. While waiting, the gate observes the
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
consumer pin publication (manual workflow writing other repositories), and
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

### CI gate (Windows)

`.gitea/workflows/aislop.yml` runs the gate on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch.
It runs on **windows-latest**, not Linux like the rest of the fleet: `MFTLib.sln`
includes the native `MFTLibNative.vcxproj`, which only loads/builds under
MSBuild + MSVC, and both jb inspectcode and roslynator load the full solution.
`lint.csharp.jbProjects` in `.aislop/config.yml` scopes the C# jb inspection to the six
C# projects, and `lint.cpp.jbProjects` scopes the native ReSharper C++ inspection to
`MFTLibNative`, which the same gate also scores with clang-tidy, cppcheck and clang-format.

The workflow prepares the `schoen/aislop` fork at the commit in
`.aislop/fork-commit`, using a pin-keyed cache at the workspace-relative path `.aislop-fork`
(the cache version hash covers the raw path string, so it must be the same in
every job; actions/cache rejects `..` segments; and the act workspace is per-job,
so no job shares a checkout with another job, which matters because concurrency
is grouped per ref only). The directory is gitignored, which aislop honors, so
the scan never reads the fork's source. The workflow repairs restored
dependency links on cache hits and clones/builds with pnpm on misses.
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
The same class pins all three Linux container images to `MFTLIB_CI_IMAGE` and
checks the scanner and consumer-sync server context. `.plan` remains tracked;
`.maintenance.json` is ignored local state.

### Character budgets

`MFTLib.Tests/AgentInstructionsTests.cs` checks that the root `AGENTS.md` stays
strictly below 15000 Unicode characters and that every blank-line-delimited
block stays strictly below 3000. Both limits include the file's actual line
endings. The test project copies the source document to
`repository-docs/AGENTS.md` in its output on every build. Missing input fails.

Run `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AgentInstructionsTests`.
Run `wc -m AGENTS.md` in a UTF-8 locale for the independent whole-file count.
After editing the document, build before using `--no-build` so the tested copy
is current. The `docs` job, as well as the Windows and Linux coverage jobs, run these tests.
Keep operational rules in the root file; move narrative unchanged into its
linked reference pages instead of raising the limits.

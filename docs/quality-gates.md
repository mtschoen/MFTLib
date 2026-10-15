# CI and quality gates

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` defines four jobs on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch (the merge train validates a batch there before landing it): `windows`, `linux`, `docs`, and `linux-package`. The coverage jobs (`windows` and `linux`) run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires the `test / windows`, `test / linux`, `test / linux-package` and `aislop / quality-gate` `(pull_request)` checks to pass before merge; the `test / docs (pull_request)` check runs on every pull request but is not a required check.

The `windows`, `linux`, and `linux-package` jobs each begin with a changed-files detection step (`skip_build`). When every changed path is `.aislop/fork-commit`, under `docs/`, or a top-level markdown file (`*.md` at the repository root, including `AGENTS.md`), these three jobs skip their compilation, test, and packaging steps and finish green. Any detection error or unhandled diff fails open and runs the jobs in full. Workflow-level `paths` or `paths-ignore` filters are avoided because they leave required status checks pending permanently on Gitea (issue 36895).

The `docs` job always runs without an early exit. Running on the Linux fleet runner image, it restores `MFTLib.Tests.csproj`, performs a managed-only build (`dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-restore`), and executes `AgentInstructionsTests` to verify character budgets and document link integrity. Because `AgentInstructionsTests` requires no native code and `Directory.Build.targets` copies `MFTLibNative` only when present, the job remains fast and cheap while ensuring docs changes are validated.

The `linux` job verifies the initialization output before coverage whenever
`skip_build` is false. It runs `./init.sh --build`, checks for
`MFTLib.Tests/bin/x64/Release/net10.0/MFTLib.Tests.dll`, runs
`AgentInstructionsTests` with Release/x64 and `--no-build`, then runs
`bash scripts/test-agent-instructions.sh`. Checking before the script's own
build ensures initialization produced the expected output.

The `windows` job also runs the post-clean init verification (`git clean -ffxd`, `init.ps1` twice, `init.bat`, `init.ps1 -Build`, and a clean-tree check after each) described in [checkout maintenance](checkout-maintenance.md). It runs only on the push to `main`: each of those steps is guarded by `github.event_name == 'push' && github.ref == 'refs/heads/main'` (plus the skip-build check), never by workflow-level `paths`, which leaves a required check pending on Gitea (issue 36895). The steps stay in the same job and checkout, after the build and tests, because the first `git clean -ffxd` plus `git status --porcelain` assertion detects a build or test that modified a tracked file. A pull request therefore never proves the clean-tree invariant; the merge to `main` does.

The Windows job runs `scripts/test-release-package-validation.ps1` and
`scripts/test-release-notes.ps1` before coverage when `skip_build` is false.
These offline checks reject incomplete release packages and verify that a
failed GitHub tag push stops before GitHub release creation; they also retain
the release-note extraction and UTF-8 encoding checks.

The `linux-package` job builds the Release linux-x64 library, runs `scripts/check-linux-native.sh` on it and smoke-tests the packed library through `scripts/smoke-package-linux.sh`; the release rebuilds the library in WSL, since Gitea lists no CI artifact over REST ([Linux package](linux-package.md)).

Windows jobs run on the host-mode `windows-latest` runner, where toolchains are provisioned on the host: no `actions/setup-*` step belongs in a job that runs on it (`microsoft/setup-msbuild` only locates MSBuild and is allowed). The Linux jobs run in the fleet runner image `gitea.fleet.sticktoitive.net/schoen/runner-images:latest`, which bakes cmake, ninja, gcovr and the .NET SDK 10.0 and inherits g++, gcov and make from its base: they install no tools and use no `actions/setup-dotnet` step.

For Gitea-specific gotchas (act_runner host-mode quirks, VS BuildTools quirks, .NET version mismatch, PS7 + dotnet test comma-splitting, etc.), read `~/schoen-lab/packages/local_ci/docs/project-ci-setup.md` before modifying the workflow. Runner-account environment needs (pwsh on PATH, `DOTNET_INSTALL_DIR`) are fixed at the runner service level - do not add per-workflow bootstrap steps for them.

## Local CI preflight

Run from Git Bash on Windows or Bash on Linux:

```bash
bash tools/ci-preflight.sh --changed-from gitea/main
bash tools/ci-preflight.sh --all
bash tools/ci-preflight.sh --only coverage-status
```

Scopes: `docs`, `preflight-tests`, `build-script`, `coverage-status`, `windows`,
`linux`, `linux-package`, `sync`, and `aislop`. Repeat `--only` to select several.
Unknown arguments, unknown scopes, missing values and mixed modes exit 2.
Every selected gate runs in the foreground, even after another gate fails.
Any gate failure exits 1; successful host-reproducible gates exit 0. `SKIP`
never means that the unavailable gate passed. The final verdict explicitly
covers only the selected host gates, not the complete remote CI verdict.

`--changed-from` uses the workflows' direct-base-tip diff and exact path rule:
a nonempty diff containing only `.aislop/fork-commit`, `docs/`, or root `*.md`
skips the build, coverage and package jobs. It includes working-tree changes
and untracked unignored files because preflight tests the working tree.
Empty diffs and inspection failures run all gates. No network fetch is made;
refresh the reference before running if its local tip is stale. Docs and
aislop always run, as in CI.

| Scope/gate | Windows | Linux | Shared CI implementation |
| --- | --- | --- | --- |
| `docs` | Run | Run | `scripts/test-agent-instructions.sh`: restore, managed build, AgentInstructionsTests |
| `preflight-tests` | Run | Run | `scripts/ci-preflight.tests.sh`, also run in the Linux CI job |
| `build-script` / `build-script` | Run | Skip | `scripts/build-windows.tests.ps1` |
| `build-script` / `aislop-tests` | Run | Skip | `scripts/ci-aislop.tests.ps1`, independent scanner regression gate |
| `coverage-status` | Run (pwsh) | Run (requires pwsh) | `scripts/test-coverage-status.ps1`, offline publisher regression tests |
| `windows` | Run | Skip | `scripts/run-coverage.ps1 -NonInteractive`, including native and managed build |
| `linux` | Skip | Run | `scripts/coverage-linux.sh`, native and managed coverage |
| `linux-package` | Skip | Run | `build-linux.sh`, `check-linux-native.sh`, `smoke-package-linux.sh` under `scripts/` |
| `sync` | Run | Run | `scripts/sync_consumers.tests.sh`, offline transport fixtures |
| `aislop` | Run | Skip | Windows build plus exact pinned scanner and CI file-count sanity check |

Install the same prerequisites as the selected CI jobs beforehand: .NET 10,
Windows MSVC/amd64 MSBuild, reportgenerator, pwsh, Git/Bash, and the C# scanner
tools; Linux coverage/package scopes need CMake, Ninja, g++, gcovr and Python.
The aislop scope also requires Node and the built pinned fork checkout, with no
global aislop launcher required.
Missing tools on a supported platform fail their gate rather than silently skip.
NuGet restore may need a populated offline cache. Preflight does not install tools.

The aislop gate uses the already-built fork checkout at `AISLOP_HOME` or
`~/aislop`. It verifies its clean tracked source and exact HEAD against
`.aislop/fork-commit`, the same pin read by `.gitea/workflows/aislop.yml`, then
runs `node <checkout>/dist/cli.js --version` followed by
`node <checkout>/dist/cli.js ci . --json`. It prints that checkout's version,
checkout commit and required CI commit. A different or unidentifiable commit
is refused, not merely warned: CI uses the pinned scanner, and a semantic
version alone cannot prove identity. Provision/build
that exact fork commit before running. The local JSON report is at
`.aislop/preflight/report.json`. Like CI, fewer than ten scanned files fails.

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

`aislop` is installed globally on this machine, pinned to a **specific commit** of
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
`MFTLibNative`, which the same gate also scores with clang-tidy, cppcheck and clang-format. The
workflow installs `aislop` by cloning the `schoen/aislop` fork from Gitea at the
commit pinned in `.aislop/fork-commit` (built with `pnpm`) and runs it via `node`.
It deliberately does NOT use `actions/setup-node`
(its 7zr extraction dies with exit code 2 on the host-mode act_runner). The
build step calls `scripts/build-windows.ps1`, the same Windows recipe used by coverage and `init.ps1 -Build`. The shared script requires amd64 MSBuild; a 32-bit binary cannot resolve legacy SYSTEM-profile checkout paths. See the traps in
`~/schoen-lab/packages/local_ci/docs/project-ci-setup.md`. `aislop / quality-gate
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

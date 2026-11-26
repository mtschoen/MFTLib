# Checkout maintenance

## Cleaning the working tree

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

`.maintenance.json` is tracked maintenance state: it records which findings were actioned and which were deliberately rejected, and nothing regenerates that history. The backlog in `.plan` also remains tracked.

Git offers no way to protect a file from `git clean`. Aliases cannot shadow built-in commands, there is no `pre-clean` hook, and `-x` overrides `.git/info/exclude`. Only tracked files and files outside the tree are safe, which is why the invariant above is the whole mechanism.

What a clean removes and how each comes back:

| Removed | Restored by |
| --- | --- |
| `bin/`, `obj/`, `x64/`, `build/`, `.vs/`, `node_modules/`, `TestResults/` | rebuild |
| Coverage reports and logs at the repository root | re-run the coverage scripts |
| `.claude/` and `.aislop/` scan output and caches | re-run aislop or inspectcode |
| `.claude/settings.local.json` | re-granted as needed; broad grants live in the user-scope settings, and any provisioned project-scope overrides are re-applied by the tool that wrote them |
| `.claude/AISLOP.md`, `.claude/CLAUDE.md` | `aislop hook install claude --project` |

`.claude/AISLOP.md` and `.claude/CLAUDE.md` are generated boilerplate the aislop installer writes into a sentinel-fenced block, which is why they are not tracked. They do not restore themselves - nothing rewrites them until that command is run, so run it after a clean. It is also how to refresh them after an aislop upgrade.

`.aislop/` run history (`history.jsonl`) and session logs are ephemeral runtime telemetry. The quality gate enforces an absolute `failBelow: 100` threshold on the current tree rather than relative historical deltas, so past run logs are not required to build or verify the repository, and fresh scan output and caches are regenerated on the next run.

### Getting back to work after a clean

`init.ps1` (Windows) and `init.sh` (Linux) at the repository root do the two things a clean does not undo by itself - the NuGet restore and the generated agent files - and report any prerequisite they cannot install for you:

```powershell
git clean -ffxd && .\init.ps1          # restore only, a few seconds
git clean -ffxd && .\init.ps1 -Build   # also build the solution Release|x64
```

From `cmd.exe`, use `init.bat`, which forwards to the same script:

```bat
git clean -ffxd && .\init.bat
git clean -ffxd && .\init.bat -Build
```

Keep the `.\` prefix. On systems with `NoDefaultCurrentDirectoryInExePath=1`, `cmd.exe` does not search the working directory for executables and a bare `init.bat` fails with "not recognized as an internal or external command".

```bash
git clean -ffxd && ./init.sh           # restore only
git clean -ffxd && ./init.sh --build   # also build native (cmake/ninja) + managed
```

The solution and project configuration lists contain only x64. Linux
`./init.sh --build` builds the native Release library with CMake/Ninja and the
managed projects with `-c Release -p:Platform=x64`. Managed outputs are under
each project's `bin/x64/Release/net10.0` directory. After initialization, run
`bash scripts/test-agent-instructions.sh` from the repository root to build
and run the instruction checks with the same configuration and platform.

Both are idempotent, so they are safe to run at any time, not only after a clean.

### Worktree-only mode (pr-crew review worktrees)

`.\init.ps1 -WorktreeOnly -Build` and `./init.sh --worktree-only --build` prepare a bare review worktree: NuGet restore and, with the build flag, the same native and managed Release|x64 build as normal mode, and nothing else. Worktree-only mode writes no git configuration, runs no `git` command, installs no agent instruction files (`aislop hook install`), and never restores or runs the optional-provisioning helper or a provisioner, so it touches nothing outside the worktree beyond package-manager caches. A missing prerequisite (the dotnet SDK; with build, also amd64 MSBuild with the MSVC workload on Windows, or cmake, ninja and g++ on Linux) exits non-zero with one line naming it, and nothing prompts. Without the build flag it restores only. `init.bat` forwards `-WorktreeOnly` like any other argument. Normal mode is unchanged.

The repository root `.pr-crew.toml` declares both commands under `[review.setup]`, so pr-crew runs them in each review worktree and reports whether the worktree was built. Both platforms are declared because both build the native library the tests load (MFTLibNative.dll with MSVC on Windows, libMFTLibNative.so with cmake and Ninja on Linux). The adapter regressions below assert the mode's no-git, no-provisioner contract.

Both init entry points delegate optional provisioning to `scripts/optional-provisioning/OptionalProvisioning.csproj`, a package-free utility built with the already-required .NET SDK 10. It selects `schoen-lab-onboard` before `schoen-lab` on PATH and invokes exactly `apply auto-memory`. Absence is a silent skip; a failed invocation is reported and initialization continues. Only an exit-zero invocation adds the completed-step message. The unqualified `onboard` executable is never considered.

The external provisioner owns settings, registry discovery, and memory writes. Its selected feature can affect global settings and registered checkouts; MFTLib does not recreate that logic or limit the feature to the current checkout. The utility's local build configuration has no package sources or package dependencies and does not import the library's native-copy targets. This step needs neither PowerShell on Linux nor Python or Bash on Windows.

Windows `init.ps1 -Build` calls `scripts/build-windows.ps1 -Configuration Release -NoRestore` after its successful NuGet restore. The shared build serves managed coverage, native coverage and the aislop workflow: it builds the native x64 DLL with v143 and an absolute trailing-backslash `SolutionDir`, then builds the six managed projects. `scripts/windows-tools.ps1` owns checked Visual Studio discovery for build, init, native coverage and release, selecting amd64 MSBuild through `vswhere` with BuildTools included. Init collects discovery errors in its missing-prerequisite summary and continues checking the remaining prerequisites. Run `./scripts/build-windows.ps1` for a standalone Release restore/build, or pass `-Configuration Debug`. Use `-NoRestore` only after a successful restore. Release checks Clean before coverage; coverage's shared build owns its restore. Linux continues to use cmake/Ninja through `scripts/build-linux.sh` and restores managed projects individually because dotnet cannot load the native `.vcxproj`.

Native output distribution belongs to the managed build. On Windows,
`Directory.Build.targets` copies `MFTLibNative.dll` and its available PDB from
root `x64/<Configuration>` to each managed project's output after its build;
build native before managed when using dotnet. Visual Studio MSBuild orders
native builds through the managed projects' `ProjectReference` entries. On
non-Windows hosts, `MFTLibNative.Linux.props`, explicitly imported by MFTLib and
MFTLib.Tests, copies one library with `Always`, so a flavor switch overwrites the output library whatever the file timestamps, chosen by `MFTLibNativeLinuxFlavor`: `Release` is
`build/linux/libMFTLibNative.so` (`scripts/build-linux.sh` passes it) and `Coverage` is
`build/linux-coverage/libMFTLibNative.so` (`scripts/coverage-linux.sh` passes it). The unselected library is
never included. An unset flavor, as in a plain `dotnet build`, means Coverage when that library exists,
otherwise Release.
Referencing managed projects receive that item through the SDK. With neither
source present, managed-only builds remain available. Package runtime assets
follow the separate [Linux package contract](linux-package.md).

CI verification: the `windows` job in `.gitea/workflows/test.yml` runs `scripts/ci-post-clean-destructive.ps1` (the clean, both init entry points, the idempotency rerun and `init.ps1 -Build` with a clean-tree assertion after each), but only on the push to `main` (see [quality gates](quality-gates.md)). A pull request does not run it, so run the disposable acceptance check below when a change touches init, build or ignore files.

The Linux CI job also runs `./init.sh --build` before coverage. It starts
without the Release/x64 test assembly, verifies init creates that assembly,
and runs `AgentInstructionsTests` with `--no-build` before invoking the
shared agent-instructions script.

### Init regression and acceptance checks

The Windows and Linux CI jobs run offline init/provisioning regressions on pull
requests and pushes when the build job is selected. The harness uses an owned
temporary checkout layout and executable command doubles; it never invokes the
installed provisioner. Run the same host gate with:

    dotnet run --project scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj -c Release -p:Platform=x64 -- --adapters .

The harness builds `OptionalProvisioning.csproj` once per process into a shared temporary directory, and the
fixtures' fake `dotnet run` executes that build with the arguments after `--`, still logging the `dotnet run` argv.
Each launch starts a fresh shell, so launches are spent only where an entry point's own behavior is the subject.
The helper's policy runs in process: `TestProgram.Policy` asserts every installed-provisioner by exit-code cell
of `ProvisioningDispatcher`, and `LauncherTests` asserts `CommandLauncher` discovery over a temporary `PATH`
(preferred, fallback, absent, the unqualified `onboard` never run, a non-executable file skipped on Linux, `.cmd`
and `.bat` through `PATHEXT`) and, on Windows, both batch start-info shapes in process plus one real `cmd.exe` shim.
That shim combines spaces, a literal `%NAME%` with a hostile value, `!`, `^` and `&` in one path and checks the
complete argument vector and exit code. The entry point (`init.ps1` on Windows, `init.sh` on Linux) is launched for:
the real `dotnet run` from the fixture copy (installed provisioners, exit 0; this verifies the copied helper file list,
the delegation and the completion summary), no provisioner installed, a failing provisioner with `-Build`/`--build`
forwarding, and on Linux also launcher failure, required restore failure, required build failure and worktree-only
mode (without and with build, restore failure, and a missing dotnet); on Windows those four are tested in process
(below). Windows adds the `PSNativeCommandUseErrorActionPreference`
case (restore stdout reaches the caller before `Done.` and survives a terminating restore error)
and three `init.bat` launches. With only `-WorktreeOnly`, the Windows PowerShell fallback restores the solution
without a managed or native build. With `-Build -WorktreeOnly`, the preferred `pwsh` double returns exit code 37
through the wrapper. Those two check the exact arguments received by the selected shell. The third runs a full init under
Windows PowerShell with no `pwsh` and a failing optional provisioner, and requires exit 0 with the diagnostics.

`init.ps1` is functions plus a thin entry point that runs only when the script is executed, never when it is
dot-sourced. `scripts/init.tests.ps1` dot-sources it and tests the branch logic in one PowerShell process with
Pester 6 mocks for `dotnet`, `aislop`, `git` and the helper functions that reach the machine, plus a temporary
`build-windows.ps1` that can throw or return a nonzero exit code. The worktree-only success and restore-failure cases invoke a
temporary copy of `init.ps1` in that same process to check switch binding, the commands dispatched and the caller-visible exit code. The tests cover
every `exit` path (`Invoke-Init` sets the status by reference and the entry point exits once), `-WorktreeOnly` with and without
`-Build`, restore, hook-install and build failure, the `settings.json` line-ending normalization, and each
optional-provisioning status. Run it with `pwsh -NoProfile -File scripts/test-init-pester.ps1`. The real launches
above remain only where an entry point's own behavior is the subject: the real `dotnet run`, `init.bat`
argument transport and exit codes, the `PSNativeCommandUseErrorActionPreference` case and self-location.

Local CI preflight includes the harness gate under `--only init` and under the matching
`windows` or `linux` scope, and the Pester gate (`init-pester`, Windows only) under the same Windows selections.
The other platform is reported as unavailable.

For real-build acceptance, commit the implementation locally, provision the
documented dependencies beforehand, and run:

    dotnet run --project scripts/optional-provisioning/tests/OptionalProvisioning.Tests.csproj -c Release -p:Platform=x64 -- --acceptance .

This creates and cleans its own disposable local clone, shadows both provisioner
names, and runs each platform entry point twice for restore and build, once with
a successful optional command and once with a failed one. Windows covers
`init.ps1`, `init.bat`, and their `-Build` forwarding; Linux covers `init.sh` and
`--build`. It checks clean tracked state and native/managed build outputs.
The existing Windows main-push-only post-clean verification remains a separate
check of the original CI checkout and is not replaced by these regressions.

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop, reportgenerator (managed coverage summaries and HTML reports), Pester 6 (the in-process init tests; a runner without it is fixed at the service, never by a workflow step). Linux: .NET SDK, cmake, ninja, g++, aislop, gcovr (native coverage only).

# Checkout maintenance

## Cleaning the working tree

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

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

Keep the `.\` prefix. This machine sets `NoDefaultCurrentDirectoryInExePath=1`, so `cmd.exe` does not search the working directory for executables and a bare `init.bat` fails with "not recognized as an internal or external command".

```bash
git clean -ffxd && ./init.sh           # restore only
git clean -ffxd && ./init.sh --build   # also build native (cmake/ninja) + managed
```

Both are idempotent, so they are safe to run at any time, not only after a clean.

They also make one optional call: if a settings-provisioning tool is on PATH, they ask it to re-apply the project-scope settings it owns, since a clean removes `.claude/settings.local.json`. The call names a single feature rather than running the tool's whole pipeline, and the step is skipped entirely when the tool is absent, so nothing here depends on it.

Windows `init.ps1 -Build` calls `scripts/build-windows.ps1 -Configuration Release -NoRestore` after its NuGet restore. The shared script also serves managed coverage and the aislop workflow: it resolves amd64 MSBuild through `vswhere`, builds the native x64 DLL with v143 and an absolute trailing-backslash `SolutionDir`, then builds the five managed projects. Run `.\scripts\build-windows.ps1` for a standalone Release build, or pass `-Configuration Debug`; standalone builds restore by default. Use `-NoRestore` only after a successful restore. Linux continues to use cmake/Ninja through `scripts/build-linux.sh` and restores managed projects individually because dotnet cannot load the native `.vcxproj`.

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop, reportgenerator (HTML coverage only). Linux: .NET SDK, cmake, ninja, g++, aislop, gcovr (native coverage only).

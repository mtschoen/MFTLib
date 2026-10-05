# CI and quality gates

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` runs `windows` + `linux` jobs on every PR and on push to `main` or to a `pr-crew/merge-train/**` branch (the merge train validates a batch there before landing it). Both run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires both `(pull_request)` checks to pass before merge.

For Gitea-specific gotchas (act_runner host-mode quirks, VS BuildTools quirks, .NET version mismatch, PS7 + dotnet test comma-splitting, etc.), read `~/schoen-lab/packages/local_ci/docs/project-ci-setup.md` before modifying the workflow. Runner-account environment needs (pwsh on PATH, `DOTNET_INSTALL_DIR`) are fixed at the runner service level - do not add per-workflow bootstrap steps for them.

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
`lint.csharp.jbProjects` in `.aislop/config.yml` scopes jb inspection to the five
C# projects so the C++ tree stays on its own clang-tidy/cppcheck gate. The
workflow installs `aislop` by cloning the `schoen/aislop` fork from Gitea at the
commit pinned in `.aislop/fork-commit` (built with `pnpm`) and runs it via `node`.
It deliberately does NOT use `actions/setup-node`
(its 7zr extraction dies with exit code 2 on the host-mode act_runner). The
build step calls `scripts/build-windows.ps1`, the same Windows recipe used by coverage and `init.ps1 -Build`. The shared script requires amd64 MSBuild; a 32-bit binary cannot resolve legacy SYSTEM-profile checkout paths. See the traps in
`~/schoen-lab/packages/local_ci/docs/project-ci-setup.md`. `aislop / quality-gate
(pull_request)` is one of the required status checks in the branch protection
rule on `main`, alongside `test / windows` and `test / linux`, so a failing gate
blocks the merge.

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
is current. The existing Windows and Linux coverage jobs run these tests.
Keep operational rules in the root file; move narrative unchanged into its
linked reference pages instead of raising the limits.

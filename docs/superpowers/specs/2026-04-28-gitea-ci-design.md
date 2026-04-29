# Gitea CI for MFTLib — Design

**Date:** 2026-04-28
**Status:** Approved (brainstorming session)
**Supersedes:** `docs/handoff-ci-windows-runner.md` (will be deleted once
the workflow lands)

## Goal

Wire MFTLib to Gitea Actions so every PR runs a Windows build+test+coverage
job and a Linux build+test+coverage job before merge to `main`. Both jobs
must pass for the merge gate. The work closes out the standalone Windows
runner spec in `~/local-ci`.

## Non-goals

- Tag-triggered release / benchmark workflow (separate scope; will be
  designed when 0.4 ships).
- Admin-required tests in CI. The Windows runner is a non-admin local user
  by design; admin tests (`TestCategory=RequiresAdmin`, ~3 classes) stay
  on the developer box for release validation.
- NuGet publish automation. Publish stays manual via
  `dotnet nuget push` per `CLAUDE.md`.

## Runners (already provisioned)

- **Windows:** label `windows-latest` (host-mode, no container). Pre-installed:
  .NET 10 SDK, VS Build Tools 2022 + C++ Desktop workload + Win 10 SDK
  19041, MAUI workload, PowerShell 7 LTS, Node.js LTS. Runs as non-admin
  local user `gitea-runner`. Topology: `~/local-ci/runner/README.md`.
- **Linux:** label
  `ubuntu-latest:docker://gitea/runner-images:ubuntu-latest`. Container
  job — image is GitHub-Actions-compatible (.NET 8 SDK expected; verify
  on first run).

## Architecture

One workflow file: `.gitea/workflows/test.yml`. Two jobs running in
parallel: `windows` and `linux`. Single workflow keeps trigger config
in one place; per-job status checks plug cleanly into branch protection.

**Triggers:**
- `pull_request` — any branch
- `push` to `main` only

Feature branches go through the `pull_request` event. The `push`-to-`main`
event keeps a green-build history on the protected branch.

## Job: `windows`

Runs on `windows-latest`. Shell defaults to `pwsh`.

Steps:
1. `actions/checkout@v4`
2. `pwsh scripts\run-coverage.ps1 -NonInteractive` — drives MSBuild
   `Release|x64`, then `dotnet test` with filter
   `TestCategory!=RequiresAdmin`, then emits cobertura + HTML coverage.
3. Upload artifacts (`if: always()`): coverage XML + HTML report so failed
   runs leave inspectable coverage.

**Pre-implementation check:** `scripts\run-coverage.ps1` must work on a
fresh checkout (no dependence on local pre-build state). If it doesn't,
fix the script — do not work around it inline in YAML. Local script and CI
must stay the single source of truth.

## Job: `linux`

Runs on `ubuntu-latest` (Docker container).

Steps:
1. `actions/checkout@v4`
2. Install build deps:
   `apt-get install -y cmake ninja-build g++ python3-pip` (defensive —
   cheap if already present).
3. `pip install --user gcovr`, prepend `~/.local/bin` to `PATH`.
4. `bash scripts/coverage-linux.sh` — drives the cmake + ninja + gcovr
   native coverage flow plus the managed `dotnet test` pass.
5. Upload artifacts (`if: always()`): the `coverage-report/` directory
   (native HTML + `summary.txt` + managed `coverage.cobertura.xml`).

**Pre-implementation check:** verify the runner image has the .NET 8 SDK.
If absent, add `actions/setup-dotnet@v4` with `dotnet-version: '8.x'` as
a step before the coverage script. A short probe job on the first push
will confirm.

## Branch protection

Apply only **after** both jobs are green on a real PR.

Required status checks on `main`:
- `test / windows (pull_request)`
- `test / linux (pull_request)`

**Pull-request variants only.** The `(push)` variants would sit forever
pending on PR feature branches because the `push` event is restricted to
`branches: [main]`. This is the documented `canary` gotcha.

API call (PATCH if a rule already exists):

```bash
TOKEN=$(cat ~/.gitea-token)
curl -sk -X POST -H "Authorization: token $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"rule_name":"main","enable_status_check":true,"status_check_contexts":["test / windows (pull_request)","test / linux (pull_request)"]}' \
  https://gitea.llamabox.internal/api/v1/repos/schoen/MFTLib/branch_protections
```

## Closing the loop

When MFTLib's CI is green and branch protection is applied:

1. `git rm` `~/local-ci/docs/specs/2026-04-26-windows-runner-design.md`.
2. Confirm `~/local-ci/runner/README.md` is the sole authoritative runner
   topology doc.
3. Update `~/local-ci/docs/specs/2026-04-25-future-coverage-roadmap.md`
   to mark the Windows-runner item complete.
4. Optionally retire
   `~/local-ci/docs/plans/2026-04-26-windows-runner-implementation.md`
   (mostly historical at this point).
5. `git rm docs/handoff-ci-windows-runner.md` from this repo — its
   purpose is fulfilled once the workflow exists.

## Risks and unknowns

- **`run-coverage.ps1` on fresh checkout:** unverified. Mitigation: probe
  early; fix the script if needed rather than inlining steps.
- **`gitea/runner-images:ubuntu-latest` toolchain contents:** unverified.
  Mitigation: install dotnet via `setup-dotnet@v4` if probe shows it's
  missing; defensive `apt-get` for cmake/ninja/g++.
- **`actions/checkout@v4` requires Node.js on the Windows runner.** Already
  installed; if the runner ever fails with `Cannot find: node in PATH`,
  `Restart-Service act_runner` (services inherit machine PATH at start;
  toolchain upgrades require a restart). Documented in the handoff.
- **Concurrency:** not specified initially. If feature-branch PR pushes
  pile up, add a `concurrency:` block to cancel in-progress runs. Defer
  until observed as a problem.

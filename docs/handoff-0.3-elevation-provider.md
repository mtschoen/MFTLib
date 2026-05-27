# Handoff — MFTLib 0.3: public `IElevationProvider` for consumer testability

**Date:** 2026-05-26. **Branch:** `feat/0.3-elevation-provider`.

## Why

git-wizard (and later file-wizard) need to test their *self-elevation decision logic* without triggering real UAC. MFTLib's internal Func seams (`IsWindows` / `GetProcessPathFunc` / `StartProcess` + `ResetToDefaults`) let MFTLib test its **own** `IsElevated`/`CanSelfElevate`/`TryRunElevated`, but they are `internal` and **cannot force `IsElevated()==true`** (it does a real Windows token check). So a *consumer* can't fake the elevation decision — especially the already-elevated branch. 0.3 has been deliberately held to nail down exactly this public surface.

## Task (keep MFTLib at 100% line/branch/method, 0 exclusions)

Add a public injectable elevation abstraction:

```csharp
namespace MFTLib;

public interface IElevationProvider
{
    bool IsElevated();
    bool CanSelfElevate();
    bool TryRunElevated(string arguments, int timeoutMs = 60000);
}
```

Plus a public default implementation backed by the existing statics, exposed as a singleton — `ElevationUtilities.DefaultProvider` (its 3 methods delegate to `IsElevated()`/`CanSelfElevate()`/`TryRunElevated()`). Keep the internal Func seams internal.

- Cover `DefaultProvider`'s delegation in the MSTest suite using the existing seams (`IsWindows = () => false` ⇒ `DefaultProvider.IsElevated()` is false; `StartProcess` fake ⇒ `TryRunElevated` outcomes) → preserves 100% / 0-exclusions.
- Bump `<Version>` to **0.3.0**. **Ship only AFTER git-wizard validates the surface** against a local `ProjectReference` — the consumer's tests are the acceptance gate (user's "consumer port-back is the gate" preference).
- file-wizard will consume the same `IElevationProvider` (see `~/file-wizard` branch `feat/elevation-provider-prep`).

## Consumer usage (git-wizard — for reference)

git-wizard adds an optional `IElevationProvider? elevation = null` to `GitWizardApi.TryFindAllRepositoriesUsingMft` and `WindowsDefenderException.AddExclusions` (`elevation ??= ElevationUtilities.DefaultProvider`), and injects a fake in tests that returns elevated true/false freely — covering its decision logic with no UAC. Full design: git-wizard `docs/superpowers/specs/2026-05-26-uac-free-tests-and-elevation-seam-design.md`.

## Notes

- MFTLib uses **MSTest**; git-wizard uses **NUnit** (`TestCategory` ↔ `Category`, both have `Assert.Inconclusive`).
- Cross-repo memory: `~/.claude/notes/idioms_mftlib_elevation_testing.md`, `~/.claude/notes/project_mftlib_wizard_family.md`.
- Family is mirror-remotes (gitea + github), no canonical; keep both `main`s in sync.

# LINTER-SETUP — MFTLib

Recommended linting setup for MFTLib — fleet survey 2026-05-29.

---

## Current state

| Aspect | Finding |
|---|---|
| Languages | **C#** — 29 source files across `MFTLib/`, `MFTLib.Tests/`, `TestProgram/`, `Benchmark/` (net8.0, `Nullable enable`, `AllowUnsafeBlocks`) |
| | **C++** — 11 `.cpp` + 7 `.h` in `MFTLibNative/`; CMake build (`CMakeLists.txt` present at root of `MFTLibNative/`) |
| `.editorconfig` | Present at repo root — whitespace + C# naming rules (field prefix, PascalCase, camelCase), no Roslyn diagnostic severity overrides |
| Roslyn analyzers | Not enabled in any `.csproj` — no `EnableNETAnalyzers`, no `AnalysisLevel`, no `Directory.Build.props`; no Roslynator NuGet reference |
| C++ linter config | No `.clang-format`, no `.clang-tidy` |
| CI | `.gitea/workflows/test.yml` — `dotnet test` + coverage on both Windows and Linux; **no lint step** |
| Claude Code hook | No `.claude/settings*.json` — no PostToolUse on-save hook |

---

## Three-tier model

The three tiers serve different speeds and scopes:

1. **On-save ①** — fires on every file write (Claude Code `PostToolUse` hook or IDE). Fast, per-file. Catches issues as code is written.
2. **Validate ②** — full-repo, all rules, run on demand and by `/maintaining-full-coverage`. "0 findings" is the bar. This is the lint dimension of the maintaining-full-coverage gate.
3. **CI ③** — automates tier 2 so regressions block at merge. Runs on every push/PR.

Where the same tool covers both ① and ②, that is called out below.

---

## Three-tier recommendation

### C#

| Tier | Tool | Command | Why |
|---|---|---|---|
| ① On-save | `dotnet format` | `dotnet format MFTLib.sln --include <file>` | Applies `.editorconfig` whitespace + style rules; instant per-file |
| | IDE live | Roslyn analyzers in Rider/VS | Zero-latency in-editor feedback; complements `dotnet format` |
| ② Validate | `dotnet build -warnaserror` | `dotnet build MFTLib.sln -warnaserror` | Roslyn analyzers ship in the SDK; `-warnaserror` makes them a hard gate |
| ② Validate | **Roslynator** | Add `<PackageReference Include="Roslynator.Analyzers" Version="4.*" PrivateAssets="all" />` to each `.csproj`, then build | 500+ additional Roslyn rules; runs at build time, zero extra tooling |
| ② Deep | **jbinspect** | `jb inspectcode MFTLib.sln --output=report.xml` | Solution-scoped; slower (CI/validate tier only, not on-save) |
| ③ CI | `dotnet format --verify-no-changes` | `dotnet format MFTLib.sln --verify-no-changes` | Fails if any formatting drift — pairs with `dotnet build -warnaserror` |
| ③ CI | jbinspect | (same as tier ②) | Full solution analysis blocking merge |

**Enabling Roslyn analyzers** — add to each `.csproj` `<PropertyGroup>` (or a new `Directory.Build.props` at repo root to apply to all):

```xml
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisLevel>latest-Recommended</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

`EnforceCodeStyleInBuild` makes `.editorconfig` naming rules into build errors (they are currently warnings-only in the IDE).

---

### C++

| Tier | Tool | Command | Why |
|---|---|---|---|
| ① On-save | `clang-format` | `clang-format -i <file>` | Formats to `.clang-format` style; instant, no compile DB needed |
| ② Validate | `clang-tidy` | `clang-tidy <file> -p build/` | Style + modernize + bugprone checks; needs `compile_commands.json` (see below) |
| ② Validate | `cppcheck` | `cppcheck --enable=all --error-exitcode=1 MFTLibNative/` | Zero-false-positive real bugs; complementary to clang-tidy; no compile DB needed |
| ③ CI | `clang-format --dry-run --Werror` | `clang-format --dry-run --Werror $(find MFTLibNative -name '*.cpp' -o -name '*.h')` | Fails on any formatting deviation |
| ③ CI | clang-tidy | (same as tier ②) | Full static analysis blocking merge |
| ③ CI | cppcheck | (same as tier ②) | Zero-FP bug checks blocking merge |

**Getting `compile_commands.json`** — build CMake with:

```bash
cmake -S MFTLibNative -B build/linux -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
# compile_commands.json lands at build/linux/compile_commands.json
# Pass -p build/linux/ to clang-tidy
```

clangd (the language server) also reads `compile_commands.json` — when it is present, clang-tidy checks run live in VS Code / CLion as you type.

**Starter `.clang-format`** (place at repo root or `MFTLibNative/.clang-format`):

```yaml
BasedOnStyle: Google
IndentWidth: 4
ColumnLimit: 120
```

Adjust `BasedOnStyle` to match the existing code style before auto-fixing.

**Starter `.clang-tidy`** (place at `MFTLibNative/.clang-tidy`):

```yaml
Checks: >
  clang-diagnostic-*,
  clang-analyzer-*,
  bugprone-*,
  modernize-*,
  readability-*,
  -modernize-use-trailing-return-type,
  -readability-magic-numbers
WarningsAsErrors: "*"
```

---

## On-save hook (C# — Claude Code PostToolUse)

Paste into `.claude/settings.json` (create `.claude/` dir at repo root if absent):

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Write|Edit",
        "hooks": [
          {
            "type": "command",
            "command": "f=$(jq -r '.tool_input.file_path // .tool_response.filePath // empty'); case \"$f\" in *.cs) cd \"$(dirname \"$f\")\"; o=$(dotnet format \"$(git -C \"$(dirname \"$f\")\" rev-parse --show-toplevel)\" --include \"$f\" 2>&1); [ -n \"$o\" ] && jq -n --arg c \"dotnet format:\\n$o\" '{hookSpecificOutput:{hookEventName:\"PostToolUse\",additionalContext:$c}}';; esac; exit 0"
          }
        ]
      }
    ]
  }
}
```

For C++ on-save, add a second `*.cpp|*.h` branch calling `clang-format -i "$f"`.

---

## CI step

Add a `lint` job to `.gitea/workflows/test.yml` (runs on the existing `ubuntu-latest` runner where `clang-format`, `clang-tidy`, and `cppcheck` are typically pre-installed):

```yaml
  lint:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.x'

      # --- C# ---
      - name: dotnet format (verify)
        run: dotnet format MFTLib.sln --verify-no-changes

      - name: dotnet build -warnaserror
        run: dotnet build MFTLib.sln -warnaserror --no-incremental

      # --- C++ ---
      - name: Install C++ lint tools
        run: sudo apt-get install -y clang-format clang-tidy cppcheck

      - name: clang-format check
        run: |
          find MFTLibNative -name '*.cpp' -o -name '*.h' | \
            xargs clang-format --dry-run --Werror

      - name: CMake (generate compile_commands.json)
        run: cmake -S MFTLibNative -B build/lint -DCMAKE_EXPORT_COMPILE_COMMANDS=ON

      - name: clang-tidy
        run: |
          find MFTLibNative -name '*.cpp' | \
            xargs clang-tidy -p build/lint/

      - name: cppcheck
        run: cppcheck --enable=all --error-exitcode=1 MFTLibNative/
```

---

## Rollout

Adopt incrementally — no big-bang required:

1. **Mechanical autofix sweep** — run `dotnet format MFTLib.sln` (C#) and `clang-format -i` on all C++ files, commit as a single formatting commit.
2. **Hand-fix real findings** — enable Roslyn analyzers + Roslynator, run `dotnet build -warnaserror`; address the semantic diagnostics. Add `.clang-tidy` and fix clang-tidy / cppcheck findings.
3. **Bake the gate** — add the CI `lint` job + the PostToolUse on-save hook. Zero findings becomes the merge bar.

projdash did exactly this in three stacked PRs: #113 (autofix sweep), #115 (real fixes), #116 (bake the gate). Whether to use auto-fix + PR or review manually is your call — both paths land at the same gate.

---

## AI-slop gate (aislop)

**Status: ready-but-gated (C# only).** aislop (https://github.com/scanaislop/aislop) is a
language-agnostic AI-slop quality gate — deterministic, 40+ rules, scored 0-100.
**C# support is in progress (not shipped yet, no version pinned as of
2026-05-29).** Set it up now; **flip it on once aislop's C# engine lands.** Until
then aislop false-greens on C# (scores a meaningless 100) — do NOT enable the
gate yet; the per-language linters above (Roslyn/Roslynator + `dotnet format`,
and jbinspect where used) remain the real gate.

aislop will cover the **C# on landing**, but **never the C++** (unsupported) —
the C++ relies on clang-tidy/cppcheck as the quality gate and that does not change.

When C# support ships:
- **Per-edit (① on-save):** `aislop hook install --claude --project` (pin the binary
  version; never `@latest` — it network-checks every edit).
- **PR/CI gate (③):** `npx --yes aislop@<C#-capable version> ci .` — scores the
  WHOLE repo (gate = "don't-regress the whole-repo score"; no diff mode). On
  Gitea use the npx CLI, NOT the GitHub composite action `scanaislop/aislop@vX`.
- **Config `.aislop/config.yml`:** `ci.failBelow` (git-wizard's baseline is 80),
  `exclude` (e.g. `obj/`, `bin/`, generated `*.g.cs`/`*.Designer.cs`), whole-engine
  toggles. No per-rule config in 0.9.4 — clean up first, then gate.

Full detail: `C:\Users\mtsch\.claude\notes\idioms_linters.md` (AI-slop gate section).

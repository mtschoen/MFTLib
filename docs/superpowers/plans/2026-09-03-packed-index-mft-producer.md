# Packed Index MFT Producer Implementation Plan (plan 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native parser read real sizes and modified times, turn the elevated broker into a producer that writes packed index blocks directly into a client-created named section, and retire `ScanPayload` v2 once the new path is proven on a real drive.

**Architecture:** The native parser gains two columns on the compact application binary interface (size and modified time) plus a synthesized size-unknown bit, which costs an interface version bump from 3 to 4 and a stride change from 32 to 48 bytes. On the write side the non-elevated client creates the drive's block file at a planned size, maps it as a named section, and passes the section name in the `ArmAndScan` spec exactly where the page-file map name travels today. The elevated broker opens that section by name and writes rows and names straight into it as parse chunks arrive, so broker memory is bounded by the parse chunk size and there is no name table, no path pool, and no merge step. The header is written last with the complete flag, so a broker that dies mid-write leaves a block the client discards. Path resolution leaves the broker path entirely for block output. Catch-up, cursors, watch, `ReplaceWatchCursors`, progress frames, and `BrokerScanProfile` keep their current protocol.

**Tech Stack:** C++17 in `MFTLibNative` (MSBuild on Windows, CMake plus Ninja on Linux), C# on `net10.0` in `MFTLib`, `System.IO.MemoryMappedFiles` named sections, MSTest, coverlet for coverage, aislop as the quality gate.

**Spec:** `docs/superpowers/specs/2026-09-02-packed-index-design.md`, section 9 item 2, with the producer contract in section 4.1 and supporting detail in sections 3, 5.4, 7, 8, and 10. The spec stays in place for plans 3 to 5 and is deleted when plan 5 consumes it.

**Tracking issue:** https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/128

**Also owned by this plan:** https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/116, the root-row header field. `LookupEngine` currently mints the root handle at row 0 and starts every path descent there, which is right for an enumeration block and wrong for an MFT block whose root is NTFS record 5. Task 6 defines header offset 20 (today `ReservedPadding`) as a root-row `u32` and routes `Root` and `Find` through it. Existing blocks already write zero there, which is the correct enumeration value, so there is no format version bump.

**Plan 1** landed as https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/115 (squashed to `607e553`). Everything it built is on `main` under `MFTLib/Index/` and `docs/index-format.md`.

---

## Decisions

The spec's section 2 decisions are locked and are not reopened here. These are the decisions this plan had to make where the spec is silent.

1. **The compact entry grows by two columns appended at the end.** `int64 size` at offset 32 and `int64 modifiedTime` at offset 40, so every existing offset is unchanged and `MftResult.GetCompactEntry` only adds two reads. Stride goes 32 to 48; `MFT_NATIVE_ABI_VERSION` and `MFTLibNative.ExpectedMftNativeAbiVersion` go 3 to 4.
2. **Size-unknown crosses the interface as a synthesized bit in the existing `flags` field**, `MFT_ENTRY_FLAG_SIZE_UNKNOWN = 0x8000`, rather than a third new column. The field already carries the raw `FILE_RECORD_SEGMENT_HEADER` flags, whose defined bits are 0x0001 and 0x0002; 0x8000 is documented in `mft_api.h` as parser-synthesized, not on-disk. A separate column would push the stride to 56 with padding for one bit. **Confirm with the orchestrator.**
3. **Modified time crosses the interface as a raw FILETIME**, because the native side has no notion of .NET ticks. `MftRecord.ModifiedUtc` converts with `DateTime.FromFileTimeUtc` guarded against out-of-range values, which clamp to `DateTime.MinValue` rather than throwing. When `$STANDARD_INFORMATION` is absent the parser falls back to the `$FILE_NAME` `ModificationTime` field, mirroring how `fileAttributes` already falls back.
4. **Header offset 20 becomes `RootRow` (`u32`)**, per issue 116. `BlockFileCreateOptions.RootRow` defaults to 0 so enumeration blocks are unchanged; the MFT producer sets 5. No format version bump, because every block ever written has a zero there.
5. **The MFT producer lives under `MFTLib/Broker/`, not in `MFTLib.Index`.** Spec section 4.1 places it in the elevated broker, and the namespace rule only forbids `MFTLib.Index` from referencing `MFTLib.Broker`, not the reverse. `FileIndexOptions` reaches it through a delegate declared in `MFTLib.Index`, so the boundary holds and the selection logic is testable on Linux with a fake. **Confirm with the orchestrator.**
6. **The `ArmAndScan` spec token gains a sixth field, the output format** (`0` = scan payload, `1` = block), absent meaning `0`. This is what lets the payload path stay alive and green while the block path is built and proven, per the sequencing requirement below. Task 17 deletes the field along with the payload branch. **Confirm with the orchestrator.**
7. **`ScanReady` keeps its frame kind and wire layout.** In block mode its two `int64` fields carry the row count and the name pool used bytes. The `BrokerFrame` property names are changed to `RowCount` and `NamePoolUsedBytes` in task 17, when the payload meaning is gone and the rename costs nothing.
8. **`BrokerScanProfile.DirectoryIndex` in block mode simply does not write a row** for a record that fails the filter. An unwritten slot reads as not in use, which is exactly "a producer-side filter on which rows are marked in use" from spec section 4.1. Every directory is kept, so the parent column that path building walks stays intact.
9. **The block path parses with `resolvePaths: false`.** That is where the `PathLookup` table, the resolve phase, and the resolve progress phase drop out of the broker path. The native resolve code itself is not deleted: `MftVolume.ParseMFTFromFile(path, filter, MatchFlags.ResolvePaths, ...)` is still a public surface with its own tests, and removing it is out of scope.
10. **Name pool sizing is slot capacity times 48 bytes**, the same 24-UTF-16-unit mean that `EnumerationProducer.EstimatedNameBytesPerRow` already uses, then run through `BlockLayout.ComputeNamePoolCapacity` for headroom. Spec section 4.1 says "a per-machine average name length"; 48 bytes is the constant this plan picks, exposed as `MftBlockCapacity.DefaultAverageNameBytesPerRow` so a caller can override it.
11. **Windows-only tests guard with `OperatingSystem.IsWindows()` and `Assert.Inconclusive`,** and nothing this plan adds goes into the Linux exclusion filter in `scripts/coverage-linux.sh`. An inconclusive test is not a failure, so coverlet still writes its output.
12. **Name and parent selection is unchanged.** The parser keeps taking one name and one parent per record, the first non-DOS `$FILE_NAME` in the base segment, exactly as it does today. Hard links as multiple rows per record are deferred by spec section 10 and nothing here moves toward them, so no task touches that selection.
13. **Journal-wrap handling is already in place and is not reopened.** `EmitScanCompletionFramesAsync` already catches a catch-up failure after the scan, re-queries the cursor, emits a `Warning` frame, and watches from the current journal position. That is the spec section 7 behavior, it is orthogonal to the output format, and task 12 must leave it on the shared path rather than duplicating it into the block arm.
14. **Consumers pin the pre-retirement commit.** file-wizard and git-wizard consume MFTLib through a source submodule and still use `ScanPayload`. Tasks 1 to 16 are additive and leave both consumers building against `main`. Tasks 17 and 18 are the breaking ones and are deliberately the last code tasks, so the consumers pin the commit that task 16 signs off until plans 3 and 4 land their ports.

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework is `net10.0`.** No new `TargetFramework`, no `net10.0-windows`, no new `PackageReference` in any project.
- **C++ standard is C++17** (`CMAKE_CXX_STANDARD 17`). No C++20 constructs.
- **Namespace boundary.** `MFTLib.Index` never references `MFTLib.Mft`, `MFTLib.Broker`, `MFTLib.Internal`, or `MFTLib.Interop`, and never names a type declared under `MFTLib/Mft/`, `MFTLib/Broker/`, `MFTLib/Internal/`, or `MFTLib/Interop/`. The only allowed cross-folder types are `UsnJournalEntry`, `UsnJournalEntryOptions`, and `UsnReason` from `MFTLib/Journal/`. The reverse direction is allowed and this plan uses it: code under `MFTLib/Broker/` may reference `MFTLib.Index`.
- **Files stay under 400 lines.** `.aislop/config.yml` sets `quality.maxFileLoc: 400`, `maxFunctionLoc: 80`, `maxNesting: 5`, `maxParams: 6`, and `ci.failBelow: 100`. A finding of any kind red-lights the gate. Never edit `.aislop/config.yml` and never suppress a rule to pass.
- **No abbreviations in identifiers.** `maximum` not `max`, `configuration` not `config`, `cancellationToken` not `ct`, `directory` not `dir`, `attribute` not `attr`. This applies to new C++ identifiers too; existing native names such as `nameAttr` are left alone unless the task already rewrites that line.
- **No em-dashes** anywhere: code, comments, doc comments, markdown, commit messages.
- **Every bug fix gets a regression test that fails before the fix and passes after.** Verify both states; do not assert it.
- **Test-driven development per task.** Write the failing test, run it and see it fail for the stated reason, implement, run it and see it pass, commit.
- **Never assert on wall-clock time.** No sleep followed by an elapsed-time assertion. Timestamps written into blocks come from an injected value.
- **No hard-coded machine-specific absolute paths** in production code. Test code derives every path from `Path.GetTempPath()` (managed) or a `/tmp` constant already used by `linux_smoke_test.cpp` (native).
- **Clean-tree invariant.** `git clean -ffxd` must stay safe. Nothing this plan produces lives untracked in the working tree.
- **Commit after every task** with a green test run. Never leave the tree dirty between tasks.
- **Native error messages** go through the `SetErrorMessage` helper in `MFTLibNative/internal.h`, never `swprintf_s` directly.

## Build and verification notes

**Work only in the worktree `C:\Users\mtsch\MFTLib-worktrees\mft-producer` on branch `feat/index-mft-producer`.** Do not touch the main checkout at `C:\Users\mtsch\MFTLib`.

**Building the native library in a worktree.** `dotnet build` cannot build `MFTLibNative.vcxproj`. Use 64-bit MSBuild and pass `SolutionDir` pointing at the worktree with a trailing backslash, otherwise the post-build copy resolves against the wrong root and a stale `MFTLibNative.dll` from another project's output directory on `PATH` (git-wizard's `bin` is the usual culprit) shadows the one you just built:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest
& "$msbuild\MSBuild\Current\Bin\amd64\MSBuild.exe" MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143 -p:SolutionDir="C:\Users\mtsch\MFTLib-worktrees\mft-producer\"
dotnet build -c Release -p:Platform=x64
```

After any native change, confirm the loaded library is the one you built:

```powershell
Get-Item MFTLib.Tests\bin\x64\Release\net10.0\MFTLibNative.dll | Select-Object FullName, LastWriteTime
```

**Building the native library on Linux** (llamabox, or the Linux continuous-integration job):

```bash
cmake -S MFTLibNative -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug -DBUILD_TESTING=ON
cmake --build build/linux
LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

**What each surface can verify where.**

| Surface | Linux | Windows headless | Windows attended, elevated |
| --- | --- | --- | --- |
| Native parse from an exported MFT file (`ParseMFTFromFileUtf8`) | yes, `linux_smoke_test` | no (managed entry point is `ParseMFTFromFile`, Windows only) | yes |
| Managed parse from an exported MFT file (`MftVolume.ParseMFTFromFile`) | no | yes | yes |
| `MFTLib.Index` block reads and writes | yes | yes | yes |
| Named sections (`MemoryMappedFile` map names) | no | yes, no elevation needed | yes |
| Broker host and client over the in-process pipe harness with fake seams | yes | yes | yes |
| Real volume scan, real elevation | no | no, `RequiresAdmin` is skipped | yes, attended only |

The crew (pr-crew) runs on Linux only. Anything in the last column is an **attended checkpoint** on chonkers and is called out as such in the task.

**Test commands.**

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

```bash
bash scripts/coverage-linux.sh
```

```bash
aislop scan .        # before declaring a task done
aislop scan --staged # before committing
aislop ci .          # the gate; exit non-zero below score 100
```

**NTFS references used for the byte offsets in tasks 3 and 4.**

- `$STANDARD_INFORMATION` body layout, offsets 0x00 creation, 0x08 last altered (the modified time this plan reads), 0x10 MFT changed, 0x18 last read, 0x20 DOS file permissions: https://flatcap.github.io/linux-ntfs/ntfs/attributes/standard_information.html
- `ATTRIBUTE_RECORD_HEADER`, including `FormCode` 0 resident and 1 non-resident, `NameLength` 0 meaning an unnamed attribute, the resident `ValueLength` and `ValueOffset`, and the non-resident `FileSize`, with the explicit statement that `AllocatedLength`, `FileSize`, and `ValidDataLength` **are not valid when `LowestVcn` is nonzero**: https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header
- `$DATA` attribute, the real size of a file being the size of its unnamed data stream: https://flatcap.github.io/linux-ntfs/ntfs/attributes/data.html

The repository's own `MFTLibNative/ntfs.h` already models both structures and cites the same Microsoft pages, so the tasks below use its field names rather than raw offsets wherever it has one.

---

## Dispatch waves

Tasks inside one wave touch disjoint files and may run in parallel lanes. A wave starts only after every task in the previous wave is committed. This plan is more serial than plan 1 by nature: it is a pipeline through one native parser, one protocol, and one broker host, and most tasks edit a file the previous task just changed.

| Wave | Tasks | Lanes | Why serial against the previous wave |
| --- | --- | --- | --- |
| 1 | 1 | 1 | Every extraction task asserts against the fixture this builds. |
| 2 | 2 | 1 | The interface widening touches the same native and managed files as tasks 3 and 4. |
| 3 | 3 | 1 | Modified-time extraction rewrites `FindNamedAttribute` in `mft.records.cpp`. |
| 4 | 4 | 1 | Size extraction rewrites the same function again. |
| 5 | 5, 6, 7 | 3 | Broker record mapping, the root-row header field, and the named-section helper touch disjoint files. |
| 6 | 8 | 1 | The producer seam types are consumed by tasks 9 and 14. |
| 7 | 9, 10 | 2 | Producer selection in `FileIndex` and capacity planning in the broker client are disjoint. |
| 8 | 11 | 1 | The output-format protocol field touches the host, the protocol, and the client at once. |
| 9 | 12 | 1 | The broker block write path rewrites `JournalBrokerHost.Scan.cs`. |
| 10 | 13 | 1 | The directory-index filter edits the function task 12 just wrote. |
| 11 | 14 | 1 | Client block adoption consumes tasks 9, 10, 11, and 12. |
| 12 | 15 | 1 | The end-to-end test consumes everything above it. |
| 13 | 16 | 1, attended | Real-drive verification gates the retirement tasks. |
| 14 | 17 | 1 | Retiring the payload branch is breaking and waits for the sign-off. |
| 15 | 18 | 1 | Deleting `ScanPayload` and `ScanRecord` waits for the branch removal. |
| 16 | 19 | 1 | Documentation describes the finished state. |
| 17 | 20 | 1 | The gate runs over the finished branch. |

**Out of scope for this plan** (spec section 9 items 3 to 5): the file-wizard port and the deletion of its old index and cache, the git-wizard port, the README paragraph on the not-just-MFT scope, the aislop architecture rule enforcing the namespace boundary, the attended memory measurement against the 21.3M-file targets in spec section 8, and every deferred item in spec section 10 (children table, name interning, secondary name index, subtree exclusion, hard links as multiple rows).

---

## Phase 1: Native size and modified time

### Task 1: Deterministic size and modified-time fixture

**Files:**
- Create: `MFTLibNative/mft/mft_synthetic.fixture.cpp`
- Create: `MFTLibNative/mft/mft_fixture.h`
- Modify: `MFTLibNative/mft/mft_synthetic.cpp` (include the fragment inside the anonymous namespace; add the two exports)
- Modify: `MFTLibNative/MFTLibNative.vcxproj`, `MFTLibNative/MFTLibNative.vcxproj.filters`
- Modify: `MFTLib/Internal/MFTLibNative.cs`, `MFTLib/Mft/MftVolume.cs`
- Test: `MFTLibNative/test/linux_smoke_test.cpp` (new case `fixture_round_trip`), `MFTLib.Tests/MftFixtureTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `MFTLibNative/mft/mft_fixture.h` with `constexpr uint32_t kFixtureRecordSize = 1024;`, `constexpr uint64_t kFixtureRecordCount = 12;`, `constexpr uint64_t kFixtureModifiedBase = 132000000000000000ULL;`, `constexpr uint64_t kFixtureModifiedStep = 10000000ULL;`
  - `EXPORT bool GenerateFixtureMFT(const wchar_t* filePath)` under `#ifdef _WIN32`
  - `EXPORT bool GenerateFixtureMFTUtf8(const char* filePath)` under `#ifndef _WIN32`
  - `public static void MftVolume.GenerateFixtureMFT(string filePath)` throwing `InvalidOperationException` on a false return, mirroring `GenerateSyntheticMFT`
  - `internal static Func<string, bool> MFTLibNative._generateFixtureMft`

**Design notes:** The existing `GenerateSyntheticMFT` rolls its record contents from a private pseudo-random generator, so a test cannot state an expected size or timestamp without duplicating that generator. This fixture is the opposite: twelve hand-authored records with values a test writes as literals. It is generated by native code rather than by a managed builder so the Linux smoke test and the managed Windows tests assert against the same bytes. `mft_synthetic.cpp` is its own translation unit, not one of the `mft.cpp` fragments, so the new fragment is included by `mft_synthetic.cpp` and reuses its anonymous-namespace helpers `ApplyUSAProtection`, `StoreNtfsName`, `WriteStandardInformationAttribute`, and `WriteFileNameAttribute`. Adding it to `mft_synthetic.cpp` directly would push that file past the 400-line cap.

**The fixture.** Record size 1024, twelve records, 12288 bytes total. Record 0 carries the record size at byte offset 0x1C so `DetectRecordSizeFromHeader` finds the geometry. Modified FILETIME for record `n` is `kFixtureModifiedBase + n * kFixtureModifiedStep`, one second apart, distinct per record.

| Record | Header flags | Parent | Name | Attributes written | Expected size | Size unknown |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 0x0001 | 5 | `$MFT` | SI, FN, `$DATA` non-resident, `LowestVcn` 0, `FileSize` 65536 | 65536 | no |
| 1 to 4 | zeroed | | | none | skipped by the parser | |
| 5 | 0x0003 | 5 | `.` | SI, FN | 0 | no |
| 6 | 0x0001 | 5 | `resident.txt` | SI, FN, `$DATA` resident, `ValueLength` 37 | 37 | no |
| 7 | 0x0001 | 5 | `big.bin` | SI, FN, `$DATA` non-resident, `LowestVcn` 0, `FileSize` 1234567 | 1234567 | no |
| 8 | 0x0003 | 5 | `sub` | SI, FN | 0 | no |
| 9 | 0x0001 | 8 | `nodata.dat` | SI, FN, `$ATTRIBUTE_LIST` resident, no `$DATA` | 0 | **yes** |
| 10 | 0x0001 | 8 | `split.bin` | SI, FN, `$DATA` non-resident `LowestVcn` 8 `FileSize` 999, then `$DATA` non-resident `LowestVcn` 0 `FileSize` 4096 | 4096 | no |
| 11 | 0x0000 | | | zeroed | skipped by the parser | |

Record 10 exists because the Microsoft reference is explicit that `FileSize` is not valid when `LowestVcn` is nonzero; the parser must skip the first `$DATA` and take the second. Record 9 is the attribute-list case from spec section 4.1. The `$STANDARD_INFORMATION` DOS permissions field is 0x06 for record 0, 0x10 for records 5 and 8, and 0x20 for the rest, so attribute assertions stay meaningful.

- [ ] **Step 1: Write the failing native test**

Add to `MFTLibNative/test/linux_smoke_test.cpp`, alongside the existing declarations and cases:

```cpp
extern "C" bool GenerateFixtureMFTUtf8(const char* filePath);

bool test_fixture_round_trip() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture.mft";
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        std::fprintf(stderr, "  setup FAIL: GenerateFixtureMFTUtf8 returned false\n");
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePathName, nullptr, 0, 4096);
    // Records 0, 5, 6, 7, 8, 9, and 10 are in use and non-extension; 1 to 4 and 11 are
    // zeroed, so the parser reports twelve total and seven used.
    bool passed = parseResult != nullptr && parseResult->errorMessage[0] == L'\0' &&
                  parseResult->totalRecords == 12 && parseResult->usedRecords == 7;
    if (!passed && parseResult != nullptr) {
        std::fprintf(stderr, "  FAIL: total=%llu used=%llu\n",
                     static_cast<unsigned long long>(parseResult->totalRecords),
                     static_cast<unsigned long long>(parseResult->usedRecords));
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}
```

Add `{"fixture_round_trip", test_fixture_round_trip}` to the `tests` array and widen its `std::array<TestCase, 16>` to 17.

- [ ] **Step 2: Run it to verify it fails**

```bash
cmake -S MFTLibNative -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Debug -DBUILD_TESTING=ON && cmake --build build/linux
```

Expected: the build fails with an undefined reference to `GenerateFixtureMFTUtf8`.

- [ ] **Step 3: Write the fixture header**

Create `MFTLibNative/mft/mft_fixture.h`:

```cpp
#pragma once

#include <cstdint>

// Geometry and expected values of the deterministic size and modified-time
// fixture written by GenerateFixtureMFT. Hand-authored rather than rolled from a
// pseudo-random generator, so a test can state every expected value as a literal.
// The record table is documented in docs/superpowers/plans and mirrored by the
// managed tests in MFTLib.Tests/MftFixtureTests.cs.
constexpr uint32_t kFixtureRecordSize = 1024;
constexpr uint64_t kFixtureRecordCount = 12;
constexpr uint64_t kFixtureModifiedBase = 132000000000000000ULL;
constexpr uint64_t kFixtureModifiedStep = 10000000ULL;
```

- [ ] **Step 4: Write the fixture record builder**

Create `MFTLibNative/mft/mft_synthetic.fixture.cpp`. It is a fragment: it opens with the same `AISLOP_TU_FRAGMENT` guard the `mft.*.cpp` fragments use, and is included from inside the anonymous namespace of `mft_synthetic.cpp` so it can call `ApplyUSAProtection`, `StoreNtfsName`, `WriteStandardInformationAttribute`, and `WriteFileNameAttribute`.

```cpp
// Part of the synthetic-generator component. Included by mft_synthetic.cpp from
// inside its anonymous namespace; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT_SYNTHETIC
    #error "mft_synthetic.fixture.cpp is a fragment included by mft_synthetic.cpp"
#endif

struct FixtureDataSpec {
    bool present = false;
    bool resident = false;
    int64_t lowestVcn = 0;
    uint64_t size = 0;
};

struct FixtureRecordSpec {
    uint64_t recordIndex = 0;
    uint16_t headerFlags = 0;
    uint64_t parentRecord = 0;
    const wchar_t* name = nullptr;
    uint8_t nameLength = 0;
    uint32_t fileAttributes = 0;
    bool attributeListPresent = false;
    FixtureDataSpec firstData{};
    FixtureDataSpec secondData{};
};

// Writes one $DATA attribute in the requested form. Resident carries the size in
// ValueLength; non-resident carries it in FileSize, which the reference states is
// only valid when LowestVcn is zero, so the parser must ignore a nonzero-LowestVcn
// record and the fixture writes one deliberately.
uint16_t WriteFixtureDataAttribute(uint8_t* record, uint16_t offset, const FixtureDataSpec& data);

// Writes a minimal resident $ATTRIBUTE_LIST so a record can carry one without a
// $DATA in the base segment: the size-unknown case from the specification.
uint16_t WriteFixtureAttributeListAttribute(uint8_t* record, uint16_t offset);

void BuildFixtureRecord(uint8_t* record, const FixtureRecordSpec& spec);

bool GenerateFixtureMFTImpl(const char* filePath);
```

Implement each. `BuildFixtureRecord` follows `BuildSyntheticRecord`: zero the buffer, set the multi-sector magic `0x454C4946`, update-sequence-array offset 0x30 and size `(1024 / 512) + 1`, sequence number `recordIndex + 1`, header flags, first attribute offset `(0x30 + usaSize * 2 + 7) & ~7`, the record size at offset 0x1C, then the attributes in order (`$STANDARD_INFORMATION`, `$FILE_NAME`, `$ATTRIBUTE_LIST` when requested, `$DATA` occurrences), an end marker, and `ApplyUSAProtection` last. Build a `SyntheticMeta` by hand with `modTime = kFixtureModifiedBase + recordIndex * kFixtureModifiedStep` and `fileAttrs` from the spec so the reused `WriteStandardInformationAttribute` and `WriteFileNameAttribute` helpers apply.

`GenerateFixtureMFTImpl` allocates one `kFixtureRecordCount * kFixtureRecordSize` buffer, zeroes it, builds records 0, 5, 6, 7, 8, 9, 10 into it (leaving 1 to 4 and 11 zeroed), and writes it with `mftlib::platform::pwrite_at` through `mftlib::platform::open_write`, returning whether the whole buffer was written.

- [ ] **Step 5: Wire the fragment and the exports**

In `MFTLibNative/mft/mft_synthetic.cpp`, immediately before the `}  // namespace` that closes the anonymous namespace:

```cpp
#define AISLOP_TU_FRAGMENT_SYNTHETIC
// NOLINTNEXTLINE(bugprone-suspicious-include) -- component-as-TU pattern, see mft.cpp
#include "mft_synthetic.fixture.cpp"
#undef AISLOP_TU_FRAGMENT_SYNTHETIC
```

Add `#include "mft_fixture.h"` to its include block. In the `extern "C"` block add, in the matching platform arms:

```cpp
#ifdef _WIN32
EXPORT bool GenerateFixtureMFT(const wchar_t* filePath) {
    if (ShouldFailPathConversion()) {
        return false;
    }
    int utf8Length = WideCharToMultiByte(CP_UTF8, 0, filePath, -1, nullptr, 0, nullptr, nullptr);
    if (utf8Length <= 0) {
        return false;
    }
    std::string utf8(static_cast<size_t>(utf8Length - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, filePath, -1, utf8.data(), utf8Length, nullptr, nullptr);
    return GenerateFixtureMFTImpl(utf8.c_str());
}
#endif

#ifndef _WIN32
EXPORT bool GenerateFixtureMFTUtf8(const char* filePath) {
    return GenerateFixtureMFTImpl(filePath);
}
#endif
```

Add the fragment to `MFTLibNative.vcxproj` inside the same `ItemGroup` as the other fragments, copying the four-configuration exclusion attributes verbatim from the `mft\mft.records.cpp` line, and add the matching entry plus `mft\mft_fixture.h` to `MFTLibNative.vcxproj.filters`. `CMakeLists.txt` needs no change: fragments are not listed there.

- [ ] **Step 6: Run the native test to verify it passes**

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: 17 passed, 0 failed, including `fixture_round_trip`.

- [ ] **Step 7: Add the managed surface and its test**

In `MFTLib/Internal/MFTLibNative.cs` add the field, the declaration, and the `ResetToDefaults` line:

```csharp
internal static Func<string, bool> _generateFixtureMft = NativeGenerateFixtureMFT;

[DllImport(LibraryName, EntryPoint = "GenerateFixtureMFT", CallingConvention = CallingConvention.Cdecl,
    CharSet = CharSet.Unicode)]
[return: MarshalAs(UnmanagedType.I1)]
static extern bool NativeGenerateFixtureMFT(string filePath);
```

In `MFTLib/Mft/MftVolume.cs`, next to `GenerateSyntheticMFT`:

```csharp
/// <summary>
///     Writes the deterministic size and modified-time fixture: twelve 1024-byte records
///     with hand-authored sizes, timestamps, and attribute shapes, covering resident and
///     non-resident data, directories, a record whose data attribute lives in an extension
///     record, and a non-resident record whose first data attribute has a nonzero lowest
///     virtual cluster number. Test support, not a production entry point.
/// </summary>
public static void GenerateFixtureMFT(string filePath)
{
    if (!MFTLibNative._generateFixtureMft(filePath))
    {
        throw new InvalidOperationException($"GenerateFixtureMFT failed for {filePath}");
    }
}
```

Create `MFTLib.Tests/MftFixtureTests.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftFixtureTests
{
    internal const long ModifiedBaseFileTime = 132000000000000000L;
    internal const long ModifiedStepFileTime = 10000000L;

    string _fixturePath = null!;

    internal static bool SkipOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.Inconclusive("MftVolume.ParseMFTFromFile binds a Windows-only native export.");
        return true;
    }

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"mftlib-fixture-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_fixturePath))
        {
            File.Delete(_fixturePath);
        }
    }

    [TestMethod]
    public void Fixture_Parses_SevenInUseRecords()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _);
        Assert.AreEqual(7, records.Length);
        CollectionAssert.AreEquivalent(
            new ulong[] { 0, 5, 6, 7, 8, 9, 10 },
            records.Select(record => record.RecordNumber).ToArray());
    }

    [TestMethod]
    public void Fixture_NamesAndParents_MatchTheAuthoredTable()
    {
        if (SkipOnNonWindows())
        {
            return;
        }

        var records = MftVolume.ParseMFTFromFile(_fixturePath, out _)
            .ToDictionary(record => record.RecordNumber);
        Assert.AreEqual("resident.txt", records[6].FileName);
        Assert.AreEqual(5ul, records[6].ParentRecordNumber);
        Assert.AreEqual("nodata.dat", records[9].FileName);
        Assert.AreEqual(8ul, records[9].ParentRecordNumber);
        Assert.IsTrue(records[8].IsDirectory);
        Assert.IsFalse(records[7].IsDirectory);
    }
}
```

- [ ] **Step 8: Run the managed test**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~MftFixtureTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 9: Commit**

```bash
git add MFTLibNative/mft/mft_fixture.h MFTLibNative/mft/mft_synthetic.fixture.cpp MFTLibNative/mft/mft_synthetic.cpp MFTLibNative/MFTLibNative.vcxproj MFTLibNative/MFTLibNative.vcxproj.filters MFTLibNative/test/linux_smoke_test.cpp MFTLib/Internal/MFTLibNative.cs MFTLib/Mft/MftVolume.cs MFTLib.Tests/MftFixtureTests.cs
git commit -m "test(native): deterministic size and modified-time MFT fixture"
```

---

### Task 2: Widen the compact entry and bump the native interface to version 4

**Files:**
- Modify: `MFTLibNative/mft_api.h`
- Modify: `MFTLibNative/mft/mft.internal.h` (`ParsedEntry`, `SliceResult::append`)
- Modify: `MFTLibNative/mft/mft.parse_core.cpp` (`PopulatePathSlice` carries the two new columns)
- Modify: `MFTLib/Internal/MFTLibNative.cs`, `MFTLib/Mft/MftResult.cs`, `MFTLib/Mft/MftRecord.cs`
- Modify: `MFTLib.Tests/MftResultTests.cs` (version and stride literals), `MFTLibNative/test/linux_smoke_test.cpp` (version and stride literals)
- Test: `MFTLib.Tests/MftRecordSizeAndTimeTests.cs`

**Interfaces:**
- Consumes: task 1's fixture.
- Produces:
  - `constexpr uint32_t MFT_NATIVE_ABI_VERSION = 4;`
  - `constexpr uint16_t MFT_ENTRY_FLAG_SIZE_UNKNOWN = 0x8000;`
  - `struct MftCompactEntry` at 48 bytes with `int64_t size` at offset 32 and `int64_t modifiedTime` at offset 40
  - `internal const uint MFTLibNative.ExpectedMftNativeAbiVersion = 4;`
  - `internal const uint MFTLibNative.NativeCompactEntrySize = 48;`
  - `public long MftRecord.Size { get; }`
  - `public bool MftRecord.SizeKnown { get; }`
  - `public DateTime MftRecord.ModifiedUtc { get; }`

**Design notes:** This task widens the interface and plumbs the columns end to end while the parser still writes zeros into them, so the version bump, the stride change, and the mismatch-path tests land as one reviewable change separate from the extraction logic. `MFT_ENTRY_FLAG_SIZE_UNKNOWN` is 0x8000 because the `flags` field carries the raw record-header flags, whose defined bits are 0x0001 in use and 0x0002 directory; the top bit is free and is documented as parser-synthesized rather than on-disk. `ModifiedUtc` guards the FILETIME conversion: `DateTime.FromFileTimeUtc` throws for a negative value or one past `DateTime.MaxValue`, and a corrupt record must not take down a scan, so an out-of-range value reads as `DateTime.MinValue`.

- [ ] **Step 1: Write the failing test**

Create `MFTLib.Tests/MftRecordSizeAndTimeTests.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordSizeAndTimeTests
{
    [TestMethod]
    public void ExpectedAbiVersion_IsFour()
    {
        Assert.AreEqual(4u, MFTLibNative.ExpectedMftNativeAbiVersion);
    }

    [TestMethod]
    public void NativeCompactEntrySize_IsFortyEight()
    {
        Assert.AreEqual(48u, MFTLibNative.NativeCompactEntrySize);
    }

    [TestMethod]
    public void NativeLibrary_ReportsAbiVersionFour()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        Assert.AreEqual(4u, MFTLibNative._getMftNativeAbiVersion());
    }

    [TestMethod]
    public void ModifiedUtc_OutOfRangeFileTime_ReadsAsMinValue()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1, ParentRecordNumber = 5, Flags = 1, FileName = "x",
            ModifiedFileTime = long.MinValue
        });
        Assert.AreEqual(DateTime.MinValue, record.ModifiedUtc);
    }

    [TestMethod]
    public void ModifiedUtc_ValidFileTime_RoundTrips()
    {
        var expected = DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime);
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1, ParentRecordNumber = 5, Flags = 1, FileName = "x",
            ModifiedFileTime = MftFixtureTests.ModifiedBaseFileTime
        });
        Assert.AreEqual(expected, record.ModifiedUtc);
    }

    [TestMethod]
    public void SizeKnown_IsFalse_WhenTheSizeUnknownFlagIsSet()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 1, ParentRecordNumber = 5, Flags = 0x8001, FileName = "x"
        });
        Assert.IsFalse(record.SizeKnown);
        Assert.IsTrue(record.InUse);
    }
}
```

`MftRecord.CreateForTest` is a new `internal static` factory on `MftRecord` (the test assembly already has `InternalsVisibleTo`) that forwards to the materialized constructor. It takes one value carrier rather than a parameter per column, because seven parameters would break the `maxParams: 6` limit in `.aislop/config.yml`. Add both in this task, in `MFTLib/Mft/MftRecord.cs`:

```csharp
/// <summary>
///     Every column a test needs to mint a materialized record. Grouped into one value so
///     the factory stays inside the parameter limit as the row gains columns.
/// </summary>
sealed record MftRecordTestValues
{
    public required ulong RecordNumber { get; init; }
    public required ulong ParentRecordNumber { get; init; }
    public required ushort Flags { get; init; }
    public required string FileName { get; init; }
    public string? FullPath { get; init; }
    public FileAttributes FileAttributes { get; init; }
    public long Size { get; init; }
    public long ModifiedFileTime { get; init; }
}
```

Declare it `internal` alongside `MftRecord` in the same file.

- [ ] **Step 2: Run it to verify it fails**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~MftRecordSizeAndTimeTests"
```

Expected: FAIL to compile, `MftRecord` does not contain `CreateForTest`, `Size`, `SizeKnown`, or `ModifiedUtc`.

- [ ] **Step 3: Widen the native structure**

In `MFTLibNative/mft_api.h`:

```cpp
constexpr uint32_t MFT_NATIVE_ABI_VERSION = 4;

// Parser-synthesized, not an on-disk NTFS record flag. The flags field carries the
// raw FILE_RECORD_SEGMENT_HEADER flags, whose defined bits are 0x0001 (in use) and
// 0x0002 (directory); the parser sets this top bit when a non-directory record has
// no unnamed $DATA attribute in its base segment, so the size column holds zero
// because the size is unknown rather than because the file is empty.
constexpr uint16_t MFT_ENTRY_FLAG_SIZE_UNKNOWN = 0x8000;

struct MftCompactEntry {
    uint64_t recordNumber;
    uint64_t parentRecordNumber;
    uint64_t stringOffset;  // UTF-16 code units from the selected pool base
    uint32_t fileAttributes;
    uint16_t flags;
    uint16_t stringLength;  // UTF-16 code units; zero is valid
    int64_t size;           // bytes; zero for a directory or a size-unknown record
    int64_t modifiedTime;   // FILETIME, 100-nanosecond intervals since 1601-01-01 UTC
};
```

In `mft.internal.h`, add `int64_t size;` and `int64_t modifiedTime;` to `ParsedEntry`, and extend the `MftCompactEntry` aggregate initializer in `SliceResult::append` to carry both. In `mft.parse_core.cpp`, `PopulatePathSlice` copies `src.size` and `src.modifiedTime` into its `ParsedEntry` alongside the existing fields, so the resolve path does not drop them.

- [ ] **Step 4: Widen the managed side**

`MFTLib/Internal/MFTLibNative.cs`: `ExpectedMftNativeAbiVersion` to `4`, `NativeCompactEntrySize` to `48`.

`MFTLib/Mft/MftResult.cs`, in `GetCompactEntry`, after the existing reads:

```csharp
var size = Unsafe.ReadUnaligned<long>(row + 32);
var modifiedFileTime = Unsafe.ReadUnaligned<long>(row + 40);
```

and pass both to the `MftRecord` constructor.

`MFTLib/Mft/MftRecord.cs`: add `readonly long _size;` and `readonly long _modifiedFileTime;`, thread them through both constructors and `Materialize`, and add:

```csharp
/// <summary>
///     Size in bytes of the unnamed data stream. Zero for a directory, and zero when
///     <see cref="SizeKnown" /> is false, which means the record's data attribute lives
///     in an extension record this parser does not follow.
/// </summary>
public long Size => _size;

public bool SizeKnown => (_flags & SizeUnknownFlag) == 0;

/// <summary>
///     Last modification time from <c>$STANDARD_INFORMATION</c>. A value the runtime
///     cannot represent reads as <see cref="DateTime.MinValue" /> rather than throwing,
///     so one corrupt record never fails a whole scan.
/// </summary>
public DateTime ModifiedUtc
{
    get
    {
        if (_modifiedFileTime <= 0 || _modifiedFileTime > MaximumFileTime)
        {
            return DateTime.MinValue;
        }

        return DateTime.FromFileTimeUtc(_modifiedFileTime);
    }
}

const ushort SizeUnknownFlag = 0x8000;

// DateTime.MaxValue as a FILETIME. Anything past it makes FromFileTimeUtc throw.
static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();
```

Add the `internal static MftRecord CreateForTest(...)` factory forwarding to the materialized constructor.

- [ ] **Step 5: Update the interface-mismatch tests and the smoke test literals**

`MFTLib.Tests/MftResultTests.cs`: rename `GetMftNativeAbiVersion_ReturnsVersion3` to `GetMftNativeAbiVersion_ReturnsVersion4` and assert `4u`; in `MftResult_AbiVersionMismatch_ThrowsInvalidOperation` update the comment to `// Mismatch (expected 4)`; in `MftResult_EntryStrideMismatch_ThrowsInvalidOperation` change `EntryStride = 40` to `EntryStride = 32 // Mismatch (expected 48)`, which is the stride the previous interface version used and is therefore the realistic mismatch to test.

`MFTLibNative/test/linux_smoke_test.cpp`: `test_abi_version` asserts 4; `test_round_trip` and `test_round_trip_4096` assert `abiVersion == 4` and `entryStride == 48`.

- [ ] **Step 6: Build both and run**

```powershell
& $msbuild MFTLibNative\MFTLibNative.vcxproj -t:Build -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143 -p:SolutionDir="C:\Users\mtsch\MFTLib-worktrees\mft-producer\"
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: both green. 6 new managed tests pass; the smoke test still reports 17 passed.

- [ ] **Step 7: Commit**

```bash
git add MFTLibNative/mft_api.h MFTLibNative/mft/mft.internal.h MFTLibNative/mft/mft.parse_core.cpp MFTLibNative/test/linux_smoke_test.cpp MFTLib/Internal/MFTLibNative.cs MFTLib/Mft/MftResult.cs MFTLib/Mft/MftRecord.cs MFTLib.Tests/MftResultTests.cs MFTLib.Tests/MftRecordSizeAndTimeTests.cs
git commit -m "feat(native): compact entry gains size and modified time, ABI version 4"
```

---

### Task 3: Extract modified time from $STANDARD_INFORMATION

**Files:**
- Modify: `MFTLibNative/mft/mft.records.cpp`
- Test: `MFTLibNative/test/linux_smoke_test.cpp` (new case `fixture_modified_time`), `MFTLib.Tests/MftFixtureTests.cs`

**Interfaces:**
- Consumes: task 1's fixture, task 2's `ParsedEntry.modifiedTime`.
- Produces: no new public surface. `MftRecord.ModifiedUtc` starts returning real values.

**Design notes:** `TryExtractStandardInformation` already validates that the resident value offset is at least 0x18 and that 36 bytes of body fit inside `RecordLength`, then reads the DOS permissions at body offset 32. The last-modification FILETIME is at body offset 8, well inside the same 36-byte guard, so no new bounds check is needed. The fallback when a record has no `$STANDARD_INFORMATION` is the `$FILE_NAME` attribute's `ModificationTime` field, which `FILE_NAME` in `ntfs.h` already models; that mirrors the existing `fileAttributes` fallback exactly.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLibNative/test/linux_smoke_test.cpp`:

```cpp
bool test_fixture_modified_time() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture_time.mft";
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePathName, nullptr, 0, 4096);
    bool passed = parseResult != nullptr && parseResult->entries != nullptr;
    if (passed) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            const MftCompactEntry& entry = parseResult->entries[i];
            auto expected = static_cast<int64_t>(132000000000000000ULL + entry.recordNumber * 10000000ULL);
            if (entry.modifiedTime != expected) {
                std::fprintf(stderr, "  FAIL: record %llu modifiedTime %lld, expected %lld\n",
                             static_cast<unsigned long long>(entry.recordNumber),
                             static_cast<long long>(entry.modifiedTime), static_cast<long long>(expected));
                passed = false;
            }
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}
```

Register it and widen the `tests` array to 18.

Add to `MFTLib.Tests/MftFixtureTests.cs`:

```csharp
[TestMethod]
public void Fixture_ModifiedTime_ComesFromStandardInformation()
{
    if (SkipOnNonWindows())
    {
        return;
    }

    var records = MftVolume.ParseMFTFromFile(_fixturePath, out _)
        .ToDictionary(record => record.RecordNumber);
    foreach (var (recordNumber, record) in records)
    {
        var expected = DateTime.FromFileTimeUtc(
            ModifiedBaseFileTime + (long)recordNumber * ModifiedStepFileTime);
        Assert.AreEqual(expected, record.ModifiedUtc, $"record {recordNumber}");
    }
}
```

- [ ] **Step 2: Run both to verify they fail**

```bash
cmake --build build/linux && LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: `fixture_modified_time` FAIL, every record reporting `modifiedTime 0`.

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~MftFixtureTests"
```

Expected: FAIL, expected a 2019 timestamp, actual `DateTime.MinValue`.

- [ ] **Step 3: Implement the extraction**

In `mft.records.cpp`, change `TryExtractStandardInformation` to take one output aggregate instead of two parallel out parameters, which keeps the function inside the six-parameter limit as more fields are read:

```cpp
struct StandardInformationValues {
    uint32_t fileAttributes = 0;
    int64_t modifiedTime = 0;
    bool present = false;
};

bool TryExtractStandardInformation(const ATTRIBUTE_RECORD_HEADER* attribute,
                                   StandardInformationValues* values) {
    constexpr size_t kResidentHeaderSize = 0x18;
    constexpr size_t kMinStandardInformationSize = 36;
    if (attribute->Form.Resident.ValueOffset < kResidentHeaderSize ||
        static_cast<size_t>(attribute->Form.Resident.ValueOffset) + kMinStandardInformationSize >
            attribute->RecordLength) {
        return false;
    }
    const auto* value = reinterpret_cast<const uint8_t*>(attribute) + attribute->Form.Resident.ValueOffset;
    // $STANDARD_INFORMATION body: 0x00 creation, 0x08 last altered, 0x10 MFT changed,
    // 0x18 last read, 0x20 DOS file permissions. The 36-byte guard above covers all of
    // these, so neither read needs a further bounds check.
    memcpy(&values->modifiedTime, value + 8, sizeof(int64_t));
    memcpy(&values->fileAttributes, value + 32, sizeof(uint32_t));
    values->present = true;
    return true;
}
```

Thread `StandardInformationValues*` through `FindNamedAttribute` in place of the two pointers, and in `ScanRecordForEntry`:

```cpp
outEntry->fileAttributes = standardInformation.present ? standardInformation.fileAttributes
                                                       : nameAttr->FileAttributes;
outEntry->modifiedTime = standardInformation.present
                             ? standardInformation.modifiedTime
                             : static_cast<int64_t>(nameAttr->ModificationTime);
```

- [ ] **Step 4: Run both to verify they pass**

Same two commands as step 2. Expected: 18 native cases pass; the managed fixture tests pass.

- [ ] **Step 5: Run the full managed suite**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

Expected: green. Nothing else reads `ModifiedUtc` yet.

- [ ] **Step 6: Commit**

```bash
git add MFTLibNative/mft/mft.records.cpp MFTLibNative/test/linux_smoke_test.cpp MFTLib.Tests/MftFixtureTests.cs
git commit -m "feat(native): read modified time from standard information"
```

---

### Task 4: Extract size from the unnamed $DATA attribute

**Files:**
- Modify: `MFTLibNative/mft/mft.records.cpp`
- Test: `MFTLibNative/test/linux_smoke_test.cpp` (new case `fixture_sizes`), `MFTLib.Tests/MftFixtureTests.cs`

**Interfaces:**
- Consumes: tasks 1 to 3.
- Produces: no new public surface. `MftRecord.Size` and `MftRecord.SizeKnown` start returning real values.

**Design notes:** Four rules, straight from spec section 4.1 and the Microsoft reference. An **unnamed** `$DATA` is `TypeCode == Data && NameLength == 0`; a named one is an alternate data stream and is ignored. **Resident** (`FormCode == 0`) size is `Form.Resident.ValueLength`. **Non-resident** (`FormCode == 1`) size is `Form.Nonresident.FileSize`, and only when `Form.Nonresident.LowestVcn.QuadPart == 0`, because the reference states plainly that `FileSize` is not valid for a nonzero lowest virtual cluster number. A **directory** record (header flag 0x0002) is size 0 and size-known, without looking for `$DATA` at all. A **non-directory record with no qualifying unnamed `$DATA` in the base segment** gets size 0 and `MFT_ENTRY_FLAG_SIZE_UNKNOWN`; that is the attribute-list case, and a consumer may fall back to a file-information call for those rows.

The attribute walk in `FindNamedAttribute` already returns as soon as it finds the first non-DOS `$FILE_NAME`, so it cannot be reused to reach a `$DATA` that follows. Rework it to walk the whole attribute chain, remembering the first non-DOS `$FILE_NAME` and the first qualifying unnamed `$DATA` and continuing to the end marker, then return both. That changes when the function stops, not what it accepts: the existing malformed-attribute guards stay exactly where they are, and the existing `malformed_attribute_offset` smoke test must still pass.

- [ ] **Step 1: Write the failing tests**

Add to `MFTLibNative/test/linux_smoke_test.cpp` a `fixture_sizes` case asserting, by record number, the expected size and the size-unknown bit from the task 1 table:

```cpp
struct ExpectedSize {
    uint64_t recordNumber;
    int64_t size;
    bool sizeUnknown;
};

bool test_fixture_sizes() {
    constexpr const char* kFixturePathName = "/tmp/mftlib_fixture_sizes.mft";
    const std::array<ExpectedSize, 7> expected = {{
        {0, 65536, false},
        {5, 0, false},
        {6, 37, false},
        {7, 1234567, false},
        {8, 0, false},
        {9, 0, true},
        {10, 4096, false},
    }};
    if (!GenerateFixtureMFTUtf8(kFixturePathName)) {
        return false;
    }
    MftParseResult* parseResult = ParseMFTFromFileUtf8(kFixturePathName, nullptr, 0, 4096);
    bool passed = parseResult != nullptr && parseResult->usedRecords == expected.size();
    if (passed) {
        for (uint64_t i = 0; i < parseResult->usedRecords; i++) {
            const MftCompactEntry& entry = parseResult->entries[i];
            const ExpectedSize* match = nullptr;
            for (const auto& candidate : expected) {
                if (candidate.recordNumber == entry.recordNumber) {
                    match = &candidate;
                }
            }
            bool unknown = (entry.flags & MFT_ENTRY_FLAG_SIZE_UNKNOWN) != 0;
            if (match == nullptr || entry.size != match->size || unknown != match->sizeUnknown) {
                std::fprintf(stderr, "  FAIL: record %llu size %lld unknown %d\n",
                             static_cast<unsigned long long>(entry.recordNumber),
                             static_cast<long long>(entry.size), static_cast<int>(unknown));
                passed = false;
            }
        }
    }
    if (parseResult != nullptr) {
        FreeMftResult(parseResult);
    }
    std::remove(kFixturePathName);
    return passed;
}
```

Register it and widen the `tests` array to 19.

Add three managed tests to `MftFixtureTests`:

```csharp
[TestMethod]
public void Fixture_ResidentData_SizeIsTheValueLength()
{
    if (SkipOnNonWindows()) { return; }
    var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
    Assert.AreEqual(37L, records[6].Size);
    Assert.IsTrue(records[6].SizeKnown);
}

[TestMethod]
public void Fixture_NonResidentData_SizeComesFromTheLowestVcnZeroRun()
{
    if (SkipOnNonWindows()) { return; }
    var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
    Assert.AreEqual(1234567L, records[7].Size);
    // Record 10's first $DATA has a nonzero lowest virtual cluster number, whose file
    // size field is not valid; the parser must take the second one.
    Assert.AreEqual(4096L, records[10].Size);
    Assert.IsTrue(records[10].SizeKnown);
}

[TestMethod]
public void Fixture_DirectoriesAndMissingData_ReportZeroWithTheRightKnownFlag()
{
    if (SkipOnNonWindows()) { return; }
    var records = MftVolume.ParseMFTFromFile(_fixturePath, out _).ToDictionary(r => r.RecordNumber);
    Assert.AreEqual(0L, records[8].Size);
    Assert.IsTrue(records[8].SizeKnown, "a directory has a known size of zero");
    Assert.AreEqual(0L, records[9].Size);
    Assert.IsFalse(records[9].SizeKnown, "the data attribute lives in an extension record");
}
```

- [ ] **Step 2: Run both to verify they fail**

Expected native: `fixture_sizes` FAIL, every record reporting `size 0 unknown 0`. Expected managed: FAIL on record 6, expected 37, actual 0.

- [ ] **Step 3: Implement the extraction**

Add to the anonymous namespace of `mft.records.cpp`:

```cpp
// Reports the unnamed $DATA size for one attribute, or false when this attribute is
// not the one that carries it: a named stream, or a non-resident record whose lowest
// virtual cluster number is nonzero, for which the reference states the file size
// field is not valid.
bool TryExtractDataSize(const ATTRIBUTE_RECORD_HEADER* attribute, int64_t* size) {
    if (attribute->NameLength != 0) {
        return false;
    }
    if (attribute->FormCode == 0) {
        *size = static_cast<int64_t>(attribute->Form.Resident.ValueLength);
        return true;
    }
    if (attribute->Form.Nonresident.LowestVcn.QuadPart != 0) {
        return false;
    }
    *size = attribute->Form.Nonresident.FileSize;
    return true;
}
```

Rework `FindNamedAttribute` into `ScanRecordAttributes`, which walks to the end marker and fills one output aggregate:

```cpp
struct RecordAttributes {
    PFILE_NAME nameAttribute = nullptr;
    StandardInformationValues standardInformation{};
    int64_t dataSize = 0;
    bool dataPresent = false;
};
```

Inside the walk, keep the first non-DOS `$FILE_NAME` (`nameAttr->Flags != 2`) instead of returning at it, and record the first attribute for which `attribute->TypeCode == Data && TryExtractDataSize(...)` succeeds. Every existing malformed-attribute guard stays where it is and still returns a failure for the whole record.

In `ScanRecordForEntry`, after the name check:

```cpp
bool isDirectory = (rec->Flags & 0x0002) != 0;
outEntry->flags = rec->Flags;
if (isDirectory) {
    outEntry->size = 0;
} else if (attributes.dataPresent) {
    outEntry->size = attributes.dataSize;
} else {
    outEntry->size = 0;
    outEntry->flags |= MFT_ENTRY_FLAG_SIZE_UNKNOWN;
}
```

- [ ] **Step 4: Run both to verify they pass**

Expected: 19 native cases pass; the six fixture tests pass.

- [ ] **Step 5: Run the full suites on both platforms**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

```bash
bash scripts/coverage-linux.sh
```

Expected: both green. `NativeParserCoverageTests` and `PathResolutionTests` exercise the same walk and must be unaffected; if one fails, the walk changed behavior it should not have.

- [ ] **Step 6: Commit**

```bash
git add MFTLibNative/mft/mft.records.cpp MFTLibNative/test/linux_smoke_test.cpp MFTLib.Tests/MftFixtureTests.cs
git commit -m "feat(native): read size from the unnamed data attribute"
```

---

## Phase 2: Root row, named sections, and the producer seam

### Task 5: Carry size and modified time through the broker record mapping

**Files:**
- Modify: `MFTLib/Broker/Host/JournalBrokerHost.cs` (`ToScanRecords`)
- Test: `MFTLib.Tests/JournalBrokerHostRealSeamsTests.cs`

**Interfaces:**
- Consumes: `MftRecord.Size`, `MftRecord.ModifiedUtc`.
- Produces: no new surface. `ScanRecord.Size` and `ScanRecord.LastWriteTicks` stop being constant zero.

**Design notes:** `ScanRecord` has carried `Size` and `LastWriteTicks` fields since it was written, always zero from the MFT path, with a comment saying so. Filling them is purely additive for the two consumers and is the smallest possible proof that the native columns reach managed code through the existing pipeline. `ScanPayload` has no size-unknown bit, so a size-unknown record still writes zero here; that is a known limitation of the format being retired and it is recorded in the replacement comment rather than worked around.

- [ ] **Step 1: Write the failing test**

Add to `MFTLib.Tests/JournalBrokerHostRealSeamsTests.cs`:

```csharp
[TestMethod]
public void ToScanRecords_CarriesSizeAndModifiedTimeFromTheMftRecord()
{
    var modified = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    var record = MftRecord.CreateForTest(new MftRecordTestValues
    {
        RecordNumber = 42, ParentRecordNumber = 5, Flags = 1, FileName = "a.txt",
        FullPath = @"C:\a.txt", FileAttributes = FileAttributes.Archive,
        Size = 4321, ModifiedFileTime = modified.ToFileTimeUtc()
    });

    var scanRecords = JournalBrokerHost.ToScanRecordsForTest([record]);

    Assert.AreEqual(1, scanRecords.Length);
    Assert.AreEqual(4321ul, scanRecords[0].Size);
    Assert.AreEqual(modified.Ticks, scanRecords[0].LastWriteTicks);
}
```

`ToScanRecordsForTest` is an `internal static` passthrough to the private `ToScanRecords`, added in this task. `MftRecordTestValues.FullPath` already exists from task 2.

- [ ] **Step 2: Run it to verify it fails**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~ToScanRecords_CarriesSizeAndModifiedTime"
```

Expected: FAIL, expected 4321, actual 0.

- [ ] **Step 3: Implement**

Replace the body and the stale comment in `JournalBrokerHost.ToScanRecords`:

```csharp
// The MFT path fills Size and LastWriteTicks from the record's own columns. It cannot
// express a size-unknown record: the scan payload format has no such flag, so such a
// record writes zero here and reads as an empty file. The block write path carries the
// flag properly; this limitation belongs to the format being retired.
static ScanRecord[] ToScanRecords(MftRecord[] records)
{
    var result = new List<ScanRecord>(records.Length);
    foreach (var record in records)
    {
        if (!record.InUse || string.IsNullOrEmpty(record.FullPath))
        {
            continue;
        }

        result.Add(new ScanRecord(
            record.RecordNumber, record.ParentRecordNumber, (ulong)Math.Max(record.Size, 0),
            record.ModifiedUtc.Ticks, (uint)record.FileAttributes, record.IsDirectory,
            record.FileName, record.FullPath));
    }

    return result.ToArray();
}
```

- [ ] **Step 4: Run it to verify it passes, then the full suite**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

Expected: green.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Host/JournalBrokerHost.cs MFTLib/Mft/MftRecord.cs MFTLib.Tests/JournalBrokerHostRealSeamsTests.cs
git commit -m "feat(broker): scan records carry real size and modified time"
```

---

### Task 6: Root row header field (issue 116)

**Files:**
- Modify: `MFTLib/Index/BlockHeader.cs`, `MFTLib/Index/BlockFileCreateOptions.cs`, `MFTLib/Index/BlockFile.cs`, `MFTLib/Index/LookupEngine.cs`
- Modify: `docs/index-format.md` (header table row at offset 20)
- Test: `MFTLib.Tests/Index/LookupEngineTests.cs`, `MFTLib.Tests/Index/BlockHeaderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public uint BlockHeader.RootRow` at `[FieldOffset(20)]`, replacing `ReservedPadding`
  - `public uint BlockFileCreateOptions.RootRow { get; init; }` defaulting to 0

**Design notes:** Fixes https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/116. `LookupEngine.Root` mints the root handle at row 0 and `LookupEngine.Find` starts every descent there, which is right for an enumeration block whose producer writes its root at row 0 and wrong for an MFT block whose root is NTFS record 5 while record 0 is `$MFT`. Nothing produces an MFT block yet, so nothing is broken today; task 8 is the first thing that would break. Offset 20 is `ReservedPadding` today and every block ever written has a zero there, which is exactly the enumeration value, so this costs no format version bump. `BlockHeader.Validate` gains no rule: a root row past `SlotCapacity` is a corrupt block, but so is any other out-of-range column, and the row-region bound check that already exists is what protects the read.

- [ ] **Step 1: Write the failing test**

`MFTLib.Tests/Index/SyntheticBlockBuilder.cs` writes rows through sequential `AddRow` calls and cannot place a row at a chosen index, so this task extends it with two members before the tests can be written:

- `public uint AddRowAt(uint rowIndex, string name, uint parentRow, RowFlags flags, long size, DateTime modifiedUtc, uint attributes = 0)`: the body of the existing `AddRow` with the row index taken as an argument instead of drawn from `_nextRow`, advancing `_nextRow` to `rowIndex + 1` when it lags. Refactor `AddRow` to call it, so there is one implementation.
- `public static SyntheticBlockBuilder MftShaped()`: constructs a builder, calls `MutateHeader` to set `ProducerKind.Mft` and `RootRow = 5`, then writes row 0 as `$MFT` with parent 0, row 5 as `.` with parent 5 and the directory flag, row 6 as `documents` with parent 5 and the directory flag, and row 7 as `notes.txt` with parent 6 and a size of 99, and calls `Complete`.

`AddRowAt` takes seven parameters including the default, which is over the `maxParams: 6` limit, so group `size`, `modifiedUtc`, and `attributes` into the existing `RowColumns` record and take `(uint rowIndex, string name, in RowColumns columns)`. Update `AddRow` and its existing callers in the same commit.

Then add to `MFTLib.Tests/Index/LookupEngineTests.cs`:

```csharp
[TestMethod]
public void Root_UsesTheHeaderRootRow_NotRowZero()
{
    using var builder = SyntheticBlockBuilder.MftShaped();
    using var block = builder.OpenForReading(out var validation)!;
    Assert.AreEqual(BlockValidationResult.Valid, validation);
    var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false, rootDirectoryPath: @"T:\");
    var snapshot = Snapshot.Create([driveBlock]);

    var root = LookupEngine.Root(snapshot, 'T');

    Assert.AreEqual(5u, root.RowIndex);
}

[TestMethod]
public void Find_DescendsFromTheHeaderRootRow_NotRowZero()
{
    using var builder = SyntheticBlockBuilder.MftShaped();
    using var block = builder.OpenForReading(out _)!;
    var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false, rootDirectoryPath: @"T:\");
    var snapshot = Snapshot.Create([driveBlock]);

    var found = LookupEngine.Find(snapshot, @"T:\documents\notes.txt");

    Assert.IsNotNull(found);
    Assert.AreEqual("notes.txt", found.Value.Name);
    Assert.AreEqual(99L, found.Value.Size);
}
```

Both fail today: `Root` returns row 0, which is `$MFT`, and `Find` starts its descent at row 0 and never reaches `documents`.

Add to `MFTLib.Tests/Index/BlockHeaderTests.cs`:

```csharp
[TestMethod]
public void RootRow_SitsAtHeaderOffsetTwenty()
{
    Assert.AreEqual(20, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.RootRow)));
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~LookupEngineTests|FullyQualifiedName~BlockHeaderTests"
```

Expected: FAIL to compile on `BlockHeader.RootRow` and `BuildMftShapedBlock`.

- [ ] **Step 3: Implement**

In `BlockHeader.cs`, replace the field and its comment:

```csharp
/// <summary>
///     Row index of the volume root. An enumeration block writes its root at row 0, so
///     zero is the correct value there and every block written before this field existed
///     already carries it. An MFT block sets 5, the NTFS root directory record, because
///     record 0 is $MFT. The field occupies the padding slot that aligns the 64-bit fields
///     below, so defining it costs no format version bump.
/// </summary>
[FieldOffset(20)] public uint RootRow;
```

Update the type summary, which currently describes offset 20 as padding that must be written as zero.

In `BlockFileCreateOptions.cs` add:

```csharp
/// <summary>
///     Row index of the volume root. Leave at zero for an enumeration producer, which
///     writes its root at row 0; the MFT producer sets 5.
/// </summary>
public uint RootRow { get; init; }
```

In `BlockFile.InitializeHeader`, replace `header.ReservedPadding = 0;` with `header.RootRow = options.RootRow;`.

In `LookupEngine.cs`, `Root` returns `FileEntry.Create(snapshot, driveBlock.DriveOrdinal, driveBlock.Block.Header.RootRow)` and `Find` initializes `var currentRow = driveBlock.Block.Header.RootRow;`.

Update the offset-20 row of the header table in `docs/index-format.md` to `| 20 | root row | u32 | Row index of the volume root; 0 for enumeration, 5 for MFT |`.

- [ ] **Step 4: Run to verify it passes, then the full suite**

Expected: the two new lookup tests and the offset test pass; every existing `MFTLib.Tests.Index` test still passes, because an enumeration block's `RootRow` is 0 and the behavior is unchanged.

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/BlockHeader.cs MFTLib/Index/BlockFileCreateOptions.cs MFTLib/Index/BlockFile.cs MFTLib/Index/LookupEngine.cs MFTLib.Tests/Index/LookupEngineTests.cs MFTLib.Tests/Index/BlockHeaderTests.cs MFTLib.Tests/Index/SyntheticBlockBuilder.cs docs/index-format.md
git commit -m "fix(index): root row is a header field, not an assumed row zero"
```

---

### Task 7: Named block sections

**Files:**
- Create: `MFTLib/Index/NamedBlockSection.cs`
- Test: `MFTLib.Tests/Index/NamedBlockSectionTests.cs`

**Interfaces:**
- Consumes: `BlockFile`, `BlockFileCreateOptions`, `BlockLayout`.
- Produces `public static class MFTLib.Index.NamedBlockSection`, all members `[SupportedOSPlatform("windows")]`:
  - `public static (BlockFile Block, IDisposable Lifetime) Create(BlockFileCreateOptions options, string sectionName)`
  - `public static BlockFile OpenExisting(string sectionName, long expectedLength)`
  - `public static string BuildSectionName(char driveLetter)`

**Design notes:** The client is non-elevated and the broker is elevated, and a page-file-backed map created by the client and opened by the broker is exactly how the cold-scan payload already travels, so the direction is proven. What changes is that the section is backed by the block file rather than the page file, so the broker's writes land straight in the cache file and the cold scan and the cache save are one act. Section names are unqualified, matching `CreateRealDriveMmf`, which puts them in the session-local namespace; an elevated child of the same user in the same session opens them by name. Named map names are a Windows facility and `MemoryMappedFile.CreateFromFile` throws `PlatformNotSupportedException` for a non-null `mapName` on Unix, hence the platform attribute and the guarded tests. This lives in `MFTLib.Index` beside `BlockFile` rather than in the broker because it is block-format plumbing that both sides of the pipe need, and it stays in its own file so `BlockFile.cs` does not grow past the 400-line cap.

`Create` returns the lifetime separately from the block: the `MemoryMappedFile` that owns the section name must outlive the client's own `BlockFile` view only until the broker has opened it, and the client disposes it when the scan completes, exactly as it disposes the page-file map lifetime today.

- [ ] **Step 1: Write the failing test**

Create `MFTLib.Tests/Index/NamedBlockSectionTests.cs` with a Windows guard modeled on `JournalBrokerClientTests`:

```csharp
[TestMethod]
public void CreateThenOpenExisting_SeesTheSameRows()
{
    if (!OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Named memory-mapped sections require Windows.");
        return;
    }

    var sectionName = NamedBlockSection.BuildSectionName('T');
    var options = new BlockFileCreateOptions
    {
        Path = _blockPath,
        VolumeSerial = 0x0BADF00D,
        ProducerKind = ProducerKind.Mft,
        RootRow = 5,
        SlotCapacity = BlockLayout.ComputeSlotCapacity(64),
        NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(1024)
    };

    var (creatorBlock, lifetime) = NamedBlockSection.Create(options, sectionName);
    using (lifetime)
    using (creatorBlock)
    {
        using var openedBlock = NamedBlockSection.OpenExisting(sectionName, creatorBlock.Length);
        var writer = new BlockWriter(openedBlock);
        Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(5, RowFlags.InUse | RowFlags.Directory,
            (uint)FileAttributes.Directory, 0, Moment.Ticks)));

        // The creator's mapping and the opener's mapping are the same pages, so the row
        // the opener wrote is visible through the creator's view with no flush.
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, creatorBlock.Rows[5].Flags);
        Assert.AreEqual(5u, creatorBlock.Header.RootRow);
    }
}

[TestMethod]
public void OpenExisting_UnknownSectionName_ThrowsFileNotFound()
{
    if (!OperatingSystem.IsWindows())
    {
        Assert.Inconclusive("Named memory-mapped sections require Windows.");
        return;
    }

    Assert.ThrowsException<FileNotFoundException>(() =>
        NamedBlockSection.OpenExisting(NamedBlockSection.BuildSectionName('Z'), 4096));
}
```

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL to compile, `NamedBlockSection` does not exist.

- [ ] **Step 3: Implement**

`Create` computes the length with `BlockLayout.TotalBlockBytes`, opens the file with `FileMode.Create`, `FileAccess.ReadWrite`, `FileShare.ReadWrite | FileShare.Delete`, calls `MemoryMappedFile.CreateFromFile(fileStream, sectionName, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false)`, builds the view, and hands both to `BlockFile.BuildAndInitialize` so the header is initialized and the failure unwind (dispose the view and the mapping, delete the file) is the one that already exists. The named `MemoryMappedFile` is the returned lifetime.

`OpenExisting` calls `MemoryMappedFile.OpenExisting(sectionName, MemoryMappedFileRights.ReadWrite)`, creates a view accessor over `expectedLength`, and builds a `BlockFile` over it without initializing or validating the header, because the header is written last by the writer.

`BuildSectionName` returns `"mftlib-block-" + char.ToUpperInvariant(driveLetter) + "-" + Guid.NewGuid().ToString("N")`, mirroring `CreateRealDriveMmf`.

`BlockFile` needs an internal constructor overload that takes an already-built mapping and view without touching the header. Add it next to `BuildAndInitialize` rather than reworking the existing one.

- [ ] **Step 4: Run to verify it passes**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~NamedBlockSectionTests"
```

Expected: PASS, 2 tests on Windows, 2 inconclusive on Linux.

- [ ] **Step 5: Confirm the Linux run stays green without a filter entry**

```bash
bash scripts/coverage-linux.sh
```

Expected: green, with the two tests reported as skipped. Do not add anything to the exclusion filter.

- [ ] **Step 6: Commit**

```bash
git add MFTLib/Index/NamedBlockSection.cs MFTLib/Index/BlockFile.cs MFTLib.Tests/Index/NamedBlockSectionTests.cs
git commit -m "feat(index): named sections over block files"
```

---

### Task 8: The MFT producer seam

**Files:**
- Create: `MFTLib/Index/MftBlockProducer.cs` (the delegate and its request and result records)
- Modify: `MFTLib/Index/FileIndexOptions.cs`
- Test: `MFTLib.Tests/Index/MftBlockProducerContractTests.cs`

**Interfaces:**
- Consumes: `BlockFile`, `ProducerKind`, `IndexScanProgress`.
- Produces:
  - `public delegate Task<MftBlockProduceResult> MftBlockProducer(MftBlockProduceRequest request, CancellationToken cancellationToken);`
  - `public sealed record MftBlockProduceRequest` with `char DriveLetter`, `uint VolumeSerial`, `string BlockPath`, `bool DeleteOnClose`, `IProgress<IndexScanProgress>? Progress`
  - `public sealed record MftBlockProduceResult(BlockFile Block, ulong JournalId, long NextUsn, int SkippedRecordCount, bool CompactionNeeded)`
  - `public MftBlockProducer? FileIndexOptions.MftProducer { get; init; }`

**Design notes:** Spec section 4.1 puts the MFT producer in the elevated broker, and the namespace rule forbids `MFTLib.Index` from referencing `MFTLib.Broker`. A delegate declared in `MFTLib.Index` inverts that dependency: the index states what it needs, the broker supplies it, and `FileIndex`'s producer selection is testable on Linux with a fake that writes a block by hand. This is also where spec section 5.1's "broker launcher" option lands, in the form the boundary allows: the caller closes over its own broker launcher inside the delegate rather than handing `FileIndexOptions` a broker type.

The producer owns creating the block file, because only it knows the volume's record count. It returns the opened `BlockFile` and the armed journal cursor, and `FileIndex` wraps the block in a `DriveBlock` exactly as the enumeration path does. `SkippedRecordCount` is the count of parser records that did not fit and maps onto the same drive-status warning field the enumeration producer's access-denied count uses.

- [ ] **Step 1: Write the failing test**

Create `MFTLib.Tests/Index/MftBlockProducerContractTests.cs`:

```csharp
[TestMethod]
public async Task Producer_ReceivesTheRequestAndReturnsItsBlock()
{
    MftBlockProduceRequest? seen = null;
    using var builder = SyntheticBlockBuilder.MftShaped();

    MftBlockProducer producer = (request, cancellationToken) =>
    {
        seen = request;
        var block = builder.OpenForReading(out _)!;
        return Task.FromResult(new MftBlockProduceResult(block, JournalId: 7, NextUsn: 4096,
            SkippedRecordCount: 0, CompactionNeeded: false));
    };

    var result = await producer(new MftBlockProduceRequest
    {
        DriveLetter = 'C',
        VolumeSerial = 0x0BADF00D,
        BlockPath = builder.BlockPath,
        DeleteOnClose = false,
        Progress = null
    }, CancellationToken.None);

    using (result.Block)
    {
        Assert.AreEqual('C', seen!.DriveLetter);
        Assert.AreEqual(0x0BADF00Du, seen.VolumeSerial);
        Assert.AreEqual(builder.BlockPath, seen.BlockPath);
        Assert.IsFalse(seen.DeleteOnClose);
        Assert.AreEqual(7ul, result.JournalId);
        Assert.AreEqual(5u, result.Block.Header.RootRow);
    }
}

[TestMethod]
public void FileIndexOptions_CarriesTheProducer()
{
    MftBlockProducer producer = (_, _) => throw new NotSupportedException();
    var options = new FileIndexOptions { MftProducer = producer };
    Assert.AreSame(producer, options.MftProducer);
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL to compile, `MftBlockProducer` and `FileIndexOptions.MftProducer` do not exist.

- [ ] **Step 3: Implement**

Create `MFTLib/Index/MftBlockProducer.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     Builds one drive's block from the Master File Table. Declared here rather than in the
///     broker so <see cref="MFTLib.Index" /> states what it needs without referencing the
///     elevated broker that supplies it, which is the namespace boundary the library keeps.
///     A caller closes over its own broker launcher inside the delegate.
/// </summary>
public delegate Task<MftBlockProduceResult> MftBlockProducer(
    MftBlockProduceRequest request, CancellationToken cancellationToken);

/// <summary>
///     What the index needs one drive's block to be. The producer creates the file at
///     <see cref="BlockPath" /> exactly: cache mode against no-cache mode is already resolved
///     by the time this request is built, and <see cref="DeleteOnClose" /> says which one it
///     was.
/// </summary>
public sealed record MftBlockProduceRequest
{
    public required char DriveLetter { get; init; }

    public required uint VolumeSerial { get; init; }

    public required string BlockPath { get; init; }

    public bool DeleteOnClose { get; init; }

    public IProgress<IndexScanProgress>? Progress { get; init; }
}

/// <summary>
///     One finished block plus the journal cursor armed before the scan began, so a watch
///     resumes from before the scan rather than after it and nothing that changed during the
///     scan is lost. <paramref name="SkippedRecordCount" /> counts records the producer could
///     not place, which becomes the drive's warning the same way an enumeration walk's
///     access-denied subtree count does.
/// </summary>
public sealed record MftBlockProduceResult(
    BlockFile Block,
    ulong JournalId,
    long NextUsn,
    int SkippedRecordCount,
    bool CompactionNeeded);
```

Add to `FileIndexOptions`:

```csharp
/// <summary>
///     Supplies MFT-derived blocks. Null means no MFT producer is available, so
///     <see cref="ProducerPolicy.Auto" /> uses enumeration and
///     <see cref="ProducerPolicy.MftOnly" /> is an error.
/// </summary>
public MftBlockProducer? MftProducer { get; init; }
```

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/MftBlockProducer.cs MFTLib/Index/FileIndexOptions.cs MFTLib.Tests/Index/MftBlockProducerContractTests.cs
git commit -m "feat(index): MFT block producer seam on FileIndexOptions"
```

---

### Task 9: FileIndex selects the MFT producer

**Files:**
- Modify: `MFTLib/Index/FileIndex.cs` (drop the `MftOnly` rejection, describe the producer kind), `MFTLib/Index/FileIndex.Scanning.cs`
- Test: `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs`

**Interfaces:**
- Consumes: task 8.
- Produces: no new public surface; `ProducerPolicy.MftOnly` becomes usable and `DriveStatus.WatchSupported` becomes true for MFT-produced drives.

**Design notes:** `FileIndex.OpenAsync` currently throws `NotSupportedException` for `ProducerPolicy.MftOnly` because no MFT producer exists. Selection now reads: `MftOnly` requires `MftProducer` and throws `InvalidOperationException` naming the option when it is null; `Auto` uses `MftProducer` when it is set and the drive's block would be MFT-shaped, and falls back to enumeration otherwise; `EnumerationOnly` ignores `MftProducer` entirely. Under `Auto`, a producer that throws is not fatal: the failure is recorded and the drive falls back to enumeration, because spec section 4 is explicit that no drive is ever unindexed because of its substrate. Under `MftOnly` the exception propagates, because the caller asked for exactly one producer.

`ScanDriveResult` gains the journal cursor so the caller can start a watch from it. `DescribeDrive` already derives `WatchSupported` from `driveBlock.ProducerKind`, so it needs no change once the block carries `ProducerKind.Mft`.

- [ ] **Step 1: Write the failing tests** covering: `MftOnly` with no producer throws and names `MftProducer`; `MftOnly` with a fake producer opens a drive whose `DriveStatus.ProducerKind` is `Mft` and `WatchSupported` is true; `Auto` with a fake producer prefers it; `Auto` with a throwing producer falls back to enumeration and still opens; `EnumerationOnly` with a fake producer ignores it. Each fake writes a small MFT-shaped block, so the tests run on Linux.

- [ ] **Step 2: Run to verify they fail.** Expected: the `MftOnly` cases fail with the current `NotSupportedException`.

- [ ] **Step 3: Implement.** Extract the selection into a private `Task<ScanDriveResult> ProduceDriveBlockAsync(IndexedDrive drive, ushort driveOrdinal, string blockPath, CancellationToken cancellationToken)` in `FileIndex.Scanning.cs` so `AddDriveAsync` stays under the function-length limit.

- [ ] **Step 4: Run to verify they pass, then the full suite on Linux.**

- [ ] **Step 5: Commit**

```bash
git add MFTLib/Index/FileIndex.cs MFTLib/Index/FileIndex.Scanning.cs MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs
git commit -m "feat(index): FileIndex picks the MFT producer when one is supplied"
```

---

## Phase 3: The broker block write path

### Task 10: Block capacity planning from volume information

**Files:**
- Create: `MFTLib/Broker/Client/MftBlockCapacity.cs`
- Test: `MFTLib.Tests/MftBlockCapacityTests.cs`

**Interfaces:**
- Consumes: `NtfsVolumeInformation`, `BlockLayout`.
- Produces `public static class MFTLib.MftBlockCapacity`:
  - `public const uint DefaultAverageNameBytesPerRow = 48;`
  - `public const uint MinimumEstimatedRowCount = 65536;`
  - `public static uint EstimateRowCount(NtfsVolumeInformation? volumeInformation)`
  - `public static (uint SlotCapacity, uint NamePoolCapacity) Plan(NtfsVolumeInformation? volumeInformation, uint averageNameBytesPerRow = DefaultAverageNameBytesPerRow)`

**Design notes:** Spec section 4.1 sizes the block from the slot count in `VolumeInfo` and the name pool from that count times a per-machine average name length with headroom. `NtfsVolumeInformation.MftRecordCount` is already transmitted to the non-elevated client by `QueryVolumesAsync`, so this needs no new round trip: the client's existing capacity-planning hook already queries volumes when a planner is set. A null or degenerate volume information (`MftRecordCount` zero, which the type documents as the unqueried case) falls back to `MinimumEstimatedRowCount`, and the block's own headroom plus the compaction-needed flag absorb an underestimate, exactly as they do for the enumeration producer. Both capacities go through `BlockLayout.ComputeSlotCapacity` and `BlockLayout.ComputeNamePoolCapacity`, so the 25 percent headroom rule lives in one place.

- [ ] **Step 1: Write the failing test**

Create `MFTLib.Tests/MftBlockCapacityTests.cs` covering five cases:

```csharp
[TestMethod]
public void Plan_LargeVolume_UsesTheRecordCountAndTheAverageNameLength()
{
    // 4 million records at 1024 bytes per file record segment.
    var volumeInformation = new NtfsVolumeInformation(
        MftValidDataLength: 4_000_000L * 1024, BytesPerFileRecordSegment: 1024,
        BytesPerSector: 512, BytesPerCluster: 4096, TotalClusters: 0, FreeClusters: 0);

    var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation);

    Assert.AreEqual(BlockLayout.ComputeSlotCapacity(4_000_000), slotCapacity);
    Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(slotCapacity * 48u), namePoolCapacity);
}

[TestMethod]
public void EstimateRowCount_NullVolumeInformation_FallsBackToTheMinimum()
{
    Assert.AreEqual(MftBlockCapacity.MinimumEstimatedRowCount, MftBlockCapacity.EstimateRowCount(null));
}

[TestMethod]
public void EstimateRowCount_UnqueriedSegmentSize_FallsBackToTheMinimum()
{
    // BytesPerFileRecordSegment zero is the type's documented unqueried case, so
    // MftRecordCount is zero and there is nothing to size from.
    var volumeInformation = new NtfsVolumeInformation(1024, 0, 0, 0, 0, 0);
    Assert.AreEqual(MftBlockCapacity.MinimumEstimatedRowCount,
        MftBlockCapacity.EstimateRowCount(volumeInformation));
}

[TestMethod]
public void Plan_HonorsACallerSuppliedAverageNameLength()
{
    var volumeInformation = new NtfsVolumeInformation(1_000_000L * 1024, 1024, 0, 0, 0, 0);
    var (slotCapacity, namePoolCapacity) = MftBlockCapacity.Plan(volumeInformation, 96);
    Assert.AreEqual(BlockLayout.ComputeNamePoolCapacity(slotCapacity * 96u), namePoolCapacity);
}

[TestMethod]
public void EstimateRowCount_RecordCountBeyondThirtyTwoBits_Clamps()
{
    var volumeInformation = new NtfsVolumeInformation(long.MaxValue, 1024, 0, 0, 0, 0);
    Assert.AreEqual(uint.MaxValue, MftBlockCapacity.EstimateRowCount(volumeInformation));
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL to compile, `MftBlockCapacity` does not exist.

- [ ] **Step 3: Implement**

```csharp
public static uint EstimateRowCount(NtfsVolumeInformation? volumeInformation)
{
    var recordCount = volumeInformation?.MftRecordCount ?? 0;
    if (recordCount <= MinimumEstimatedRowCount)
    {
        return MinimumEstimatedRowCount;
    }

    return recordCount > uint.MaxValue ? uint.MaxValue : (uint)recordCount;
}

public static (uint SlotCapacity, uint NamePoolCapacity) Plan(
    NtfsVolumeInformation? volumeInformation,
    uint averageNameBytesPerRow = DefaultAverageNameBytesPerRow)
{
    ArgumentOutOfRangeException.ThrowIfZero(averageNameBytesPerRow);
    var slotCapacity = BlockLayout.ComputeSlotCapacity(EstimateRowCount(volumeInformation));

    // Widened before the multiply and clamped after: a large volume times a generous
    // average name length overflows 32 bits, and a saturated pool plus the block's own
    // compaction-needed flag is the right answer there, not a checked-arithmetic throw
    // on a path whose whole job is estimating.
    var estimatedNameBytes = (ulong)slotCapacity * averageNameBytesPerRow;
    var clamped = estimatedNameBytes > uint.MaxValue / 2 ? uint.MaxValue / 2 : (uint)estimatedNameBytes;
    return (slotCapacity, BlockLayout.ComputeNamePoolCapacity(clamped));
}
```

`BlockLayout.ComputeSlotCapacity` and `ComputeNamePoolCapacity` are `checked`, so the clamp above `uint.MaxValue / 2` is what keeps the headroom addition inside the range rather than throwing.

- [ ] **Step 4: Run to verify it passes.**
- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Client/MftBlockCapacity.cs MFTLib.Tests/MftBlockCapacityTests.cs
git commit -m "feat(broker): plan block capacities from NTFS volume information"
```

---

### Task 11: The output-format field on the ArmAndScan spec

**Files:**
- Modify: `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs` (`ParseScanSpec`, `ScanDriveRequest`)
- Modify: `MFTLib/Broker/Client/JournalBrokerClient.Scan.cs` (`PrepareDriveScan`)
- Create: `MFTLib/Broker/Client/BrokerScanOutputFormat.cs`
- Test: `MFTLib.Tests/BrokerProtocolTests.cs`, `MFTLib.Tests/JournalBrokerHostTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public enum BrokerScanOutputFormat { ScanPayload = 0, Block = 1 }`
  - `public BrokerScanOutputFormat BrokerScanOptions.OutputFormat { get; init; }` defaulting to `ScanPayload`
  - Spec token shape `letter:journalId:nextUsn:sectionName:profile:outputFormat`, the sixth field optional

**Design notes:** The spec token is already a colon-joined, positional, growable list parsed by `ParseScanSpec`, and the watch spec deliberately uses a shorter form. Adding a sixth field with an absent-means-zero default keeps every existing caller and every existing test working, which is what lets the payload path stay green while the block path is built. The section name travels in the fourth field either way: for the payload it is the page-file map name, for a block it is the block file's section name, which is exactly what spec section 4.1 asks for. `ParseScanProfile`'s pattern of rejecting an undefined enumeration value is repeated for the format.

- [ ] **Step 1: Write the failing tests**

Four cases: a five-field token parses with `BrokerScanOutputFormat.ScanPayload`; a six-field token ending in `1` parses as `Block`; a six-field token ending in `7` throws `InvalidDataException` naming the value; `PrepareDriveScan` emits the sixth field from `BrokerScanOptions.OutputFormat`. `ParseScanSpec` is private, so reach it through the existing `internal` seam the current spec tests use, or add an `internal static ScanDriveRequest[] ParseScanSpecForTest(string spec)` passthrough in this task, mirroring `ApplyScanProfile`'s existing internal visibility.

```csharp
[TestMethod]
public void ParseScanSpec_FiveFields_DefaultsToTheScanPayloadFormat()
{
    var requests = JournalBrokerHost.ParseScanSpecForTest("C:0:0:map-name:0");
    Assert.AreEqual(1, requests.Length);
    Assert.AreEqual(BrokerScanOutputFormat.ScanPayload, requests[0].OutputFormat);
}

[TestMethod]
public void ParseScanSpec_SixthFieldOne_SelectsTheBlockFormat()
{
    var requests = JournalBrokerHost.ParseScanSpecForTest("C:0:0:section-name:0:1");
    Assert.AreEqual(BrokerScanOutputFormat.Block, requests[0].OutputFormat);
}

[TestMethod]
public void ParseScanSpec_UndefinedOutputFormat_Throws()
{
    var exception = Assert.ThrowsException<InvalidDataException>(
        () => JournalBrokerHost.ParseScanSpecForTest("C:0:0:section-name:0:7"));
    StringAssert.Contains(exception.Message, "7");
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement**

Create `MFTLib/Broker/Client/BrokerScanOutputFormat.cs` with the two-value enumeration and a doc comment saying the field is positional field six of the scan spec token and that an absent field means `ScanPayload`, which is what keeps every pre-existing caller working.

In `JournalBrokerHost.Scan.cs`, add the field to `ScanDriveRequest`, extend `ParseScanSpec`, and add the parser helper next to `ParseScanProfile`:

```csharp
static BrokerScanOutputFormat ParseScanOutputFormat(string value)
{
    var format = (BrokerScanOutputFormat)int.Parse(value, CultureInfo.InvariantCulture);
    if (!Enum.IsDefined(format))
    {
        throw new InvalidDataException($"Unknown broker scan output format: {value}");
    }

    return format;
}
```

```csharp
yield return new ScanDriveRequest(
    parts[0],
    ulong.Parse(parts[1], CultureInfo.InvariantCulture),
    long.Parse(parts[2], CultureInfo.InvariantCulture),
    parts.Length > 3 ? parts[3] : string.Empty,
    parts.Length > 4 ? ParseScanProfile(parts[4]) : BrokerScanProfile.Full,
    parts.Length > 5 ? ParseScanOutputFormat(parts[5]) : BrokerScanOutputFormat.ScanPayload);
```

In `PrepareDriveScan`, extend the token: `specTokens.Add(FormattableString.Invariant($"{letter}:0:0:{mmfName}:{(int)profile}:{(int)outputFormat}"));`

- [ ] **Step 4: Run to verify they pass, then the full suite.** Every existing broker test still passes: none of them supply a sixth field.
- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Client/BrokerScanOutputFormat.cs MFTLib/Broker/Client/BrokerScanOptions.cs MFTLib/Broker/Client/JournalBrokerClient.Scan.cs MFTLib/Broker/Host/JournalBrokerHost.Scan.cs MFTLib.Tests/BrokerProtocolTests.cs MFTLib.Tests/JournalBrokerHostTests.cs
git commit -m "feat(broker): scan spec carries the output format"
```

---

### Task 12: The broker writes rows into the named section

**Files:**
- Create: `MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs`, `MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs`, `MFTLib/Broker/Host/MftBlockRowWriter.cs`
- Modify: `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs`
- Create: `MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs`
- Test: `MFTLib.Tests/MftBlockRowWriterTests.cs`, `MFTLib.Tests/JournalBrokerHostBlockScanTests.cs`

**Interfaces:**
- Consumes: tasks 2 to 11.
- Produces:
  - `public readonly record struct BlockWriteResult(long RowCount, long NamePoolUsedBytes, long SkippedRecordCount, bool CompactionNeeded)`
  - `public interface IBlockSectionWriter { BlockWriteResult Write(string sectionName, long expectedLength, IEnumerable<IReadOnlyList<MftRecord>> batches, IProgress<MmfWriteProgress>? progress, CancellationToken cancellationToken); }`
  - `public sealed class RealBlockSectionWriter : IBlockSectionWriter`, `[SupportedOSPlatform("windows")]`
  - `public static class MftBlockRowWriter` with `public static BlockWriteResult WriteBatches(BlockWriter writer, IEnumerable<IReadOnlyList<MftRecord>> batches, IProgress<MmfWriteProgress>? progress, CancellationToken cancellationToken)`

**Design notes:** The seam is the whole point of the split. `MftBlockRowWriter` holds the mapping from an `MftRecord` to a `RowColumns` plus a name and is pure block-format logic with no named section in it, so it runs and is fully tested on Linux. `RealBlockSectionWriter` is the thin Windows part that opens the section by name through `NamedBlockSection.OpenExisting` and hands the resulting `BlockWriter` to `MftBlockRowWriter`. `RecordingBlockSectionWriter` is the test double, modeled on `RecordingMmfWriter`: it writes into an ordinary unnamed `BlockFile` in the temp directory and exposes it, so host tests exercise the real row-writing logic without a named section.

Row mapping, per record:

- `rowIndex` is `record.RecordNumber`; a record number past `uint.MaxValue` or past `SlotCapacity` is skipped and counted, and `BlockWriter.TryWriteRow` has already set the compaction-needed flag for the second case.
- `ParentRow` is `(uint)record.ParentRecordNumber`.
- `Flags` is `RowFlags.InUse`, plus `RowFlags.Directory` when `record.IsDirectory`, plus `RowFlags.SizeUnknown` when `!record.SizeKnown`.
- `Attributes` is `(uint)record.FileAttributes`.
- `Size` is `record.Size`, and zero for a directory.
- `ModifiedTicks` is `record.ModifiedUtc.Ticks`.
- The name is `record.FileName`. A record with an empty name is skipped and counted: it has no `$FILE_NAME`, so nothing can be said about it.

The broker calls `volume.ReadRecordBatches(resolvePaths: false, 4096, mftProgress)` in block mode. That is where the `PathLookup` table, the resolve phase, and the `ResolvingPaths` progress phase leave the broker path: with paths off, the native parser never allocates the lookup and never reports that phase, so the progress stream in block mode is parse then transfer.

Memory is bounded by the parse chunk: each batch of 4096 `MftRecord` values is written into the mapped section and dropped before the next arrives. Nothing accumulates.

After the last batch, the broker calls `writer.SetJournalCursor(journalId, nextUsn)` with the cursor armed before the scan, then `writer.Complete(DateTime.UtcNow)`, which stamps the timestamp and sets the complete flag last and flushes. A broker that dies before that leaves a block whose missing complete flag makes `BlockHeader.Validate` reject it, so the client discards and rescans.

`ExecuteDriveScanAsync` branches on `request.OutputFormat`. Keep the branch shallow by extracting the two arms into `ExecutePayloadScanAsync` and `ExecuteBlockScanAsync`; the shared progress reporter and the shared cursor frame stay in the caller. `EmitScanCompletionFramesAsync` writes `ScanReady` with the row count and the name pool used bytes in block mode.

- [ ] **Step 1: Write the failing tests**

`MftBlockRowWriterTests` (runs on Linux): a batch of `MftRecord` values written into a real `BlockFile` produces rows whose parent, attributes, size, modified ticks, and flags match, including a directory carrying size zero and the directory flag, a size-unknown record carrying `RowFlags.SizeUnknown` and size zero, a record whose number exceeds the slot capacity being skipped and counted with the compaction-needed flag set, an empty-name record being skipped and counted, and the returned `BlockWriteResult` reporting the row count and the name pool used bytes from the header.

`JournalBrokerHostBlockScanTests` (runs on Linux, with `RecordingBlockSectionWriter`): an `ArmAndScan` frame carrying `outputFormat` 1 drives the block path, the emitted `ScanReady` carries the row count and the name pool used bytes rather than a payload byte length, no `ScanPayload` bytes are produced, and the block the recorder captured has the complete flag, `ProducerKind.Mft`, `RootRow` 5, and the armed journal cursor in its header.

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `MftBlockRowWriter`.**
- [ ] **Step 4: Implement the seam, the recorder, and the host branch.**
- [ ] **Step 5: Run to verify they pass, then the full managed suite and `coverage-linux.sh`.** Every existing payload-path test still passes: those frames carry no sixth field, so they take the payload arm.
- [ ] **Step 6: Commit**

```bash
git add MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs MFTLib/Broker/Host/MftBlockRowWriter.cs MFTLib/Broker/Host/JournalBrokerHost.Scan.cs MFTLib/Broker/Host/JournalBrokerHost.cs MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs MFTLib.Tests/MftBlockRowWriterTests.cs MFTLib.Tests/JournalBrokerHostBlockScanTests.cs
git commit -m "feat(broker): write index block rows straight into the named section"
```

---

### Task 13: DirectoryIndex as a producer-side in-use filter

**Files:**
- Modify: `MFTLib/Broker/Host/MftBlockRowWriter.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`
- Test: `MFTLib.Tests/MftBlockRowWriterTests.cs`

**Interfaces:**
- Consumes: task 12.
- Produces: `MftBlockRowWriter.WriteBatches` gains a `BrokerScanProfile profile` and an `IReadOnlyCollection<string>? keepFileNames` carried in a `MftBlockRowFilter` record, keeping the parameter count within the limit.

**Design notes:** Spec section 4.1 says `DirectoryIndex` becomes a producer-side filter on which rows are marked in use. In block mode that is simply not writing a row for a filtered-out record: an untouched slot is all zeroes, which reads as not in use, so no separate marking pass exists. Every directory is kept, so the parent column that path building walks is intact for every row that survives. `keepFileNames` is matched case-insensitively against the record's file name, the same `StringComparer.OrdinalIgnoreCase` set `FilterDirectoryIndexBatch` builds today, so git-wizard's `.git` filter keeps working unchanged. Filtered-out records are not counted as skipped: skipping is the capacity and malformed-record signal, and a profile filter is neither.

- [ ] **Step 1: Write the failing tests**: under `Full` every in-use record gets a row; under `DirectoryIndex` with no keep names only directories get rows and a file's slot reads as not in use; under `DirectoryIndex` with `[".git"]` a file named `.GIT` also gets a row; the filtered rows do not increase `SkippedRecordCount`.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass, plus the existing `ApplyScanProfile` tests, which are untouched.**
- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Host/MftBlockRowWriter.cs MFTLib/Broker/Host/JournalBrokerHost.Scan.cs MFTLib.Tests/MftBlockRowWriterTests.cs
git commit -m "feat(broker): directory index profile filters which block rows are written"
```

---

### Task 14: The client creates the block and adopts it

**Files:**
- Create: `MFTLib/Broker/Client/BrokerMftBlockProducer.cs`
- Modify: `MFTLib/Broker/Client/JournalBrokerClient.Scan.cs`, `MFTLib/Broker/Client/JournalBrokerClient.ScanCollector.cs`, `MFTLib/Broker/Client/JournalBrokerClient.cs`
- Test: `MFTLib.Tests/BrokerMftBlockProducerTests.cs`

**Interfaces:**
- Consumes: tasks 7, 8, 10, 11, 12 (named sections, the producer seam, capacity planning, the output format, the broker write path).
- Produces:
  - `public sealed class BrokerMftBlockProducer` with `public MftBlockProducer CreateProducer()`, returning a delegate suitable for `FileIndexOptions.MftProducer`
  - `public IReadOnlyDictionary<string, BlockScanOutcome> BrokerScanResult.BlockOutcomes { get; }` where `BlockScanOutcome` carries the section name, the row count, and the name pool used bytes

**Design notes:** This is the piece that closes the loop: `FileIndex` asks for a block, this class creates the file at the planned size, maps it as a named section, sends `ArmAndScan` with the section name and output format 1, and hands the finished `BlockFile` back. `PrepareDriveScan` gains a block arm that calls `MftBlockCapacity.Plan` and `NamedBlockSection.Create` instead of `createDriveMmf`, storing the same kind of lifetime in `_mmfLifetimes` so the existing take-and-dispose bookkeeping on `ScanReady` and `Error` applies unchanged. `ScanCollector` gains a block arm on `ScanReady` that records the outcome rather than reading records; the record-consumer path is untouched and still runs for payload-mode scans.

Ownership after the scan: the client's own `BlockFile` view of the section is what `FileIndex` keeps, so the producer returns it and disposes only the named-section lifetime once `ScanReady` has arrived. The client validates the header before returning: a block whose complete flag is missing, whose volume serial does not match, or whose row count is zero is discarded and reported as a producer failure, which under `ProducerPolicy.Auto` falls back to enumeration.

- [ ] **Step 1: Write the failing tests** over the in-process pipe harness with a fake scan source and `RecordingBlockSectionWriter`, asserting: the `ArmAndScan` spec the host receives carries a section name and output format 1; the returned `MftBlockProduceResult` carries a block whose header is complete with `ProducerKind.Mft` and `RootRow` 5 and the armed cursor; a broker that returns an incomplete block makes the producer throw with a message naming the validation result; the named-section lifetime is disposed exactly once.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass, then the full managed suite.**
- [ ] **Step 5: Commit**

```bash
git add MFTLib/Broker/Client/BrokerMftBlockProducer.cs MFTLib/Broker/Client/JournalBrokerClient.Scan.cs MFTLib/Broker/Client/JournalBrokerClient.ScanCollector.cs MFTLib/Broker/Client/JournalBrokerClient.cs MFTLib/Broker/Client/BrokerScanResult.cs MFTLib.Tests/BrokerMftBlockProducerTests.cs
git commit -m "feat(broker): client creates the block section and adopts the finished block"
```

---

### Task 15: End-to-end scan, catch-up, and journal mutation

**Files:**
- Test: `MFTLib.Tests/MftProducerEndToEndTests.cs`

**Interfaces:** consumes everything above; produces no production code. If this task needs a production change, that change is a bug fix and gets its own regression test that fails before and passes after.

**Design notes:** The one test that proves the protocol claim in spec section 4.1: catch-up, cursors, watch, `ReplaceWatchCursors`, progress frames, and `BrokerScanProfile` keep their current protocol across the format change. It runs on Linux with `RecordingBlockSectionWriter` and a fake scan source, so pr-crew can verify it.

- [ ] **Step 1: Write the test**

One test method walking the whole sequence: open a `FileIndex` whose `MftProducer` is a `BrokerMftBlockProducer` over an in-process host and a fake scan source that yields a known set of `MftRecord` values; assert the drive is `Ready`, its `ProducerKind` is `Mft`, `WatchSupported` is true, and `Root('C').Path` is `C:\`; assert `Find(@"C:\documents\notes.txt")` resolves and its `Size` and `Modified` match the fake record; feed the catch-up entries the scan returned into `FileIndex.ApplyJournalEntries` and assert a create lands at its record number, a delete tombstones, and a rename publishes the new name; assert the header's journal cursor advanced.

A second method asserts the progress stream in block mode contains `Parsing` and `Transferring` frames and no `ResolvingPaths` frame, which is the observable consequence of dropping path resolution.

A third method asserts `DriveStatus.CompactionNeeded` is true and the state is `Stale` when the fake source yields a record past the planned slot capacity.

A fourth method asserts the watch protocol is untouched: start a `JournalBrokerScanSession` in block mode, park it, call `ReplaceWatchCursors` with the block header's cursor, and assert `WatchCursors` reflects it; then assert `RescanAsync` produces a second block, swaps it into the index, and leaves handles minted from the first snapshot readable.

A fifth method asserts the section 7 warning path survives the format change: a fake catch-up source that throws produces a `Warning` frame and an advanced cursor from a fresh query, and the block itself is still complete and adopted, matching decision 13.

- [ ] **Step 2: Run and fix what it finds.** Expected on the first run: green if tasks 7 to 14 are right, otherwise a real defect. Fix the defect in the owning file with its own regression test.
- [ ] **Step 3: Run `coverage-linux.sh` and the Windows suite.**
- [ ] **Step 4: Commit**

```bash
git add MFTLib.Tests/MftProducerEndToEndTests.cs
git commit -m "test(broker): end-to-end block scan, catch-up, and journal mutation"
```

---

### Task 16: Attended verification on a real drive

**Files:**
- Create: `.claude/scripts/verify-mft-producer.ps1` (deleted at the end of this task unless the owner asks to keep it)
- Modify: none expected.

**This is an attended checkpoint on chonkers and requires elevation.** It cannot run in the crew, in continuous integration, or in a headless subagent. A subagent that reaches this task stops and reports that the checkpoint is pending; the orchestrator runs it with the owner.

**Design notes:** Everything above is proven against fixtures and fakes. This is the first time the block producer touches a real MFT, and it is the gate on the two breaking retirement tasks. Spec section 8's full memory measurement against the 21.3M-file machine belongs to plan 5; this checkpoint is narrower: does the producer produce a correct block for one real volume.

- [ ] **Step 1: Build native and managed in the worktree with the SolutionDir recipe from the build notes, and confirm the loaded DLL timestamp.**

- [ ] **Step 2: Write the verification script**

`.claude/scripts/verify-mft-producer.ps1` opens a `FileIndex` over drive `C` with `ProducerPolicy.MftOnly` and a `BrokerMftBlockProducer`, then prints: the block file path and size on disk; `DriveStatus.RowCount`; `NtfsVolumeInformation.Query("C").MftRecordCount`; the header's `NamePoolUsed` and `NamePoolCapacity`; `CompactionNeeded`; the wall clock of the scan; and for ten named files chosen from `C:\Windows\System32`, the indexed size and modified time beside `Get-Item`'s `Length` and `LastWriteTimeUtc`.

- [ ] **Step 3: Run it elevated and record the results in the task's checklist below**

```powershell
.\.claude\scripts\verify-mft-producer.ps1
```

Acceptance:

- [ ] The scan completes without an error frame and the block validates.
- [ ] `RowCount` is within a few percent of `MftRecordCount` (they differ legitimately: free and extension records get no row).
- [ ] `NamePoolUsed` is below `NamePoolCapacity`, and `CompactionNeeded` is false.
- [ ] All ten spot-checked files match `Get-Item` on both size and modified time, to the second.
- [ ] At least one spot-checked directory reports size 0 and a known size.
- [ ] The block file size on disk matches `BlockLayout.TotalBlockBytes` for the planned capacities.
- [ ] Scan wall clock is within the same order as the current payload-path scan of the same drive; note both numbers.

- [ ] **Step 4: File any mismatch as a defect, fix it with a regression test, and re-run.** A mismatch here is a real bug in tasks 3, 4, or 12 and is fixed there, not papered over in the script.

- [ ] **Step 5: Delete the script and commit the sign-off**

```bash
git rm .claude/scripts/verify-mft-producer.ps1
git commit --allow-empty -m "chore(broker): real-drive verification of the block producer"
```

The commit message body records the measured row count, the volume record count, the name pool usage, and the wall clock, so the numbers survive in git history rather than only in a session transcript.

---

## Phase 4: Retirement

**Both tasks in this phase are breaking for file-wizard and git-wizard, which consume MFTLib through a source submodule and still use `ScanPayload`. They start only after task 16 is signed off. Both consumers pin the commit task 16 produced until plans 3 and 4 land their ports.**

### Task 17: Retire the scan payload write and read path

**Files:**
- Modify: `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs`
- Modify: `MFTLib/Broker/Client/JournalBrokerClient.Scan.cs`, `MFTLib/Broker/Client/JournalBrokerClient.ScanCollector.cs`, `MFTLib/Broker/Client/JournalBrokerClient.Connection.cs`, `MFTLib/Broker/Client/BrokerScanOptions.cs`, `MFTLib/Broker/Client/BrokerScanOutputFormat.cs` (deleted)
- Delete: `MFTLib/Broker/SharedMemory/IMmfWriter.cs`, `IMmfReader.cs`, `RealMmfWriter.cs`, `RealMmfReader.cs`
- Modify: `MFTLib/Broker/Protocol/BrokerFrame.cs`, `MFTLib/Broker/Protocol/BrokerProtocol.cs`, `BrokerProtocol.Write.cs`
- Modify: the tests that name the deleted types

**Interfaces:**
- Removed: `IMmfWriter`, `IStreamingMmfWriter`, `IMmfReader`, `IStreamingMmfReader`, `RealMmfWriter`, `RealMmfReader`, `MmfWriteResult`, `BrokerScanOutputFormat`, `BrokerScanOptions.ConsumeRecords`, `BrokerScanOptions.MmfCapacityBytes`, `BrokerScanOptions.MmfCapacityPlanner`, `JournalBrokerClient.DefaultMmfCapacity`, `JournalBrokerClient.DefaultCapacityPlanner`, `BrokerScanPhase.ResolvingPaths`, the sixth spec field.
- Changed: `JournalBrokerClient`'s constructor takes `Func<string, BlockFileCreateOptions, (string SectionName, IDisposable Lifetime)> createDriveBlockSection` in place of `IMmfReader mmfReader` and `Func<string, long, (string, IDisposable)> createDriveMmf`. `BrokerFrame.RecordCount` and `BrokerFrame.ByteLength` on the `ScanReady` path become `RowCount` and `NamePoolUsedBytes`; the `VolumeInfo` frame keeps `RecordCount` as its own field.

**Design notes:** Block output is now the only mode, so the output-format field and the whole payload arm go together. `BrokerScanPhase.ResolvingPaths` goes with them: no producer reports it any more, and leaving a phase in the enumeration that nothing emits is exactly the kind of dead surface a consumer would wire a progress label to. `MmfWriteProgress` stays: it is the progress carrier the block writer already uses, and only its `Phase` default and its name are payload-flavored; rename it to `BlockWriteProgress` in this task and keep its shape. `BrokerScanOptions.KeepFileNames`, `Profile`, and `Progress` all stay.

- [ ] **Step 1: Write the failing test.** Add one test asserting `BrokerScanPhase` has exactly two defined values, `Parsing` and `Transferring`, and one asserting the `ScanReady` frame round-trips a row count and a name pool used byte count through the renamed properties. Both fail against the current surface.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Delete the payload seam types and the payload arm**, then follow the compiler to every call site. The `superpowers:effective-refactor` skill applies: this is a mass API removal and hand-editing each call site is the failure mode.
- [ ] **Step 4: Update the tests that named the deleted types.** `JournalBrokerHostTests.RealMmfWriter_WritesPayload_UiCanReadItBack` and `JournalBrokerClientTests.ArmScanAndCatchUpAsync_ReturnsRecords_ArmedCursor_AndCatchUpEntries` become block equivalents; remove their entries from the Linux exclusion filter in `scripts/coverage-linux.sh` if their replacements are Linux-clean, and leave the entries alone otherwise.
- [ ] **Step 5: Run the full managed suite on Windows and `coverage-linux.sh`.**
- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(broker): block output is the only scan output"
```

---

### Task 18: Delete ScanPayload and ScanRecord

**Files:**
- Delete: `MFTLib/Broker/SharedMemory/ScanPayload.cs`, `MFTLib/Broker/SharedMemory/MmfWriteProgress.cs` (superseded by the renamed carrier), `MFTLib.Tests/ScanPayloadTests.cs`, `MFTLib.Tests/TestSupport/RecordingMmfWriter.cs`
- Modify: `MFTLib/Broker/Sources/DriveScanSource.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs` (`ToScanRecords`, `ArmAndScan`, `ApplyScanProfile`), `MFTLibTestExtensions/ScanSessionTestHarness.cs`, `MFTLib/Broker/Client/JournalBrokerScanSession.*.cs`

**Interfaces:**
- Removed: `ScanRecord`, `ScanPayload`, `ScanRecordBatchConsumer`, `DriveScanSource`, `StreamingDriveScanSource`, `JournalBrokerHost.ArmAndScan`, `JournalBrokerHost.ApplyScanProfile`, the record-consumer overloads on `JournalBrokerScanSession.StartAsync` and `RescanAsync`.
- Kept: `ProgressStreamingDriveScanSource`, retyped to yield `IReadOnlyList<MftRecord>` batches.

**Design notes:** The 48-byte record format and its `ScanRecord` carrier exist only to move records through a page-file map, and after task 17 nothing does that. `ProgressStreamingDriveScanSource` survives because the host still needs an injectable scan source for its tests; it now yields the parser's own `MftRecord` batches, which removes the mapping step entirely. `JournalBrokerScanSession`'s record-consumer overloads go with the consumer delegate, and `ScanSessionTestHarness` follows.

- [ ] **Step 1: Write the failing test.** A test asserting that `typeof(JournalBrokerHost).Assembly.GetType("MFTLib.ScanPayload")` is null, which fails while the type exists. Delete it in step 5 once the deletion is done; it exists only to make the removal test-driven and would be a permanent assertion about absence otherwise.
- [ ] **Step 2: Run to verify it fails.**
- [ ] **Step 3: Delete the types and follow the compiler**, retyping `ProgressStreamingDriveScanSource` and every fake scan source in the tests.
- [ ] **Step 4: Update `MFTLibTestExtensions`.** It ships as its own NuGet package and its public surface changes; the change is recorded in `CHANGELOG.md` by task 19.
- [ ] **Step 5: Delete the absence test and run both suites.**
- [ ] **Step 6: Confirm nothing dangles**

```bash
grep -rn "ScanPayload\|ScanRecord\|IMmfWriter\|IMmfReader" --include=*.cs .
```

Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(broker): delete the scan payload format and its record carrier"
```

---

## Phase 5: Documentation and the gate

### Task 19: Documentation

**Files:**
- Modify: `docs/index-format.md`, `docs/broker-integration.md`, `AGENTS.md`, `CHANGELOG.md`
- Modify: inline doc comments wherever the docs-update check finds drift

**Design notes:** The README paragraph on the not-just-MFT scope and the two consumer READMEs belong to plan 5. This task fixes what this plan made untrue.

- [ ] **Step 1: `docs/index-format.md`.** The offset-20 root-row row is already in from task 6; add a short "MFT blocks" section beside the existing "Enumeration blocks" section covering: rows are dense by NTFS record number, the root row is 5, a size-unknown row carries `RowFlags.SizeUnknown` with a zero size and means the record's data attribute lives in an extension record, and the block is written directly by the elevated broker through a named section the client created.

- [ ] **Step 2: `docs/broker-integration.md`.** Replace the `ScanPayload` material with the block write path: the client plans capacities from `NtfsVolumeInformation`, creates the block file and its named section, passes the section name in the scan spec, and receives a `ScanReady` carrying the row count and the name pool used bytes; the broker opens the section and writes rows as parse chunks arrive; the header is written last with the complete flag and a crash leaves a block the client discards. Update the "Sizing the scan map" section to "Sizing the block", and the "Testing your integration" section to name `RecordingBlockSectionWriter`. Every code sample in the file that names a deleted type must be rewritten or removed; grep the file for `ScanRecord`, `MmfCapacity`, and `ConsumeRecords` and leave none behind.

- [ ] **Step 3: `AGENTS.md`.** The Architecture section's MFTLib bullet says results cross the boundary through a versioned compact interface with packed rows plus string pools "with an allocation-failure fallback that preserves raw entries and filenames if path resolution cannot allocate". Update it to name interface version 4, the 48-byte entry with size and modified time, and the fact that the broker's block path parses without path resolution. Add one sentence to the VolumeBroker bullet saying the cold scan is written straight into a client-created block section rather than a page-file map.

- [ ] **Step 4: `CHANGELOG.md`.** Add an unreleased section with a Breaking Changes list naming every removed type from tasks 17 and 18, the `JournalBrokerClient` constructor change, the `MFTLibTestExtensions` surface change, and the native interface bump from 3 to 4; and a Features list naming `MftRecord.Size`, `MftRecord.SizeKnown`, `MftRecord.ModifiedUtc`, `BlockHeader.RootRow`, `FileIndexOptions.MftProducer`, and the block write path.

- [ ] **Step 5: Run the docs-update check.** Read `README.md`, `CLAUDE.md`, and every file under `docs/` and look for statements this branch made untrue. Fix what drifted; do not add plan 5's material.

- [ ] **Step 6: Verify every new public type carries a doc comment**

```bash
grep -L "/// <summary>" MFTLib/Index/*.cs MFTLib/Broker/Host/*.cs MFTLib/Broker/Client/*.cs MFTLib/Broker/SharedMemory/*.cs
```

Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add docs/ AGENTS.md CHANGELOG.md MFTLib/
git commit -m "docs(broker): block write path replaces the scan payload"
```

---

### Task 20: Coverage and quality gate

**Files:** any file the gate flags.

- [ ] **Step 1: Verify the namespace boundary**

```bash
grep -rn "MFTLib\.Mft\|MFTLib\.Broker\|MFTLib\.Internal\|MFTLib\.Interop" MFTLib/Index/
grep -rln "MftRecord\|MftResult\|JournalBroker\|BrokerFrame\|MftVolume\|MFTLibNative" MFTLib/Index/
```

Expected: no output from either. The MFT producer seam is a delegate declared in `MFTLib.Index`; if `MftRecord` appears under `MFTLib/Index/`, decision 5 was violated and the type belongs in `MFTLib/Broker/`.

- [ ] **Step 2: Verify no file exceeds the size limit**

```bash
wc -l MFTLib/Index/*.cs MFTLib/Broker/**/*.cs MFTLib.Tests/*.cs MFTLib.Tests/Index/*.cs MFTLibNative/mft/*.cpp MFTLibNative/*.h | awk '$2 != "total" && $1 > 400 {print $1, $2}'
```

Expected: no output, except any file that was already over the limit before this branch. `.aislop/config.yml` sets `quality.maxFileLoc: 400`; split by responsibility, not by line count.

- [ ] **Step 3: Run the full managed suite on Windows**

```powershell
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -p:Platform=x64 --filter "TestCategory!=RequiresAdmin"
```

- [ ] **Step 4: Run coverage on both platforms**

```powershell
.\scripts\run-coverage.ps1 -NonInteractive
```

```bash
bash scripts/coverage-linux.sh
```

Expected: both green. Nothing this plan added may appear in the Linux exclusion filter in `scripts/coverage-linux.sh`; a Windows-only test guards with `OperatingSystem.IsWindows()` and `Assert.Inconclusive` instead. If a test was excluded to make the run pass, that is a defect in the test.

- [ ] **Step 5: Run the native smoke test on Linux**

```bash
LD_LIBRARY_PATH=build/linux ./build/linux/test/linux_smoke_test
```

Expected: 19 passed, 0 failed.

- [ ] **Step 6: Run the aislop gate**

```bash
aislop scan .
aislop ci .
```

Expected: score 100, exit code 0. `ci.failBelow` is 100, so any finding fails the build. Fix the underlying issue; never disable a rule and never edit `.aislop/config.yml`. The C++ tree is scored at a suggestion floor by ReSharper C++ alongside cppcheck and clang-tidy, so the native tasks are as likely to raise findings as the managed ones: expect narrative comments that restate the code, a function past 80 lines in the reworked attribute walk (extract a named helper), and nesting past 5 (invert a condition and return early).

- [ ] **Step 7: Confirm the tree is clean**

```bash
git status --porcelain
```

Expected: no output.

- [ ] **Step 8: Commit any gate fixes**

```bash
git add -A
git commit -m "chore(broker): coverage and aislop gate pass"
```

---

## Plan completion

When every task is checked, the branch delivers a native parser that reads real sizes and modified times behind interface version 4, a root-row header field that fixes issue 116, an elevated broker that writes packed index blocks straight into a client-created named section with no name table and no path pool, and a retired `ScanPayload`. Task 16's commit is the pin for file-wizard and git-wizard until plans 3 and 4 land their ports.

Plan 3 begins from here: the file-wizard port and the deletion of its old index and cache. The spec at `docs/superpowers/specs/2026-09-02-packed-index-design.md` stays in place for plans 3 to 5 and is deleted when plan 5 consumes it.

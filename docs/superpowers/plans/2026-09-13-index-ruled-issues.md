# Index Ruled Issues Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five packed-index design issues whose owner rulings are final (MFTLib#145, #143, #146, #144, #118) so a consumer can release block files deterministically, read and look up real filesystem paths on any platform, tell a warm-started drive from a freshly scanned one, enumerate its own block cache, and have the `MFTLib.Index` namespace boundary enforced by a test rather than by convention.

**Architecture:** All five changes land in the existing single `MFTLib` assembly and the existing `MFTLib.Index` namespace, plus tests in `MFTLib.Tests`. Nothing new is layered underneath: `#145` reaches an already-working release primitive (`DriveBlock.Release`, `BlockFile.Dispose`) instead of building one, `#143` promotes the already-private `FileEntry.ResolveRealPath` conversion into the one path builder everything shares, `#146` adds one enum field to `DriveStatus` fed by a per-ordinal dictionary in the same shape as the four that already exist on `FileIndex`, `#144` adds the inverse of `CacheDirectory.BlockFileName` next to the formatter it must never disagree with, and `#118` adds an IL-level architecture test over the built assembly with a mandatory negative control.

**Tech Stack:** C# on net10.0, MSTest 3.1.1, coverlet.msbuild 6.0.4, Roslynator.Analyzers 4.15.0, `TngTech.ArchUnitNET` 0.13.4 plus `TngTech.ArchUnitNET.MSTestV2` 0.13.4 (test project only). Build and coverage through `scripts/coverage-linux.sh` on Linux and `scripts/run-coverage.ps1` on Windows. Quality gate is `aislop scan .`.

**Base commit:** `5462650` on branch `feat/index-ruled-issues` in the worktree `~/MFTLib-worktrees/ruled-issues`. Every file path and line anchor below was read at that commit.

**Rulings are final.** Each task implements exactly what its issue's owner ruling says. Do not re-open a decision, soften it, or add an option the ruling did not name. If a ruling and this plan disagree, the ruling wins and the plan is the defect: report it rather than choosing.

## Global Constraints

Every task's requirements implicitly include this section. Implementers and reviewers both read it.

- No em-dashes anywhere: prose, code, comments, commit messages.
- Full-word identifiers, no abbreviations (`maximum` not `max`, `arguments` not `args`, `configuration` not `config`).
- Files under about 500 lines. This repository's aislop gate is stricter and binding: `.aislop/config.yml` sets `quality.maxFileLoc: 400`, `maxFunctionLoc: 80`, `maxNesting: 5`, `maxParams: 6`. Keep new and edited files under 400 lines and new methods under 80.
- No hard-coded machine paths. Derive from `Path.GetTempPath()`, the test's own fixture root, or an argument.
- Every bug fix and every new behaviour has a test that fails before the change and passes after. Both states are observed and recorded, not assumed.
- Never assert on wall-clock time. No `sleep` followed by an elapsed-time assertion, no subtraction of two clock reads inside an assertion.
- The coverage and lint gate is whatever `TEST-REPORT.md` says it is. Quoting it verbatim:

  ```
  dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter "TestCategory!=RequiresAdmin" -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../coverage/mftlib-tests-coverage.xml
  aislop scan . --json
  ```

  and its two gate rows: `Coverage | 4580/4687 lines (97.71%), 1481/1552 branches (95.42%). Every changed executable production line was covered.` and `Lint | aislop 0.16.0: 99/100, 0 errors, 2 inherited warnings, 7 inherited informational findings. Zero findings in changed files.` The bar a task must meet is the second half of each: every changed executable production line covered, and zero findings in changed files.
- Lanes do not run jb/inspectcode/aislop/dotnet format. The controller runs those gates.
- Lanes never commit anything under `.superpowers/`.
- Commit trailer format for lanes is `Co-Authored-By: <harness> <model> <noreply@anthropic.com or the harness's address>`.

## File Structure

New files:

- `MFTLib/Index/BlockSource.cs` - the `BlockSource` enum (Task 5).
- `MFTLib/Index/CachedBlockFile.cs` - the record one cache enumeration row returns (Task 6).
- `MFTLib.Tests/Index/FileEntryDisposalTests.cs` - `#145` handle-contract tests (Task 1).
- `MFTLib.Tests/Index/FileIndexBlockReleaseTests.cs` - `#145` block-file-release acceptance (Task 2).
- `MFTLib.Tests/TestSupport/BlockFileHoldAssertions.cs` - the platform-conditional "is this file still held" helper (Task 2).
- `MFTLib.Tests/Index/TestDriveRoot.cs` - host-shaped root directory strings for synthetic blocks (Task 3).
- `MFTLib.Tests/Index/CacheDirectoryEnumerationTests.cs` - `#144` tests (Task 6).
- `MFTLib.Tests/Index/NamespaceBoundaryTests.cs` - `#118` boundary test and negative control (Task 7).
- `MFTLib.Tests/Index/NamespaceBoundaryViolationFixture.cs` - the deliberate violator (Task 7).

Modified: `MFTLib/Index/Snapshot.cs`, `FileEntry.cs`, `FileEntry.Navigation.cs`, `FileEntry.Open.cs`, `FileIndex.cs`, `FileIndex.Queries.cs`, `FileIndex.Scanning.cs`, `FileIndex.Rescan.cs`, `IndexNavigation.cs`, `LookupEngine.cs`, `DriveStatus.cs`, `CacheDirectory.cs`, `MFTLib.Tests/MFTLib.Tests.csproj`, a set of existing test files named per task, `CHANGELOG.md`, `AGENTS.md`, `README.md`, `docs/superpowers/specs/2026-09-02-packed-index-design.md`.

---

## Phase 1: MFTLib#145 - disposal always unmaps, a stale handle throws

The ruling in full, so no implementer has to fetch it:

- `FileIndex.DisposeAsync` releases every snapshot it holds, current and retired, unconditionally. No opt-in flag, no caller promise. The `HasExposedHandles` skip at `FileIndex.cs:181-188` goes away.
- `Snapshot` gains a released state that is observable from a handle. Every `FileEntry` property read (`Name`, `Size`, `SizeKnown`, `Modified`, `Attributes`, `IsDirectory`, `IsDeleted`, `Id`, `Path`, `Parent`, `Children()`, `Open()`) checks it first and throws `ObjectDisposedException`. One volatile read per access on a path that already does a pointer chase.
- New `FileEntry.IsDisposed`: true once the owning index has been disposed and the mapping released. `IsValid` is unchanged (false only for the default struct value) and `IsDeleted` is unchanged (tombstone row). Three properties, three different questions.
- The finalizer path during the index's lifetime is unchanged: a rescan still retires the old snapshot and keeps it mapped until handles minted before the rescan are unreachable. Only disposal becomes forceful.
- `Snapshot.ReleaseNow` stays internal.
- The check-then-read race is a consumer bug, documented on `DisposeAsync`, not a library guarantee.

### Task 1: Snapshot released state, FileEntry.IsDisposed, and throwing reads

**Files:**
- Modify: `MFTLib/Index/Snapshot.cs:15` (the existing `int _releaseState` field), `:23` (`HasExposedHandles`, the shape to copy), `:113-134` (`ReleaseNow` and `ReleaseCore`), `:5-11` (the type doc)
- Modify: `MFTLib/Index/FileEntry.cs:40-41` (the internal `Snapshot` property), `:52-53` (`IsValid`), `:78-81` (`ToString`)
- Modify: `MFTLib/Index/FileEntry.Navigation.cs:15` (`Path` doc), `:18` (`Parent` doc), `:35` (`Children` doc)
- Modify: `MFTLib/Index/FileEntry.Open.cs:10` (`Open` doc)
- Create: `MFTLib.Tests/Index/FileEntryDisposalTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `Snapshot.IsReleased` (`internal bool`), `FileEntry.IsDisposed` (`public bool`). Task 2 asserts on `FileEntry.IsDisposed`; every later task's tests keep working because the throw only happens after a release.

**Design decisions this task locks, all derived from the ruling:**

1. The released state is a volatile read of the existing `_releaseState` field. `ReleaseCore` already sets it with `Interlocked.Exchange(ref _releaseState, 1)` at `Snapshot.cs:123` before it releases any block, so `IsReleased` becomes true at the start of the release, which is the conservative order: a reader is turned away before the first unmap rather than after it.
2. The twelve member checks are implemented once, in the internal `FileEntry.Snapshot` property at `FileEntry.cs:40-41`, because all twelve already route through it. `Id`, `Name`, `Size`, `SizeKnown`, `Modified`, `Attributes`, `IsDirectory` and `IsDeleted` reach it through `DriveBlock` (`FileEntry.cs:47`) and `Row` (`FileEntry.cs:49-50`); `Path`, `Parent` and `Children()` pass it directly (`FileEntry.Navigation.cs:15,27,37`); `Open` reaches it through `DriveBlock` (`FileEntry.Open.cs:12`). One volatile read per access, exactly as the ruling specifies. Do not scatter twelve copies of the check.
3. `ToString()` is not in the ruling's list and must not throw: a `ToString` that throws is a debugger hazard. It reports `"<disposed FileEntry>"` when `IsDisposed`, keeping its existing `"<invalid FileEntry>"` for the default value.
4. `IsDisposed` is false for the default struct value. `IsValid` answers "does this handle reference a snapshot at all", `IsDisposed` answers "has that snapshot been released", `IsDeleted` answers "is this row a tombstone". Three questions, three answers.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/FileEntryDisposalTests.cs`:

```csharp
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The handle contract after a snapshot is released: every documented read throws
///     <see cref="ObjectDisposedException" />, <see cref="FileEntry.IsDisposed" /> reports it,
///     and <see cref="FileEntry.IsValid" /> keeps answering its own separate question.
/// </summary>
[TestClass]
public class FileEntryDisposalTests
{
    static readonly DateTime Moment = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    FileEntry _entry;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder();
        var root = _builder.AddRoot();
        var documents = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = _builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: TestDriveRoot.For('T'))]);
        _entry = FileEntry.Create(_snapshot, 0, readme);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }

    [TestMethod]
    public void IsDisposed_IsFalseWhileTheSnapshotIsLive()
    {
        Assert.IsFalse(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid);
    }

    [TestMethod]
    public void IsDisposed_IsTrueOnceTheSnapshotIsReleased()
    {
        _snapshot.ReleaseNow();

        Assert.IsTrue(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid, "IsValid answers whether the handle references a snapshot, not whether it is live");
    }

    [TestMethod]
    public void IsDisposed_IsFalseForTheDefaultValue()
    {
        var defaultEntry = default(FileEntry);

        Assert.IsFalse(defaultEntry.IsValid);
        Assert.IsFalse(defaultEntry.IsDisposed);
    }

    [TestMethod]
    public void EveryDocumentedRead_ThrowsOnceTheSnapshotIsReleased()
    {
        _snapshot.ReleaseNow();
        var entry = _entry;

        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Name);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Size);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.SizeKnown);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Modified);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Attributes);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.IsDirectory);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.IsDeleted);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Id);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Path);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Parent);
        Assert.ThrowsException<ObjectDisposedException>(() => entry.Children());
        Assert.ThrowsException<ObjectDisposedException>(() => entry.Open(FileAccess.Read));
    }

    [TestMethod]
    public void ToString_DoesNotThrowOnADisposedHandle()
    {
        _snapshot.ReleaseNow();

        Assert.AreEqual("<disposed FileEntry>", _entry.ToString());
        Assert.AreEqual("<invalid FileEntry>", default(FileEntry).ToString());
    }
}
```

Note: this test file references `TestDriveRoot.For`, which Task 3 creates. Until Task 3 lands, use the literal `rootDirectoryPath: @"T:\"` here and change it to `TestDriveRoot.For('T')` as part of Task 3's sweep. Whichever form is in the tree must compile.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileEntryDisposalTests" --logger "console;verbosity=minimal"`

Expected: compile failure, `'FileEntry' does not contain a definition for 'IsDisposed'`. That is the RED state for the three `IsDisposed` tests. To see `EveryDocumentedRead_ThrowsOnceTheSnapshotIsReleased` fail as a test rather than a compile error, comment out the three `IsDisposed` tests and re-run: several of the twelve assertions pass incidentally today (the reads that touch `BlockFile.Rows` hit `BlockFile`'s own `ObjectDisposedException.ThrowIf(_disposed, this)` at `BlockFile.cs:61`), and the test as a whole fails on the first read that does not. Record which ones failed. Uncomment before implementing.

- [ ] **Step 3: Add the released state to Snapshot**

In `MFTLib/Index/Snapshot.cs`, beside `HasExposedHandles` at line 23:

```csharp
    /// <summary>
    ///     True from the moment a release begins, before the first block is unmapped, so a
    ///     reader is turned away rather than racing the unmap. Set by <see cref="ReleaseCore" />
    ///     on both the <see cref="ReleaseNow" /> path and the finalizer path.
    /// </summary>
    internal bool IsReleased => Volatile.Read(ref _releaseState) != 0;
```

Update the type doc at `Snapshot.cs:5-11` so it no longer says the finalizer is the release path for snapshots in general. The finalizer remains the release path for a snapshot a rescan retired while the index lives; `FileIndex.DisposeAsync` now releases every snapshot it holds. Keep the `CA1816` suppression justification at `:108-112` truthful: `ReleaseNow` stays internal, and disposal is what reaches it on the consumer's behalf.

- [ ] **Step 4: Make the single check point throw, and add IsDisposed**

In `MFTLib/Index/FileEntry.cs`, replace the internal `Snapshot` property at lines 40-41:

```csharp
    /// <summary>
    ///     The snapshot this handle reads through. Every public member routes here, which is why
    ///     the released check lives here and nowhere else: one volatile read per access, on a path
    ///     that already does a pointer chase.
    /// </summary>
    internal Snapshot Snapshot
    {
        get
        {
            var snapshot = _snapshot ?? throw new InvalidOperationException(
                "This FileEntry is the default value and does not reference a snapshot.");
            ObjectDisposedException.ThrowIf(snapshot.IsReleased, typeof(FileEntry));
            return snapshot;
        }
    }
```

Beside `IsValid` at lines 52-53:

```csharp
    /// <summary>
    ///     True once the snapshot behind this handle has been released, which happens when the
    ///     owning <see cref="FileIndex" /> is disposed. Every read on this handle throws
    ///     <see cref="ObjectDisposedException" /> from that point on. False for the default value,
    ///     which references no snapshot at all: <see cref="IsValid" /> is the question for that,
    ///     and <see cref="IsDeleted" /> is the question for a tombstoned row. Three properties,
    ///     three different questions.
    /// </summary>
    public bool IsDisposed => _snapshot is not null && _snapshot.IsReleased;
```

Replace `ToString` at lines 78-81:

```csharp
    public override string ToString()
    {
        if (!IsValid)
        {
            return "<invalid FileEntry>";
        }

        return IsDisposed ? "<disposed FileEntry>" : $"{Name} ({Id})";
    }
```

- [ ] **Step 5: Document the throw on the twelve members**

Add one `<exception cref="ObjectDisposedException">The owning index has been disposed and this handle's snapshot released.</exception>` line to the doc comments of `Path` (`FileEntry.Navigation.cs:15`), `Parent` (`:18`), `Children()` (`:35`) and `Open` (`FileEntry.Open.cs:10`). For the eight one-line properties in `FileEntry.cs:55-76` that carry no doc comment today, add a single remark to the type doc at `FileEntry.cs:5-18` rather than eight repeated blocks, saying that every property read throws `ObjectDisposedException` once the owning index has been disposed. Keep the existing remarks paragraph at `:13-18` truthful: a held handle no longer stays readable after `DisposeAsync`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileEntryDisposalTests" --logger "console;verbosity=minimal"`
Expected: PASS, 5 tests.

- [ ] **Step 7: Fix the one existing test this changes**

`MFTLib.Tests/Index/FileIndexLifetimeTests.cs:196-215`, `DisposeAsync_AHeldFileEntryRemainsReadable`, asserts the exact behaviour the ruling reverses. It is Task 2's subject, not Task 1's: Task 1 leaves `FileIndex.DisposeAsync` alone, so the snapshot is only detached and not released, `IsReleased` is false, and this test still passes. Confirm that by running it:

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileIndexLifetimeTests" --logger "console;verbosity=minimal"`
Expected: PASS, unchanged. If it fails, something in Step 4 released a snapshot it should not have.

- [ ] **Step 8: Add the CHANGELOG entry**

Under `## Unreleased` / `### Added` in `CHANGELOG.md`:

```markdown
- `FileEntry.IsDisposed` reports that the handle's snapshot has been released, distinct from `IsValid` (the default struct value) and `IsDeleted` (a tombstoned row); every read on a disposed handle throws `ObjectDisposedException` ([MFTLib#145](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/145))
```

- [ ] **Step 9: Commit**

```bash
git add MFTLib/Index/Snapshot.cs MFTLib/Index/FileEntry.cs MFTLib/Index/FileEntry.Navigation.cs MFTLib/Index/FileEntry.Open.cs MFTLib.Tests/Index/FileEntryDisposalTests.cs CHANGELOG.md
git commit -F - <<'EOF'
feat(index): a released snapshot makes every FileEntry read throw (#145)

Snapshot gains an internal IsReleased volatile read, set by ReleaseCore
before the first unmap. FileEntry routes every public member through its
internal Snapshot property, so one check there covers Name, Size,
SizeKnown, Modified, Attributes, IsDirectory, IsDeleted, Id, Path, Parent,
Children() and Open(). New FileEntry.IsDisposed answers the third of the
three questions IsValid and IsDeleted already answer.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

### Task 2: FileIndex.DisposeAsync releases every snapshot unconditionally

**Files:**
- Modify: `MFTLib/Index/FileIndex.cs:145-204` (`DisposeAsync` and its doc), specifically the `HasExposedHandles` skip at `:180-188`
- Modify: `MFTLib/Index/FileIndex.Rescan.cs:349-364` (`ReleaseAllRetiredSnapshots` and its doc)
- Create: `MFTLib.Tests/TestSupport/BlockFileHoldAssertions.cs`
- Create: `MFTLib.Tests/Index/FileIndexBlockReleaseTests.cs`
- Modify: `MFTLib.Tests/Index/FileIndexLifetimeTests.cs:195-215`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: `Snapshot.IsReleased` and `FileEntry.IsDisposed` from Task 1.
- Produces: `BlockFileHoldAssertions.AssertNotHeld(string blockPath)`, used by no later task but available.

- [ ] **Step 1: Write the failing acceptance test**

Create `MFTLib.Tests/TestSupport/BlockFileHoldAssertions.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Asserts that this process no longer holds a block file open. The question is the same on
///     both platforms and the only reliable answer differs: Linux reads the process's own
///     descriptor table, Windows asks the operating system for an exclusive handle, which the
///     live mapping's <see cref="FileShare.ReadWrite" /> handle refuses.
/// </summary>
public static class BlockFileHoldAssertions
{
    public static void AssertNotHeld(string blockPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(blockPath);
        Assert.IsFalse(IsHeldByThisProcess(blockPath),
            $"{blockPath} is still open after everything that owns it was disposed");
    }

    static bool IsHeldByThisProcess(string blockPath)
    {
        return OperatingSystem.IsWindows()
            ? !CanOpenExclusively(blockPath)
            : AppearsInProcessDescriptorTable(blockPath);
    }

    static bool CanOpenExclusively(string blockPath)
    {
        try
        {
            using var probe = new FileStream(blockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static bool AppearsInProcessDescriptorTable(string blockPath)
    {
        var target = Path.GetFullPath(blockPath);
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? resolved;
            try
            {
                resolved = File.ResolveLinkTarget(descriptor, returnFinalTarget: false)?.FullName;
            }
            catch (IOException)
            {
                // The descriptor closed between the listing and the resolve. Nothing to report.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(resolved, target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
```

Create `MFTLib.Tests/Index/FileIndexBlockReleaseTests.cs`:

```csharp
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#145: disposing a FileIndex releases every block mapping it holds, whether or not a
///     query has handed out a handle, so a consumer can close the .mlix at a point it chooses
///     without invoking the garbage collector.
/// </summary>
[TestClass]
public class FileIndexBlockReleaseTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    uint _volumeSerial;

    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    FileIndexOptions Options()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, _volumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
    }

    [TestMethod]
    public async Task DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile()
    {
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.FindByName("readme.md").Single();
        Assert.IsFalse(entry.IsDisposed);

        await index.DisposeAsync();

        BlockFileHoldAssertions.AssertNotHeld(blockPath);
        File.Delete(blockPath);
        Assert.IsFalse(File.Exists(blockPath));
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Size);
    }

    [TestMethod]
    public async Task DisposeAsync_AfterARescan_ReleasesTheRetiredBlockToo()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var retiredHandle = index.FindByName("readme.md").Single();
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);
        var currentHandle = index.FindByName("second.md").Single();

        await index.DisposeAsync();

        Assert.IsTrue(retiredHandle.IsDisposed, "the retired snapshot must be released too");
        Assert.IsTrue(currentHandle.IsDisposed);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        BlockFileHoldAssertions.AssertNotHeld(blockPath);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileIndexBlockReleaseTests" --logger "console;verbosity=minimal"`

Expected on Linux: FAIL on `DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile` with `Assert.IsFalse failed. <cache>/T-........mlix is still open after everything that owns it was disposed`, and FAIL on the rescan test at `Assert.IsTrue failed. the retired snapshot must be released too`. If the first test passes on Linux before the fix, stop and report: the descriptor probe is not observing what MFTLib#145 measured, and a green negative is worthless. On Windows the same two tests fail, the first at the `FileShare.None` probe.

- [ ] **Step 3: Remove the HasExposedHandles skip**

In `MFTLib/Index/FileIndex.cs`, replace lines 178-192 with:

```csharp
            lock (_stateLock)
            {
                // Unconditional: a consumer that disposed everything it owns has asked for the
                // mappings to go, and a handle it kept is answered by ObjectDisposedException
                // rather than by an indefinitely open block file. See FileEntry.IsDisposed.
                _snapshot?.ReleaseNow();
                _snapshot = null;
                _driveBlocks.Clear();
                _retiredSnapshots.Clear();
            }
```

In `MFTLib/Index/FileIndex.Rescan.cs`, replace `ReleaseAllRetiredSnapshots`'s body at lines 353-364 so the `!retired.HasExposedHandles` condition goes:

```csharp
    void ReleaseAllRetiredSnapshots()
    {
        foreach (var weak in _retiredSnapshots)
        {
            if (weak.TryGetTarget(out var retired))
            {
                retired.ReleaseNow();
            }
        }

        _retiredSnapshots.Clear();
    }
```

Update both doc comments. `DisposeAsync`'s summary at `FileIndex.cs:145-150` becomes: it stops the live watch, prevents new index operations, waits for mutation and rescan ownership of `_swapGate`, and releases every snapshot it holds, current and retired. Add the ruling's race note as a `<remarks>`:

```csharp
    /// <remarks>
    ///     Disposing while another thread is reading a <see cref="FileEntry" /> minted from this
    ///     index is the same contract as disposing any other .NET disposable while another thread
    ///     uses it: the reader may observe the handle as live and then read a released mapping.
    ///     That is a consumer bug, not a library guarantee. A handle whose read is ordered after
    ///     the disposal throws <see cref="ObjectDisposedException" />.
    /// </remarks>
```

`ReleaseAllRetiredSnapshots`'s summary at `FileIndex.Rescan.cs:349-352` drops "without exposed handles".

Leave `Snapshot.HasExposedHandles` and `MarkExposed` in place: `CurrentSnapshot` at `FileIndex.cs:80` and `FileEntry.Create` at `FileEntry.cs:36` still call `MarkExposed`, and removing a now-unread flag is a separate change with its own review. If `aislop` or Roslynator reports `HasExposedHandles` as unused after this task, report it to the controller rather than deleting it here.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileIndexBlockReleaseTests" --logger "console;verbosity=minimal"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Rewrite the one existing test the ruling reverses**

`MFTLib.Tests/Index/FileIndexLifetimeTests.cs:195-215` is `DisposeAsync_AHeldFileEntryRemainsReadable` and now asserts the opposite of the contract. Replace it with:

```csharp
    /// <summary>
    ///     MFTLib#145 reversed this: a handle held across disposal used to stay readable because
    ///     disposal skipped the release whenever a query had marked the snapshot exposed. Disposal
    ///     now always unmaps, so the handle answers <see cref="ObjectDisposedException" /> instead
    ///     of keeping the block file open for a garbage collection that may never come.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_AHeldFileEntryBecomesDisposedAndThrows()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.FindByName("readme.md").Single();
        Assert.AreEqual("readme.md", entry.Name);

        await index.DisposeAsync();

        Assert.IsTrue(entry.IsValid);
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Name);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Id);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Path);
    }
```

Note the switch from `index.Find(@"T:\Documents\readme.md")` at `:199` to `index.FindByName("readme.md")`: it makes this test independent of the path shape Task 4 changes.

- [ ] **Step 6: Run the whole Index suite to find other tests this reverses**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~MFTLib.Tests.Index" --logger "console;verbosity=minimal"`

Expected: PASS. Candidates that might not be, and what to do with each:
- `MFTLib.Tests/Index/FileIndexQueryTests.cs:146-157`, `HandleFromAnOldSnapshot_StaysReadableAcrossARescan`, reads a handle after a rescan while the index is still alive. Unchanged by this task and must still pass. If it fails, Step 3 released a retired snapshot at rescan time rather than at disposal time, which the ruling forbids.
- `MFTLib.Tests/Index/FileIndexDisposalRaceTests.cs` and `NoCacheLifetimeTests.cs` exercise disposal directly. Read any failure before changing it: a failure that shows the new contract working is a test to update, a failure that shows a released block being read is a bug in Step 3.
Fix only tests whose assertions the ruling reverses; do not weaken a test to make it pass.

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## Unreleased` / `### Changed`:

```markdown
- `FileIndex.DisposeAsync` releases every snapshot it holds, current and retired, unconditionally, so the block file is closed at a point the consumer chooses instead of when the `Snapshot` finalizer runs. A `FileEntry` held across disposal reports `IsDisposed` and throws `ObjectDisposedException` on every read ([MFTLib#145](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/145))
```

- [ ] **Step 8: Commit**

```bash
git add MFTLib/Index/FileIndex.cs MFTLib/Index/FileIndex.Rescan.cs MFTLib.Tests/TestSupport/BlockFileHoldAssertions.cs MFTLib.Tests/Index/FileIndexBlockReleaseTests.cs MFTLib.Tests/Index/FileIndexLifetimeTests.cs CHANGELOG.md
git commit -F - <<'EOF'
fix(index): disposal always unmaps every block file (#145)

DisposeAsync releases the current snapshot and every retired one without
consulting HasExposedHandles, so a consumer that disposed everything it
owns gets its .mlix back. A handle it kept reports IsDisposed and throws.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

---

## Phase 2: MFTLib#143 - FileEntry.Path is the real path, Find takes a native path

The ruling: fix `Path`, option 2, not an additive `RealPath`.

- `FileEntry.Path` renders the drive block's `RootDirectoryPath` joined with the name chain using the host separator. The drive key is never rendered into a path.
- `Find` accepts a native path and resolves it by matching the longest indexed root directory prefix, then walking names from that block's root row.
- Windows MFT blocks are byte-identical before and after, because their root directory is `X:\`.
- `FileChange.Path` and `PreviousPath` come from the same builder and change with it.
- The private `FileEntry.ResolveRealPath` becomes redundant; `Open()` uses `Path`.
- Nested indexed roots resolve to the longest matching root. Needs a test.
- Child matching in `Find` follows the block's case rule: MFT blocks stay case-insensitive, enumeration blocks over a case-sensitive root match ordinally. Needs a test.

**Decision this phase locks, derived from the ruling and from existing code:** a `DriveBlock` with a null `RootDirectoryPath` cannot render a path, because the ruling forbids rendering the drive key and there is nothing else to root the chain in. `BuildPath` therefore throws `InvalidOperationException` with the same message shape `ResolveRealPath` uses today at `FileEntry.Open.cs:82-83`. Production always sets the root (`FileIndex.Scanning.cs:200-201`, `:256`, `:294` all pass `rootDirectoryPath: drive.RootDirectory`), so this only affects synthetic test blocks, which Task 3 gives roots.

**Case rule, derived from existing code:** `FileIndex.Scanning.cs:247` already decides case sensitivity for an enumeration block's cached root with `caseSensitive: !OperatingSystem.IsWindows()`. Task 4 uses exactly that rule so there is one convention, not two.

### Task 3: BuildPath renders the real root, and ResolveRealPath is deleted

**Files:**
- Modify: `MFTLib/Index/IndexNavigation.cs:30-48` (`BuildPath`)
- Modify: `MFTLib/Index/FileEntry.Open.cs:10-26` (`Open`), delete `ResolveRealPath` at `:68-91`
- Modify: `MFTLib/Index/FileEntry.Navigation.cs:5-15` (the `Path` doc)
- Modify: `MFTLib/Index/DriveBlock.cs:34-43` (the `RootDirectoryPath` doc, which currently describes the behaviour this task removes)
- Create: `MFTLib.Tests/Index/TestDriveRoot.cs`
- Modify the test files listed in Step 5
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from Phase 1.
- Produces: `IndexNavigation.BuildPath(Snapshot, ushort, uint)` keeps its signature and changes its output. `TestDriveRoot.For(char driveLetter)` returns a host-shaped root for synthetic blocks. Task 4 depends on both.

**Every caller of the path builder, found by grep at `5462650`. The implementer changes all of them or confirms each needs no change:**

- `MFTLib/Index/FileEntry.Navigation.cs:15` - `public string Path => IndexNavigation.BuildPath(Snapshot, DriveOrdinal, RowIndex);`. No code change; doc change only.
- `MFTLib/Index/JournalMutator.cs:72` - the path captured for a `FileChange` before a mutation.
- `MFTLib/Index/JournalMutator.cs:87` - the rename-new-name path.
- `MFTLib/Index/JournalMutator.cs:105` - the create path.
- `MFTLib/Index/JournalMutator.cs:110` - `previousPath` for a rename.
- `MFTLib/Index/JournalMutator.cs:118` - the rename's new path.
- `MFTLib/Index/JournalMutator.cs:148` - the modification path.
  None of the six `JournalMutator` sites needs a code change: they call the same builder and inherit the new output, which is the ruling's "intended consistency" for `FileChange.Path` and `PreviousPath`. Their tests change (Step 5).

**Every caller of `ResolveRealPath`, found by grep at `5462650`:**

- `MFTLib/Index/FileEntry.Open.cs:15` - the only call, inside `Open(FileAccess)` for `ProducerKind.Enumeration`.
- `MFTLib/Index/FileEntry.Open.cs:78` - the declaration itself.
  Both go. `Open` uses `Path` directly.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/TestDriveRoot.cs`:

```csharp
namespace MFTLib.Tests.Index;

/// <summary>
///     A root directory path shaped like the host's, for a synthetic block that has no real
///     directory behind it. Synthetic tests used to be able to leave DriveBlock.RootDirectoryPath
///     null and read a drive-letter path back; MFTLib#143 made the root the only thing a path is
///     built from, so a block that renders paths needs one on both platforms.
/// </summary>
internal static class TestDriveRoot
{
    public static string For(char driveLetter)
    {
        return OperatingSystem.IsWindows()
            ? $"{char.ToUpperInvariant(driveLetter)}:\\"
            : $"/mnt/{char.ToLowerInvariant(driveLetter)}";
    }
}
```

Add to `MFTLib.Tests/Index/IndexNavigationTests.cs` (keep the file under 400 lines; if adding these pushes it over, put them in a new `IndexNavigationTests.RealPaths.cs` partial beside it, matching the `FileIndexResilienceTests.RetiredSiblings.cs` precedent):

```csharp
    [TestMethod]
    public void Path_RendersTheBlockRootDirectory_NotTheDriveKey()
    {
        using var builder = new SyntheticBlockBuilder('T');
        var root = builder.AddRoot();
        var documents = builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var rootDirectory = Path.Combine(Path.GetTempPath(), "mftlib-path-root");
        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: rootDirectory)]);
        try
        {
            Assert.AreEqual(rootDirectory, FileEntry.Create(snapshot, 0, root).Path);
            Assert.AreEqual(Path.Combine(rootDirectory, "Documents", "readme.md"),
                FileEntry.Create(snapshot, 0, readme).Path);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Path_WithNoRootDirectoryOnTheBlock_Throws()
    {
        using var builder = new SyntheticBlockBuilder('T');
        var root = builder.AddRoot();
        var orphan = builder.AddRow("orphan.txt", root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block)]);
        try
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(
                () => _ = FileEntry.Create(snapshot, 0, orphan).Path);
            StringAssert.Contains(exception.Message, "root directory");
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
```

Add to `MFTLib.Tests/Index/FileEntryOpenTests.cs`, the round trip the issue names, which is the whole point of `#143`:

```csharp
    [TestMethod]
    public async Task Path_OverARealSubtree_IsOpenableOnThisPlatform()
    {
        var treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-open-{Guid.NewGuid():N}");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(treeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(treeRoot, "Documents", "readme.md"), "hello");
        try
        {
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', treeRoot, TestVolumeSerial.GetNext())],
                CacheDirectory = cacheDirectory,
                ProducerPolicy = ProducerPolicy.Enumeration
            }, CancellationToken.None);

            var entry = index.FindByName("readme.md").Single();

            Assert.AreEqual(Path.Combine(treeRoot, "Documents", "readme.md"), entry.Path);
            Assert.IsTrue(File.Exists(entry.Path));
            Assert.IsTrue(Directory.Exists(index.Root('T').Path));
            using var stream = entry.Open(FileAccess.Read);
            Assert.AreEqual(5L, stream.Length);
        }
        finally
        {
            foreach (var directory in new[] { treeRoot, cacheDirectory })
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // A just-unmapped block file can stay locked briefly on Windows.
                }
            }
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~IndexNavigationTests|FullyQualifiedName~FileEntryOpenTests" --logger "console;verbosity=minimal"`

Expected on Linux: `Path_RendersTheBlockRootDirectory_NotTheDriveKey` fails with `Assert.AreEqual failed. Expected:</tmp/mftlib-path-root/Documents/readme.md>. Actual:<T:\Documents\readme.md>`. `Path_WithNoRootDirectoryOnTheBlock_Throws` fails because no exception is thrown. `Path_OverARealSubtree_IsOpenableOnThisPlatform` fails on the first `Assert.AreEqual`. On Windows the first and third fail the same way with `T:\Documents\readme.md` against the temp tree path; the second fails identically.

- [ ] **Step 3: Rewrite BuildPath**

In `MFTLib/Index/IndexNavigation.cs`, replace `BuildPath` at lines 30-48:

```csharp
    /// <summary>
    ///     Joins the drive block's real root directory with the collected name chain, one path
    ///     component at a time, so the host's separator is the only one that appears and the
    ///     result is a path that can be opened and looked up again. The drive key is a display
    ///     and lookup key for the index, never part of a path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The drive block has no configured root directory, so there is nothing to root the name
    ///     chain in. Every production block sets one; a synthetic test block need not.
    /// </exception>
    internal static string BuildPath(Snapshot snapshot, ushort driveOrdinal, uint rowIndex)
    {
        var driveBlock = snapshot.GetDriveBlock(driveOrdinal);
        if (driveBlock.RootDirectoryPath is not { } rootDirectoryPath)
        {
            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} has no configured root directory to build a path from.");
        }

        var segments = CollectSegments(driveBlock.Block, rowIndex);
        var components = new string[segments.Count + 1];
        components[0] = rootDirectoryPath;
        for (var index = 0; index < segments.Count; index++)
        {
            components[index + 1] = segments[segments.Count - 1 - index];
        }

        return Path.Combine(components);
    }
```

`CollectSegments` at `:139-169` is unchanged: it already walks upward, stops at the root row without emitting the root row's own name, and throws `InvalidDataException` past `BlockLayout.MaximumPathDepth`. `Path.Combine` is what `ResolveRealPath` used at `FileEntry.Open.cs:90` and it handles a root that already ends in a separator (`T:\`) without doubling it. `using System.Text;` at `:1` becomes unused once `StringBuilder` goes; remove it.

- [ ] **Step 4: Make Open use Path and delete ResolveRealPath**

In `MFTLib/Index/FileEntry.Open.cs`, replace line 15:

```csharp
            return new FileStream(Path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
```

Delete `ResolveRealPath` and its doc comment, lines 68-91. Keep the `RootDirectoryPath` null check at `:19-23` for the MFT by-id route: that one needs any path on the volume, not this entry's path.

Update `MFTLib/Index/FileEntry.Navigation.cs:5-15` so the `Path` doc says it is the real filesystem path, built from the block's root directory and the name chain, openable and accepted by `FileIndex.Find`. Update `MFTLib/Index/DriveBlock.cs:34-43`, which currently says `FileEntry.Path` is a logical path rooted at the drive letter; it is now the base of every real path the block renders.

- [ ] **Step 5: Update every test that asserts a drive-letter path**

Each of these asserts a synthetic `X:\...` string that is now built from the block's root directory. For a test over a synthetic block, give the `DriveBlock` a root with `rootDirectoryPath: TestDriveRoot.For('<letter>')` and build the expectation with `Path.Combine(TestDriveRoot.For('<letter>'), ...)`. For a test over a real `FileIndex`, build the expectation from the fixture's own tree root.

- `MFTLib.Tests/Index/IndexNavigationTests.cs:36` (add the root), `:54`, `:55`, `:61`
- `MFTLib.Tests/Index/IndexNavigationTests.cs:117` (add the root), `:121`
- `MFTLib.Tests/Index/IndexNavigationTests.cs:141` (add the root), `:147`
- `MFTLib.Tests/Index/IndexNavigationTests.cs:190` (add the root), `:194-196` - the cyclic-parent test asserts `StartsWith(@"W:\")`; change to `StartsWith(TestDriveRoot.For('W'), StringComparison.Ordinal)`
- `MFTLib.Tests/Index/IndexNavigationTests.cs:217` (add the root), `:220-222` - the depth-cap test asserts `StartsWith(@"X:\d0\d1")`, `EndsWith(@"\d127")` and counts `'\\'`; rebuild all three with `Path.DirectorySeparatorChar` and `Path.Combine`
- `MFTLib.Tests/Index/IndexNavigationTests.cs:244` (add the root) - the depth-exceeded test asserts only the exception, so the root is needed just so `InvalidDataException` is what throws rather than `InvalidOperationException`
- `MFTLib.Tests/Index/LookupEngineTests.cs:37`, `:38` (add roots), `:72`, `:114`, `:115`
- `MFTLib.Tests/Index/LookupEngineTests.cs:142` (add the root)
- `MFTLib.Tests/Index/FileIndexQueryTests.cs:81`, `:103`, `:154` - real index over `_treeRoot`; expect `Path.Combine(_treeRoot, "Pictures", "readme.md")`, `_treeRoot`, and `Path.Combine(_treeRoot, "Documents", "readme.md")`
- `MFTLib.Tests/Index/JournalMutatorTests.cs:41` (add the root), `:162`, `:195`, `:196`, `:219`
- `MFTLib.Tests/Index/MutatorFixture.cs:21` (add the root) and `MFTLib.Tests/Index/JournalMutatorHydrationTests.cs:28`, `:51`, `:73`
- `MFTLib.Tests/Index/FileIndexLifetimeTests.cs:208` - nothing to do. Task 2 Step 5 already replaced that whole test, and its successor asserts that `entry.Path` throws rather than asserting a path shape, so no drive-letter literal survives there. Confirm that by reading the file; if a `@"T:\..."` literal is still present, Task 2 Step 5 was not applied.
- `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs:313` - `Assert.AreEqual(@"T:\", previousRoot.Path)` becomes `Assert.AreEqual(_treeRoot, previousRoot.Path)`
- `MFTLib.Tests/MftProducerEndToEndTests.cs:37` - `Assert.AreEqual(@"C:\", index.Root('C').Path)` becomes the fixture's `_rootDirectory` (the drive is built at `:216` as `new IndexedDrive('C', _rootDirectory, 123)`)
- `MFTLib.Tests/Index/EnumerationProducerTests.cs:369` compares `entry.Path` to a real `fullPath` with `OrdinalIgnoreCase`. This one starts matching for the right reason instead of by accident. Leave the comparison, and confirm it still passes.

Also change `MFTLib.Tests/Index/FileEntryOpenTests.cs:73` and `:108` from `rootDirectoryPath: @"T:\"` to `rootDirectoryPath: TestDriveRoot.For('T')`, and `MFTLib.Tests/Index/LookupEngineTests.cs:162` and `:175` likewise. Those four feed Task 4's `Find` calls as well.

If Task 1's `FileEntryDisposalTests.cs` used the literal `@"T:\"`, switch it to `TestDriveRoot.For('T')` now.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~MFTLib.Tests.Index" --logger "console;verbosity=minimal"`
Expected: PASS. Then run the whole Linux-eligible suite to catch a path assertion outside the Index folder:
Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --logger "console;verbosity=minimal"`
Expected: PASS apart from tests `scripts/coverage-linux.sh` filters out (`MftResultTests`, `MftVolumeTests`, `NativeCoverageTests`, `NativeParserCoverageTests`, `UsnJournalSyntheticTests`, plus the four named individual tests). Those five classes are excluded from compilation on Linux by `MFTLib.Tests/MFTLib.Tests.csproj`, so they will not appear at all; the four individual tests will fail on Linux and are expected to.

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## Unreleased` / `### Changed`:

```markdown
- `FileEntry.Path` renders the drive block's real root directory joined with the name chain using the host separator, so it is openable and can be looked up again on any platform; the drive key is never rendered into a path. `FileChange.Path` and `PreviousPath` come from the same builder and change with it. A Windows MFT block is byte-identical because its root directory is `X:\` ([MFTLib#143](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/143))
```

- [ ] **Step 8: Commit**

```bash
git add MFTLib/Index/IndexNavigation.cs MFTLib/Index/FileEntry.Open.cs MFTLib/Index/FileEntry.Navigation.cs MFTLib/Index/DriveBlock.cs MFTLib.Tests/ CHANGELOG.md
git commit -F - <<'EOF'
feat(index): FileEntry.Path is the real filesystem path (#143)

BuildPath joins the drive block's RootDirectoryPath with the name chain
through Path.Combine, so the host separator is the only one that appears
and an enumeration block over a subtree stops emitting a synthetic drive
path. Open() uses Path and the private ResolveRealPath is gone.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

### Task 4: Find takes a native path, longest-root match, per-block case rule

**Files:**
- Modify: `MFTLib/Index/LookupEngine.cs:22-35` (delete `TryParseDriveLetter`), `:37-64` (`Find`), `:89-107` (`TryFindChild`), `:3-8` (the type doc)
- Modify: `MFTLib/Index/FileIndex.Queries.cs:5-10` (`Find`'s parameter name and doc)
- Modify: the test files listed in Step 4
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: `IndexNavigation.BuildPath`'s new output and `TestDriveRoot.For` from Task 3.
- Produces: `LookupEngine.Find(Snapshot snapshot, string nativePath)` (internal, same shape, new semantics); `FileIndex.Find(string nativePath)` (public, same shape, new semantics); `LookupEngine.IsCaseSensitive(DriveBlock driveBlock)` (internal, available to the test assembly through the existing `InternalsVisibleTo` friendship).

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/LookupEngineTests.cs`. The existing fixture at `:19-40` builds two synthetic blocks; give them roots (Task 3 Step 5 already did) and add:

```csharp
    [TestMethod]
    public void Find_AcceptsANativePathRootedAtTheBlockRoot()
    {
        var entry = LookupEngineTestAccess.Find(_snapshot,
            Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"));

        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual("report.pdf", entry.Value.Name);
    }

    [TestMethod]
    public void Find_RoundTripsWhateverPathEmits()
    {
        var report = LookupEngineTestAccess.Find(_snapshot,
            Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"))!.Value;

        var roundTripped = LookupEngineTestAccess.Find(_snapshot, report.Path);

        Assert.IsTrue(roundTripped.HasValue);
        Assert.AreEqual(report.Path, roundTripped.Value.Path);
        Assert.AreEqual(report.Id, roundTripped.Value.Id);
    }

    [TestMethod]
    public void Find_APathUnderNoIndexedRootReturnsNull()
    {
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot,
            Path.Combine(TestDriveRoot.For('Z'), "Documents", "report.pdf")));
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, "not-a-path"));
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, ""));
    }

    [TestMethod]
    public void Find_TheRootDirectoryItselfReturnsTheRootRow()
    {
        var entry = LookupEngineTestAccess.Find(_snapshot, TestDriveRoot.For('T'));

        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(TestDriveRoot.For('T'), entry.Value.Path);
    }
```

Create a second test class in the same file's folder, `MFTLib.Tests/Index/LookupEngineRootMatchingTests.cs`, for the two behaviours the ruling explicitly says need tests:

```csharp
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#143's two named cases: nested indexed roots resolve to the longest matching root,
///     and child matching follows the block's case rule rather than one global rule.
/// </summary>
[TestClass]
public class LookupEngineRootMatchingTests
{
    static readonly DateTime Moment = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    static SyntheticBlockBuilder BuildSingleFileBlock(char driveLetter, string fileName)
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        var root = builder.AddRoot();
        builder.AddRow(fileName, root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);
        return builder;
    }

    [TestMethod]
    public void Find_NestedIndexedRoots_ResolvesToTheLongestMatchingRoot()
    {
        var outerRoot = Path.Combine(Path.GetTempPath(), "mftlib-outer");
        var innerRoot = Path.Combine(outerRoot, "inner");

        using var outerBuilder = BuildSingleFileBlock('O', "outer.txt");
        using var innerBuilder = BuildSingleFileBlock('I', "inner.txt");
        var snapshot = Snapshot.Create([
            new DriveBlock('O', 0, outerBuilder.OpenForReading(out _)!, rootDirectoryPath: outerRoot),
            new DriveBlock('I', 1, innerBuilder.OpenForReading(out _)!, rootDirectoryPath: innerRoot)
        ]);
        try
        {
            var inner = LookupEngineTestAccess.Find(snapshot, Path.Combine(innerRoot, "inner.txt"));
            Assert.IsTrue(inner.HasValue);
            Assert.AreEqual('I', inner.Value.Id.DriveLetter);

            var outer = LookupEngineTestAccess.Find(snapshot, Path.Combine(outerRoot, "outer.txt"));
            Assert.IsTrue(outer.HasValue);
            Assert.AreEqual('O', outer.Value.Id.DriveLetter);

            // "inner.txt" is not a child of the outer block's root, so the longest root having
            // won is exactly what makes this resolve at all.
            Assert.IsNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(innerRoot, "absent.txt")));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_AnMftBlockMatchesChildNamesCaseInsensitivelyOnEitherPlatform()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        var block = builder.OpenForReading(out _)!;
        var root = TestDriveRoot.For('T');
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: root)]);
        try
        {
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(root, "documents", "notes.txt")));
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(root, "DOCUMENTS", "NOTES.TXT")));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_AnEnumerationBlockFollowsTheHostCaseRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "mftlib-case");
        using var builder = BuildSingleFileBlock('E', "Readme.md");
        var snapshot = Snapshot.Create([
            new DriveBlock('E', 0, builder.OpenForReading(out _)!, rootDirectoryPath: root)
        ]);
        try
        {
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot, Path.Combine(root, "Readme.md")));

            var mismatchedCase = LookupEngineTestAccess.Find(snapshot, Path.Combine(root, "README.MD"));
            if (OperatingSystem.IsWindows())
            {
                Assert.IsNotNull(mismatchedCase, "a Windows enumeration block folds case the way the host does");
            }
            else
            {
                Assert.IsNull(mismatchedCase, "an enumeration block over a case-sensitive root matches ordinally");
            }
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~LookupEngine" --logger "console;verbosity=minimal"`

Expected on Linux: every new test fails with `Assert.IsTrue failed` or `Assert.IsNotNull failed`, because `TryParseDriveLetter` at `LookupEngine.cs:27` rejects any path whose second character is not `':'`, so `Find` returns null at `:42-46` before any lookup. On Windows `Find_NestedIndexedRoots_ResolvesToTheLongestMatchingRoot` and `Find_AnEnumerationBlockFollowsTheHostCaseRule` fail on the temp-directory roots; `Find_AcceptsANativePathRootedAtTheBlockRoot` fails because `T:\` is not a real root there either but `TryParseDriveLetter` accepts it and then walks from the wrong place. Record the actual failures.

- [ ] **Step 3: Rewrite Find**

In `MFTLib/Index/LookupEngine.cs`, delete `TryParseDriveLetter` at lines 22-35 (its only caller is `Find`; grep at `5462650` confirms no other reference in the repository) and replace `Find` at lines 37-64 and `TryFindChild` at lines 89-107:

```csharp
    /// <summary>
    ///     Resolves a native filesystem path against the indexed roots. The block whose root
    ///     directory is the longest prefix of the path wins, so nested indexed roots resolve to
    ///     the inner one, and the remaining segments are walked down from that block's root row.
    /// </summary>
    internal static FileEntry? Find(Snapshot snapshot, string nativePath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nativePath);

        if (FindLongestMatchingRoot(snapshot, nativePath) is not { } match)
        {
            return null;
        }

        var (driveBlock, remainder) = match;
        var currentRow = driveBlock.Block.Header.RootRow;
        var caseSensitive = IsCaseSensitive(driveBlock);
        foreach (var segmentRange in remainder.SplitAny(['\\', '/']))
        {
            var segment = remainder[segmentRange];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (!TryFindChild(snapshot, driveBlock.DriveOrdinal, currentRow, segment, caseSensitive, out currentRow))
            {
                return null;
            }
        }

        return FileEntry.Create(snapshot, driveBlock.DriveOrdinal, currentRow);
    }

    /// <summary>
    ///     How this block's names compare. An MFT block describes an NTFS volume, which folds
    ///     case, so it is case-insensitive wherever the process runs. An enumeration block
    ///     inherits the host's rule, which is the same decision
    ///     <c>FileIndex.TryOpenExistingBlock</c> already makes when it compares a cached root.
    /// </summary>
    internal static bool IsCaseSensitive(DriveBlock driveBlock)
    {
        ArgumentNullException.ThrowIfNull(driveBlock);
        return driveBlock.ProducerKind == ProducerKind.Enumeration && !OperatingSystem.IsWindows();
    }

    /// <summary>
    ///     The indexed root that is the longest prefix of <paramref name="nativePath" />, with the
    ///     part of the path below it. A trailing separator on either side is not significant, so
    ///     a root of <c>T:\</c> matches <c>T:\Documents</c> and a root of <c>/home/schoen</c>
    ///     matches <c>/home/schoen/</c>. A block with no root directory can never match.
    /// </summary>
    static (DriveBlock DriveBlock, ReadOnlySpan<char> Remainder)? FindLongestMatchingRoot(
        Snapshot snapshot, string nativePath)
    {
        DriveBlock? best = null;
        var bestLength = -1;
        foreach (var candidate in snapshot.DriveBlocks)
        {
            if (candidate.RootDirectoryPath is not { } root)
            {
                continue;
            }

            var trimmed = root.TrimEnd('\\', '/');
            if (trimmed.Length > nativePath.Length ||
                !nativePath.AsSpan(0, trimmed.Length).Equals(trimmed, Comparison(candidate)))
            {
                continue;
            }

            if (nativePath.Length > trimmed.Length &&
                nativePath[trimmed.Length] != '\\' && nativePath[trimmed.Length] != '/')
            {
                continue;
            }

            if (trimmed.Length > bestLength)
            {
                best = candidate;
                bestLength = trimmed.Length;
            }
        }

        return best is null ? null : (best, nativePath.AsSpan(bestLength));
    }

    static StringComparison Comparison(DriveBlock driveBlock)
    {
        return IsCaseSensitive(driveBlock) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    static bool TryFindChild(Snapshot snapshot, ushort driveOrdinal, uint parentRow,
        ReadOnlySpan<char> segment, bool caseSensitive, out uint childRow)
    {
        var scanner = new RowScanner(snapshot, driveOrdinal);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted && row.ParentRow == parentRow &&
                scanner.CurrentRowIndex != parentRow &&
                NameMatching.EqualsName(scanner.CurrentName, segment, caseSensitive))
            {
                childRow = scanner.CurrentRowIndex;
                return true;
            }
        }

        childRow = 0;
        return false;
    }
```

An empty `trimmed` (a root of `/` on Linux) matches every absolute path with length 0, which is correct: it is the shortest possible root and any other indexed root beats it.

Update the type doc at `LookupEngine.cs:3-8`: `Find` resolves a native path by matching the longest indexed root, then walks one name per level.

- [ ] **Step 4: Update FileIndex.Find and the existing Find call sites**

In `MFTLib/Index/FileIndex.Queries.cs:5-10`:

```csharp
    /// <summary>
    ///     Resolves a native filesystem path to its entry. The indexed root directory that is the
    ///     longest prefix of the path selects the block, and the remaining segments are walked
    ///     down from that block's root row, one name per level. This is the inverse of
    ///     <see cref="FileEntry.Path" />: whatever that emits, this accepts.
    /// </summary>
    public FileEntry? Find(string nativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LookupEngine.Find(CurrentSnapshot, nativePath);
    }
```

Then close the ruling's own acceptance sentence, which asks for the round trip over a real enumerated subtree and not only over a synthetic block. Task 3 created `Path_OverARealSubtree_IsOpenableOnThisPlatform` in `MFTLib.Tests/Index/FileEntryOpenTests.cs`; add two assertions to it, after its `Assert.IsTrue(File.Exists(entry.Path))`:

```csharp
            var roundTripped = index.Find(entry.Path);
            Assert.IsTrue(roundTripped.HasValue);
            Assert.AreEqual(entry.Id, roundTripped.Value.Id, "Find must accept whatever Path emits");
```

Every existing call site that passes a drive-letter path, found by grep at `5462650`:

- `MFTLib.Tests/Index/LookupEngineTests.cs:53`, `:62`, `:70`, `:78`, `:79`, `:85`, `:91`, `:92` - the eight calls in the original test methods. Rewrite each to a `Path.Combine(TestDriveRoot.For(...), ...)` form. `Find_IsCaseInsensitiveOnPathSegments` at `:60-65` now has a platform-dependent answer for an enumeration block; delete it, because `LookupEngineRootMatchingTests.Find_AnEnumerationBlockFollowsTheHostCaseRule` is the replacement that states the real rule.
- `MFTLib.Tests/Index/LookupEngineTests.cs:146` - `V:\Documents\report.pdf` against the block built at `:127-142`; give that block `rootDirectoryPath: TestDriveRoot.For('V')` and build the path from it.
- `MFTLib.Tests/Index/LookupEngineTests.cs:178` - `T:\documents\notes.txt` against an MFT-shaped block whose root is `@"T:\"` at `:175`; change to `TestDriveRoot.For('T')` and `Path.Combine`.
- `MFTLib.Tests/Index/FileEntryOpenTests.cs:76`, `:111` - same, against the roots set at `:73` and `:108`.
- `MFTLib.Tests/Index/FileIndexQueryTests.cs:53`, `:78`, `:117`, `:149` - real index over `_treeRoot`; use `Path.Combine(_treeRoot, ...)`.
- `MFTLib.Tests/Index/FileIndexResilienceTests.cs:324` - `Path.Combine(_treeRoot, "Documents", "readme.md")`.
- `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs:219` - `Path.Combine(_treeRoot, "indexed.txt")`.
- `MFTLib.Tests/Index/FileIndexWatchTests.cs:153`, `:174` - `Path.Combine(_treeRoot, "Documents", "readme.md")`; check the fixture's field name in that file.
- `MFTLib.Tests/MftProducerEndToEndTests.cs:39`, `:49`, `:50`, `:57`, `:64`, `:67`, `:69`, `:70`, `:107`, `:108`, `:109`, `:110`, `:111`, `:129`, `:130`, `:143`, `:153`, `:154`, `:185` - nineteen calls against drive `C` whose root is the fixture's `_rootDirectory` (`:216`). Introduce a small local helper in that file, `string At(params string[] segments) => Path.Combine([_rootDirectory, .. segments]);`, and rewrite all nineteen through it rather than editing nineteen literals by hand.
- `MFTLib.Tests/Index/FileIndexLifetimeTests.cs:199` - already changed to `FindByName` by Task 2 Step 5.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --logger "console;verbosity=minimal"`
Expected: PASS, apart from the four Linux-only failures `scripts/coverage-linux.sh` filters (listed in Task 3 Step 6).

- [ ] **Step 6: Add the CHANGELOG entry**

Under `## Unreleased` / `### Changed`:

```markdown
- `FileIndex.Find` accepts a native filesystem path and resolves it against the longest matching indexed root directory instead of requiring a `X:\` drive-letter prefix, so it is the exact inverse of `FileEntry.Path` on every platform. Child names match by the block's own rule: an MFT block folds case, an enumeration block over a case-sensitive root matches ordinally ([MFTLib#143](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/143))
```

Under `### Removed`:

```markdown
- Removed the internal `LookupEngine.TryParseDriveLetter`; a path no longer carries a drive key
```

- [ ] **Step 7: Commit**

```bash
git add MFTLib/Index/LookupEngine.cs MFTLib/Index/FileIndex.Queries.cs MFTLib.Tests/ CHANGELOG.md
git commit -F - <<'EOF'
feat(index): Find resolves a native path by longest indexed root (#143)

TryParseDriveLetter is gone. Find picks the drive block whose root
directory is the longest prefix of the path, then walks the remainder from
that block's root row using the block's own case rule: MFT blocks fold
case, enumeration blocks follow the host. Round-trips FileEntry.Path.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

---

## Phase 3: MFTLib#146 - a DriveStatus signal for where the current block came from

The ruling: the `DriveStatus` signal, no open-time rebuild option.

- One field on `DriveStatus` saying where the current block came from: warm-started from cache, or produced during this open (a first-ever scan or a rejected-and-rebuilt block). A small enum, not a bool, so a cache-management UI can show "loaded from cache" versus "scanned".
- The field describes the block that is current at the time the status is read. After a successful `RescanAsync` the block was produced by that rescan, so it reads as produced, not warm-started.
- No `FileIndexOptions` rebuild switch.

**Naming decision this task locks.** The enum is `BlockSource` and its two block-bearing members are `WarmStartedFromCache` and `ProducedByScan`. `ProducedByScan` rather than `ProducedDuringOpen` because the ruling requires a rescan-produced block to read as produced too, and a member named for the open would then be a lie in the one case the ruling calls out. A third member, `None`, covers an offline or failed drive that has no block at all, so the property can stay `required` alongside every other field on the record.

### Task 5: BlockSource on DriveStatus

**Files:**
- Create: `MFTLib/Index/BlockSource.cs`
- Modify: `MFTLib/Index/DriveStatus.cs` (add the field)
- Modify: `MFTLib/Index/FileIndex.cs:15-21` (add the dictionary), `:229-287` (`DescribeOnlineDrive`, `DescribeDrive`)
- Modify: `MFTLib/Index/FileIndex.Scanning.cs:5-78` (`AddDriveAsync`), `:80-99` (`RecordFailedDrive`)
- Modify: `MFTLib/Index/FileIndex.Rescan.cs:138-144` (the swap's state update)
- Modify: `MFTLib.Tests/Index/FileIndexDriveStatusTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing from earlier phases.
- Produces: `public enum BlockSource { None, WarmStartedFromCache, ProducedByScan }` and `DriveStatus.BlockSource` (`public required BlockSource`).

- [ ] **Step 1: Write the failing tests**

Add to `MFTLib.Tests/Index/FileIndexDriveStatusTests.cs`. The fixture at `:14-55` builds a real tree and an enumeration-policy `FileIndexOptions` with a fixed serial `0x0BADF00D`; the counting-producer tests need an MFT policy instead, so add a second options helper and reuse `FileIndexProducerSelectionTests.BuildMftShapedBlock`'s shape. If lifting that helper is awkward, put these two tests in `FileIndexProducerSelectionTests.cs` next to the existing `BuildMftShapedBlock` at `:73` and the counting producers at `:354-365`, and keep only the enumeration-policy test here.

```csharp
    [TestMethod]
    public async Task Drives_AFirstEverScanReportsTheBlockAsProduced()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task Drives_ASecondOpenReportsTheBlockAsWarmStarted()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task Drives_AfterARescanTheBlockReadsAsProduced()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);

        await reopened.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(BlockSource.ProducedByScan, reopened.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task Drives_AnOfflineDriveReportsNoBlockSource()
    {
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('Z', Path.Combine(_treeRoot, "absent"), 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.AreEqual(BlockSource.None, index.Drives.Single().BlockSource);
    }
```

And the ruling's own acceptance shape, the consumer loop, in `MFTLib.Tests/Index/FileIndexProducerSelectionTests.cs` beside `BuildMftShapedBlock`:

```csharp
    /// <summary>
    ///     MFTLib#146's acceptance: a consumer's "build and rebuild everything" loop rescans only
    ///     the drives that warm-started, so a first-ever open scans each drive once rather than
    ///     twice, and an open with a cache present still gets exactly one real scan.
    /// </summary>
    [TestMethod]
    public async Task ConsumerRebuildLoop_SkipsDrivesTheOpenJustScanned()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        static async Task RebuildWarmStartedDrivesAsync(FileIndex index)
        {
            foreach (var status in index.Drives.Where(
                         drive => drive.BlockSource == BlockSource.WarmStartedFromCache).ToArray())
            {
                await index.RescanAsync(status.DriveLetter, CancellationToken.None);
            }
        }

        await using (var firstOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
                         CancellationToken.None))
        {
            Assert.AreEqual(BlockSource.ProducedByScan, firstOpen.Drives.Single().BlockSource);
            await RebuildWarmStartedDrivesAsync(firstOpen);
            Assert.AreEqual(1, invocationCount, "the open already scanned this drive, so the loop must skip it");
        }

        await using var secondOpen = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, CountingProducer),
            CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, secondOpen.Drives.Single().BlockSource);

        await RebuildWarmStartedDrivesAsync(secondOpen);

        Assert.AreEqual(2, invocationCount, "a warm-started drive is the one the loop must rescan");
        Assert.AreEqual(BlockSource.ProducedByScan, secondOpen.Drives.Single().BlockSource);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~FileIndexDriveStatusTests|FullyQualifiedName~ConsumerRebuildLoop" --logger "console;verbosity=minimal"`
Expected: compile failure, `The name 'BlockSource' does not exist in the current context` and `'DriveStatus' does not contain a definition for 'BlockSource'`.

- [ ] **Step 3: Add the enum**

Create `MFTLib/Index/BlockSource.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     Where a drive's current block came from, as of the moment the status is read. A consumer
///     running a rebuild loop over an index it just opened uses this to skip the drives the open
///     already scanned, and a cache-management view uses it to show "loaded from cache" against
///     "scanned".
/// </summary>
public enum BlockSource
{
    /// <summary>
    ///     The drive has no block: it was offline at open, or its MFT producer failed.
    /// </summary>
    None,

    /// <summary>
    ///     The block was adopted from a valid file in the cache directory. Its contents are as
    ///     old as <see cref="DriveStatus.ScanTimestamp" /> says.
    /// </summary>
    WarmStartedFromCache,

    /// <summary>
    ///     This index produced the block: a first-ever cold scan during <c>OpenAsync</c>, a cold
    ///     scan after an existing block was rejected (see <see cref="DriveStatus.DiscardedBlock" />),
    ///     or a successful <see cref="FileIndex.RescanAsync" />.
    /// </summary>
    ProducedByScan
}
```

- [ ] **Step 4: Add the field to DriveStatus**

In `MFTLib/Index/DriveStatus.cs`, after `ProducerKind` at line 11:

```csharp
    /// <summary>
    ///     Where the block behind this status came from. <see cref="BlockSource.None" /> for a
    ///     drive with no block. Reads <see cref="BlockSource.ProducedByScan" /> after a successful
    ///     rescan, because that rescan is what produced the current block.
    /// </summary>
    public required BlockSource BlockSource { get; init; }
```

- [ ] **Step 5: Track it on FileIndex**

`DescribeDrive` at `FileIndex.cs:268-287` already takes five parameters and aislop's `maxParams` is 6, so bundle the per-ordinal annotations instead of adding a sixth positional. In `MFTLib/Index/FileIndex.cs`:

```csharp
    readonly Dictionary<ushort, BlockSource> _blockSourcesByOrdinal = [];
```

beside the four dictionaries at `:18-21`, and:

```csharp
    /// <summary>Everything a drive's status carries that is not read off its block header.</summary>
    readonly record struct DriveStatusAnnotations(
        BlockValidationResult? DiscardedBlock,
        int AccessDeniedSubtreeCount,
        string? MftProducerFailureMessage,
        string? WatchFailureMessage,
        BlockSource BlockSource);
```

Rewrite `DescribeOnlineDrive` at `:229-252` to build a `DriveStatusAnnotations` from the five dictionaries (`_blockSourcesByOrdinal.GetValueOrDefault(driveBlock.DriveOrdinal)` returns `BlockSource.None` for a missing entry, which never happens for an online drive but is the right default) and call `DescribeDrive(driveBlock, in annotations)`. Rewrite `DescribeDrive` at `:268-287` to take `(DriveBlock driveBlock, in DriveStatusAnnotations annotations)` and set `BlockSource = annotations.BlockSource` alongside the four fields it already reads from those parameters.

In `MFTLib/Index/FileIndex.Scanning.cs`, in `AddDriveAsync`:
- the offline path at `:13-23` sets `BlockSource = BlockSource.None`;
- the warm-start branch at `:53-56` records `_blockSourcesByOrdinal[driveOrdinal] = BlockSource.WarmStartedFromCache;` under `_stateLock`;
- the cold-scan branch at `:57-71` records `BlockSource.ProducedByScan` in the same `lock (_stateLock)` that already stores `_accessDeniedSubtreeCountByOrdinal` at `:68-71`.

In `RecordFailedDrive` at `:80-99`, set `BlockSource = BlockSource.None` on the blockless status and add `_blockSourcesByOrdinal.Remove(driveOrdinal);` beside the two existing `Remove` calls at `:96-97`.

In `MFTLib/Index/FileIndex.Rescan.cs`, inside the `lock (_stateLock)` at `:139-144` that swaps the block in, add `_blockSourcesByOrdinal[driveOrdinal] = BlockSource.ProducedByScan;`. A rescan that fails returns at `:131-134` before this block, leaving the previous value in place, which is correct: the current block is still the one that was there.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~MFTLib.Tests.Index" --logger "console;verbosity=minimal"`
Expected: PASS. Any other test that constructs a `DriveStatus` directly will fail to compile on the new `required` member; grep for `new DriveStatus` and add `BlockSource = BlockSource.None` (or the truthful value) to each.

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## Unreleased` / `### Added`:

```markdown
- `DriveStatus.BlockSource` and the `BlockSource` enum (`None`, `WarmStartedFromCache`, `ProducedByScan`) report where a drive's current block came from, so a consumer's rebuild loop can skip the drives an `OpenAsync` just scanned instead of scanning every cold drive twice. A successful `RescanAsync` leaves the drive reading `ProducedByScan` ([MFTLib#146](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/146))
```

- [ ] **Step 8: Commit**

```bash
git add MFTLib/Index/BlockSource.cs MFTLib/Index/DriveStatus.cs MFTLib/Index/FileIndex.cs MFTLib/Index/FileIndex.Scanning.cs MFTLib/Index/FileIndex.Rescan.cs MFTLib.Tests/ CHANGELOG.md
git commit -F - <<'EOF'
feat(index): DriveStatus reports where the current block came from (#146)

BlockSource distinguishes a warm start from a block this index produced,
so a consumer's rebuild loop stops scanning a cold drive twice on a first
run. A successful rescan leaves the drive reading ProducedByScan.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

---

## Phase 4: MFTLib#144 - MFTLib enumerates its own cache

The ruling: `CacheDirectory` gains an enumeration that answers "which drives do I have cached" from a cache directory path, returning one record per file whose name matches the block filename format: drive letter, volume serial, full path, file size, last-write time. The filename format stays private; no public parser is exposed.

What an implementer carries, verbatim from the ruling:

- Enumeration reports filename-recognised candidates only. It does not open or validate blocks. One corrupt file must not throw the whole listing.
- Files that do not match the format are skipped, not errors. The `mftlib-nocache-` temp files live in the system temp folder, not the cache folder, and must never be reported as cache even if a caller points enumeration at that folder.
- Root directory is not part of the record.
- file-wizard's `BlockCacheCatalog` is replaced by the library call (consumer follow-up, not this issue).

**Pattern-matching decision this task locks.** `Directory.EnumerateFiles` pattern matching is not enough. Three reasons, all checkable against `CacheDirectory.BlockFileName` at `CacheDirectory.cs:27-30`, which produces `$"{char.ToUpperInvariant(driveLetter)}-{volumeSerial:X8}.mlix"`: a `*.mlix` glob also matches `mftlib-nocache-<guid>-T-0BADF00D.mlix`, which the ruling says must never be reported; a `?-????????.mlix` glob matches `A-ZZZZZZZZ.mlix`, whose serial is not hexadecimal; and Windows glob matching has short-name behaviour that a filename contract should not depend on. So the glob is the coarse filter and a private `TryParseBlockFileName` is the decision, ending in a round-trip comparison against `BlockFileName` itself so the parser can never drift from the formatter it inverts.

### Task 6: CacheDirectory.EnumerateCached

**Files:**
- Create: `MFTLib/Index/CachedBlockFile.cs`
- Modify: `MFTLib/Index/CacheDirectory.cs` (add `EnumerateCached` and the private parser; the file is 107 lines at `5462650` and stays well under 400)
- Create: `MFTLib.Tests/Index/CacheDirectoryEnumerationTests.cs`
- Modify: `docs/index-format.md:8-12` (the file-name section)
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed record CachedBlockFile(char DriveLetter, uint VolumeSerial, string Path, long SizeBytes, DateTime LastWriteTimeUtc)` and `public static IReadOnlyList<CachedBlockFile> CacheDirectory.EnumerateCached(string cacheDirectoryPath)`.

- [ ] **Step 1: Write the failing tests**

Create `MFTLib.Tests/Index/CacheDirectoryEnumerationTests.cs`:

```csharp
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#144: the library owns both directions of its block file naming, so a consumer never
///     reimplements the inverse of <see cref="CacheDirectory.BlockFileName" />.
/// </summary>
[TestClass]
public class CacheDirectoryEnumerationTests
{
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_cacheDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void EnumerateCached_ReportsOnlyTheFilesThatMatchTheBlockFileNameFormat()
    {
        var first = WriteFile(CacheDirectory.BlockFileName('C', 0x0BADF00D), "first block");
        var second = WriteFile(CacheDirectory.BlockFileName('D', 0x12345678), "second");
        WriteFile("notes.mlix", "an unrelated name that happens to end in the extension");
        WriteFile($"mftlib-nocache-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName('E', 1)}", "no-cache temp file");
        WriteFile(CacheDirectory.BlockFileName('F', 2) + ".retired-" + Guid.NewGuid().ToString("N"), "retired");

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .OrderBy(entry => entry.DriveLetter).ToArray();

        Assert.AreEqual(2, cached.Length);
        Assert.AreEqual('C', cached[0].DriveLetter);
        Assert.AreEqual(0x0BADF00Du, cached[0].VolumeSerial);
        Assert.AreEqual(first, cached[0].Path);
        Assert.AreEqual(new FileInfo(first).Length, cached[0].SizeBytes);
        Assert.AreEqual(File.GetLastWriteTimeUtc(first), cached[0].LastWriteTimeUtc);
        Assert.AreEqual('D', cached[1].DriveLetter);
        Assert.AreEqual(0x12345678u, cached[1].VolumeSerial);
        Assert.AreEqual(second, cached[1].Path);
    }

    [TestMethod]
    public void EnumerateCached_IsTheInverseOfBlockFileNameAcrossTheLetterRangeAndEdgeSerials()
    {
        var expected = new List<(char DriveLetter, uint VolumeSerial)>();
        var serials = new uint[] { 0u, 1u, 0x0BADF00Du, uint.MaxValue };
        for (var driveLetter = 'A'; driveLetter <= 'Z'; driveLetter++)
        {
            var serial = serials[(driveLetter - 'A') % serials.Length];
            WriteFile(CacheDirectory.BlockFileName(driveLetter, serial), "block");
            expected.Add((driveLetter, serial));
        }

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .Select(entry => (entry.DriveLetter, entry.VolumeSerial)).ToArray();

        CollectionAssert.AreEquivalent(expected, cached);
    }

    [TestMethod]
    public void EnumerateCached_LowercaseFileNameIsNotRecognised()
    {
        // BlockFileName upper-cases the letter and formats the serial with X8, so a name that
        // does not round-trip through it is not one this library wrote. Windows preserves the
        // case a file was created with, so the parser is what decides on both platforms.
        WriteFile("c-0badf00d.mlix", "block");

        Assert.AreEqual(0, CacheDirectory.EnumerateCached(_cacheDirectory).Count);
    }

    [TestMethod]
    public void EnumerateCached_AMissingDirectoryReturnsEmpty()
    {
        var absent = Path.Combine(_cacheDirectory, "absent");

        var cached = CacheDirectory.EnumerateCached(absent);

        Assert.AreEqual(0, cached.Count);
    }

    [TestMethod]
    public void EnumerateCached_AnEmptyDirectoryReturnsEmpty()
    {
        Assert.AreEqual(0, CacheDirectory.EnumerateCached(_cacheDirectory).Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~CacheDirectoryEnumerationTests" --logger "console;verbosity=minimal"`
Expected: compile failure, `'CacheDirectory' does not contain a definition for 'EnumerateCached'`.

- [ ] **Step 3: Add the record**

Create `MFTLib/Index/CachedBlockFile.cs`:

```csharp
namespace MFTLib.Index;

/// <summary>
///     One block file found in a cache directory, recognised by its name alone. Nothing here was
///     read from inside the block: <see cref="CacheDirectory.EnumerateCached" /> does not open or
///     validate blocks, so a file listed here may still be rejected when the index opens it. The
///     drive's root directory is not part of this record because it lives inside the block.
/// </summary>
public sealed record CachedBlockFile(
    char DriveLetter,
    uint VolumeSerial,
    string Path,
    long SizeBytes,
    DateTime LastWriteTimeUtc);
```

- [ ] **Step 4: Add the enumeration and its private inverse**

In `MFTLib/Index/CacheDirectory.cs`, after `BlockFileName` at line 30:

```csharp
    /// <summary>
    ///     The cached drives in <paramref name="cacheDirectoryPath" />, one record per file whose
    ///     name this class wrote. The listing is eager rather than lazy so a missing directory is
    ///     an empty result at the call rather than a deferred throw, and so a file that vanishes
    ///     or refuses a stat mid-listing costs that one entry instead of the whole answer. Nothing
    ///     is opened or validated here; validation stays in the open path, which already reports
    ///     per-drive failures.
    /// </summary>
    public static IReadOnlyList<CachedBlockFile> EnumerateCached(string cacheDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var directory = new DirectoryInfo(cacheDirectoryPath);
        if (!directory.Exists)
        {
            return [];
        }

        var cached = new List<CachedBlockFile>();
        foreach (var file in directory.EnumerateFiles("*" + BlockFileExtension))
        {
            if (!TryParseBlockFileName(file.Name, out var driveLetter, out var volumeSerial))
            {
                continue;
            }

            try
            {
                cached.Add(new CachedBlockFile(driveLetter, volumeSerial, file.FullName, file.Length,
                    file.LastWriteTimeUtc));
            }
            catch (IOException)
            {
                // The file went away between the listing and the stat. One missing entry is the
                // right cost; the caller asked what is cached, not for a transaction.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning: a file this process cannot stat is not a listing failure.
            }
        }

        return cached;
    }

    const string BlockFileExtension = ".mlix";

    /// <summary>
    ///     The exact inverse of <see cref="BlockFileName" />, and private on purpose: the format
    ///     is the library's, not the consumer's. The final round trip through
    ///     <see cref="BlockFileName" /> is what keeps the two from ever drifting apart, so a name
    ///     is only accepted when the formatter would have produced exactly it.
    /// </summary>
    static bool TryParseBlockFileName(string fileName, out char driveLetter, out uint volumeSerial)
    {
        driveLetter = '\0';
        volumeSerial = 0;

        const int serialDigits = 8;
        var expectedLength = 1 + 1 + serialDigits + BlockFileExtension.Length;
        if (fileName.Length != expectedLength || fileName[1] != '-')
        {
            return false;
        }

        if (!uint.TryParse(fileName.AsSpan(2, serialDigits), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var parsedSerial))
        {
            return false;
        }

        var candidateLetter = fileName[0];
        if (!string.Equals(BlockFileName(candidateLetter, parsedSerial), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        driveLetter = candidateLetter;
        volumeSerial = parsedSerial;
        return true;
    }
```

Add `using System.Globalization;` to the file's usings at `:1-3`. Change `BlockFileName` at `:27-30` to build its extension from the `BlockFileExtension` constant so one edit moves both directions:

```csharp
    public static string BlockFileName(char driveLetter, uint volumeSerial)
    {
        return $"{char.ToUpperInvariant(driveLetter)}-{volumeSerial:X8}{BlockFileExtension}";
    }
```

The round-trip check rejects `c-0badf00d.mlix` (`BlockFileName('c', ...)` produces `C-0BADF00D.mlix`), rejects `A-ZZZZZZZZ.mlix` (the hex parse fails first), and rejects any `mftlib-nocache-*` or `*.retired-*` name on length alone.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~CacheDirectory" --logger "console;verbosity=minimal"`
Expected: PASS, 5 new tests plus the 8 existing `CacheDirectoryTests`.

- [ ] **Step 6: Document the format's two directions**

In `docs/index-format.md`, in the `## File name` section at lines 8-12, add a sentence: the library owns both directions of this name. `CacheDirectory.BlockFileName` writes it and `CacheDirectory.EnumerateCached` reads a directory back into drive letters and serials, so a consumer never parses the name itself and a future format change is one edit rather than one edit per consumer.

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## Unreleased` / `### Added`:

```markdown
- `CacheDirectory.EnumerateCached` and the `CachedBlockFile` record list the drives a cache directory holds (drive letter, volume serial, path, size, last-write time) without opening or validating any block, so a consumer never reimplements the inverse of `CacheDirectory.BlockFileName`. A missing directory returns empty and an unrecognised file name is skipped rather than reported ([MFTLib#144](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/144))
```

- [ ] **Step 8: Commit**

```bash
git add MFTLib/Index/CachedBlockFile.cs MFTLib/Index/CacheDirectory.cs MFTLib.Tests/Index/CacheDirectoryEnumerationTests.cs docs/index-format.md CHANGELOG.md
git commit -F - <<'EOF'
feat(index): CacheDirectory enumerates its own cached drives (#144)

EnumerateCached returns one CachedBlockFile per file whose name round-trips
through BlockFileName, so the format stays private and a consumer stops
globbing *.mlix. Nothing is opened or validated; a missing directory is
empty and an unrecognised name is skipped.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

---

## Phase 5: MFTLib#118 - the namespace boundary is a test, not a convention

The ruling: one assembly stays; enforce the boundary with an IL-level architecture test.

- One architecture test in `MFTLib.Tests` using a maintained library (ArchUnitNET preferred; NetArchTest acceptable) that loads the built `MFTLib` assembly and asserts that types residing in namespace `MFTLib.Index` do not depend on any type residing in the exact namespace `MFTLib` or in `MFTLib.Interop`, with an explicit allowlist of the journal value types the index legitimately consumes.
- Negative control is mandatory: a deliberately violating type (namespace `MFTLib.Index`, referencing a broker or MFT type) lives in the test assembly, and a second test asserts the same rule flags it. A green run without the negative control is not acceptance.
- The allowlist is by full type name, not by namespace, and lives in the test next to a one-line reason per entry. Growing it is a review decision, not a mechanical edit.
- The test runs on both platforms. If the library cannot load the assembly on Linux, that is a blocker to report, not a reason to mark the test Windows-only.
- Known gap, documented in the test: a `const` inlined from a forbidden type leaves no IL reference. Accepted.

**Package decision, with the reason.** `TngTech.ArchUnitNET` 0.13.4 plus `TngTech.ArchUnitNET.MSTestV2` 0.13.4, both published 2026-08-20. Chosen because the ruling prefers ArchUnitNET, it is actively maintained (the fallback `NetArchTest.Rules` 1.3.2 has not shipped since 2021-05-23), it reads IL through `Mono.Cecil` rather than runtime reflection so it is pure managed code with no Windows-only surface, and it ships `netstandard2.0`, which net10.0 consumes. The MSTestV2 companion is what supplies the throwing `Check(Architecture)` extension; core alone has only the non-throwing `HasNoViolations` and `Evaluate`. One known sharp edge: `ResideInNamespace(string pattern, bool useRegularExpressions = false)` does a contains match by default (TNG/ArchUnitNET issue 126), so every namespace predicate in this task passes an anchored regex.

**Architecture decision this task locks.** Two `Architecture` objects, not one with an exclusion. The boundary test loads only the `MFTLib` assembly, so the production rule carries no fixture exclusion that could one day mask a real violation. The negative control loads `MFTLib` and `MFTLib.Tests` together and asserts the same rule object reports exactly the fixture. That is precisely the ruling's "passes only because the rule reports the fixture".

### Task 7: The architecture test and its negative control

**Files:**
- Modify: `MFTLib.Tests/MFTLib.Tests.csproj` (two `PackageReference` entries)
- Create: `MFTLib.Tests/Index/NamespaceBoundaryViolationFixture.cs`
- Create: `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Add the packages and confirm the exclusion combinator compiles**

In `MFTLib.Tests/MFTLib.Tests.csproj`, in the `ItemGroup` at lines 12-24:

```xml
    <PackageReference Include="TngTech.ArchUnitNET" Version="0.13.4" />
    <PackageReference Include="TngTech.ArchUnitNET.MSTestV2" Version="0.13.4" />
```

Then confirm the exact name of the negated full-name predicate in 0.13.4 before writing the rule, because the research that chose this package could not pin it from public docs. Run:

```bash
dotnet restore MFTLib.Tests/MFTLib.Tests.csproj
grep -o 'DoNotHaveFullName[^<]*' ~/.nuget/packages/tngtech.archunitnet/0.13.4/lib/netstandard2.0/ArchUnitNET.xml | head
```

If `DoNotHaveFullName` appears, use it as written in Step 3. If it does not, search the same XML documentation file for the available negated name predicates (`grep -o '<member name="M:ArchUnitNET[^"]*DoNotHave[^"]*"' ...`) and use whichever one takes a full type name. Record the one that compiled in a one-line comment above the allowlist. Do not invent a method name and do not work around the allowlist by switching to a namespace-level exclusion: the ruling requires full type names.

Check `.Because(string)` the same way while you are in that file (`grep -o 'Because[^<]*' ...`). It is a readability nicety, not a requirement: if it is not on `IArchRule` in 0.13.4, drop the `.Because(...)` call from Step 3's rule and move that sentence into the method's doc comment instead.

- [ ] **Step 2: Write the failing tests and the fixture**

Create `MFTLib.Tests/Index/NamespaceBoundaryViolationFixture.cs`:

```csharp
// This file is deliberately in namespace MFTLib.Index, inside the test assembly, and it
// deliberately references a type the MFTLib.Index namespace boundary forbids. It exists only so
// NamespaceBoundaryTests can prove the boundary rule reports a violation when one is present. A
// green boundary rule with no negative control proves nothing, which is why MFTLib#118 makes this
// fixture mandatory. Nothing in production references it and nothing should.
namespace MFTLib.Index;

static class NamespaceBoundaryViolationFixture
{
    internal static MftRecord ForbiddenReference()
    {
        return default;
    }
}
```

A method whose return type is the forbidden type leaves an unambiguous IL type reference, which is what the rule reads. If aislop or Roslynator reports this fixture as dead code or an unused member, report it to the controller rather than deleting it or adding a suppression: the `#118` ruling makes the fixture mandatory, and a suppression decision is the owner's.

`MFTLib.MftRecord` is a public readonly struct declared at `MFTLib/Mft/MftRecord.cs:43`, in the flat `MFTLib` namespace, compiled on both platforms, and not on the allowlist. It is the "MFT type" the ruling names.

Create `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`:

```csharp
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#118. The packed index is substrate-neutral by design: the block format, queries,
///     snapshots, mutation and the enumeration producer must not reach into the MFT, broker,
///     elevation or interop code. That is a namespace boundary, and because this repository uses
///     a flat <c>MFTLib</c> namespace, <c>namespace MFTLib.Index</c> resolves every one of those
///     types with no <c>using</c> at all, so the boundary can be crossed by accident and no
///     import-based lint can see it. This test reads the built assembly's IL instead.
/// </summary>
/// <remarks>
///     Known and accepted gap: a <c>const</c> inlined from a forbidden type leaves no IL
///     reference, so this rule cannot see it. Everything else (fields, parameters, return types,
///     locals, method calls, attributes, generic arguments) does leave one.
/// </remarks>
[TestClass]
public class NamespaceBoundaryTests
{
    // Anchored regexes because ArchUnitNET's ResideInNamespace does a contains match by default
    // (TNG/ArchUnitNET issue 126), which would let "MFTLib" match "MFTLib.Index" and make the
    // whole rule vacuous. The forbidden side is one regex covering both namespaces rather than
    // two predicates joined by Or(), so the allowlist's And() clauses below cannot end up bound
    // to only one half of an Or().
    const string IndexNamespacePattern = @"^MFTLib\.Index$";
    const string ForbiddenNamespacePattern = @"^MFTLib(\.Interop)?$";

    static readonly Architecture ProductionArchitecture =
        new ArchLoader().LoadAssemblies(typeof(FileIndex).Assembly).Build();

    static readonly Architecture ProductionAndFixtureArchitecture =
        new ArchLoader().LoadAssemblies(
            typeof(FileIndex).Assembly,
            typeof(NamespaceBoundaryTests).Assembly).Build();

    /// <summary>
    ///     The journal value types the index legitimately consumes, by full name, one reason each.
    ///     Growing this list is a review decision, not a mechanical edit: every addition widens
    ///     what the index is allowed to know about.
    /// </summary>
    static IArchRule BoundaryRule()
    {
        var forbidden = Types().That()
            .ResideInNamespace(ForbiddenNamespacePattern, useRegularExpressions: true)
            // The journal batch a JournalMutator applies is a value type, not a substrate. It
            // carries no volume handle and no native dependency.
            .And().DoNotHaveFullName("MFTLib.UsnJournalEntry")
            // The options record that shapes a UsnJournalEntry. Same reason as above.
            .And().DoNotHaveFullName("MFTLib.UsnJournalEntryOptions")
            // The reason flags on a journal entry. A plain [Flags] enum of uint.
            .And().DoNotHaveFullName("MFTLib.UsnReason");

        // MFTLib#118's ruling also names UsnJournalId. There is no such type: the journal id
        // crosses the boundary as a plain ulong (BlockHeader.UsnJournalId at BlockHeader.cs:32),
        // so there is nothing to allow. Recorded here rather than silently dropped.

        return Types().That().ResideInNamespace(IndexNamespacePattern, useRegularExpressions: true)
            .Should().NotDependOnAny(forbidden)
            .Because("the packed index is substrate-neutral and must not reach into the MFT, " +
                     "broker, elevation or interop code");
    }

    [TestMethod]
    public void MFTLibIndex_DoesNotDependOnTheFlatNamespaceOrInterop()
    {
        var rule = BoundaryRule();
        if (rule.HasNoViolations(ProductionArchitecture))
        {
            return;
        }

        Assert.Fail(string.Join(Environment.NewLine,
            rule.Evaluate(ProductionArchitecture).Select(result => result.Description)));
    }

    /// <summary>
    ///     The negative control MFTLib#118 makes mandatory. The same rule object, evaluated over an
    ///     architecture that also holds <see cref="NamespaceBoundaryViolationFixture" />, must
    ///     report a violation and must name that fixture. If this ever passes for a different
    ///     reason, or the boundary test above passes while this one does too, the rule has stopped
    ///     detecting anything and the green run above means nothing.
    /// </summary>
    [TestMethod]
    public void TheBoundaryRule_ReportsADeliberateViolation()
    {
        var rule = BoundaryRule();

        Assert.IsFalse(rule.HasNoViolations(ProductionAndFixtureArchitecture),
            "the rule must report the deliberate violation in NamespaceBoundaryViolationFixture");

        var descriptions = string.Join(Environment.NewLine,
            rule.Evaluate(ProductionAndFixtureArchitecture).Select(result => result.Description));
        StringAssert.Contains(descriptions, nameof(NamespaceBoundaryViolationFixture));
        StringAssert.Contains(descriptions, "MftRecord");
    }
}
```

- [ ] **Step 3: Run the tests to verify the negative control fails first**

The order matters here, because a boundary test that passes by accident is the failure mode `#118` exists to prevent. Temporarily change `NamespaceBoundaryViolationFixture` to sit in `namespace MFTLib.Tests.Index` instead of `namespace MFTLib.Index`, then run:

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~NamespaceBoundaryTests" --logger "console;verbosity=minimal"`
Expected: `MFTLibIndex_DoesNotDependOnTheFlatNamespaceOrInterop` PASSES (the boundary really is clean today) and `TheBoundaryRule_ReportsADeliberateViolation` FAILS with `Assert.IsFalse failed. the rule must report the deliberate violation`. That is the proof the rule is looking at namespace membership and not at nothing.

Restore the fixture to `namespace MFTLib.Index` and run again.
Expected: both PASS, with the second one's `StringAssert.Contains` finding the fixture name.

- [ ] **Step 4: Confirm the allowlist is exactly right, and stop if it is not**

Temporarily delete the three `DoNotHaveFullName` lines and run the boundary test again. It must fail, and the violations it reports must name only `MFTLib.UsnJournalEntry` and `MFTLib.UsnReason` (grep at `5462650` finds no `MFTLib.Index` reference to `UsnJournalEntryOptions`, so its allowlist entry is a forward-looking allowance the ruling names rather than a current dependency).

If a fourth type appears, STOP and report it to the controller with the type's full name and the `MFTLib/Index/` file that reaches it. Growing the allowlist is a review decision, not this task's to make. Restore the three lines.

- [ ] **Step 5: Confirm both platforms**

Run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj --filter "FullyQualifiedName~NamespaceBoundaryTests" --logger "console;verbosity=minimal"`
Expected on Linux: PASS, 2 tests. If `ArchLoader` cannot load the `MFTLib` assembly on Linux, that is a blocker to report to the controller, not a reason to add a platform guard or an `Assert.Inconclusive`. The ruling says so explicitly.

Windows is owner-attended; Task 9 records it.

- [ ] **Step 6: Commit**

```bash
git add MFTLib.Tests/MFTLib.Tests.csproj MFTLib.Tests/Index/NamespaceBoundaryTests.cs MFTLib.Tests/Index/NamespaceBoundaryViolationFixture.cs
git commit -F - <<'EOF'
test(index): enforce the MFTLib.Index namespace boundary in IL (#118)

ArchUnitNET 0.13.4 reads the built assembly's IL, which is the only place
the boundary is visible: the forbidden folders share the flat MFTLib
namespace, so there is no using to lint. A deliberate violator in the test
assembly proves the rule reports violations rather than passing vacuously.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

No `CHANGELOG.md` entry: this task adds no library behaviour and no public API. The changelog documents what a consumer of the `MFTLib` package sees, and a test-project package reference is not that.

### Task 8: Documentation

The ruling for `#118` says spec decision 5 is amended from "aislop architecture rule" to "architecture test", and the README and AGENTS wording follows. This task also picks up the doc drift the four earlier phases created.

**Files:**
- Modify: `docs/superpowers/specs/2026-09-02-packed-index-design.md:44-46` and `:356`
- Modify: `AGENTS.md:129-135`
- Modify: `README.md:332-352` and `:383-390`

**Note on this plan's own lifecycle:** `superpowers:writing-plans` normally deletes the spec once the plan absorbs it. That does not apply here. `docs/superpowers/specs/2026-09-02-packed-index-design.md` is the packed index's living design record, it is referenced by open issues, and the `#118` ruling directs an amendment to it rather than its deletion. Amend it; do not delete it. This plan is still scaffolding and is still deleted at branch finish.

- [ ] **Step 1: Amend spec decision 5**

`docs/superpowers/specs/2026-09-02-packed-index-design.md:44-46` currently reads:

```
5. One assembly, one NuGet package, name stays MFTLib. Layering is a namespace
   boundary enforced by an aislop architecture rule: `MFTLib.Index` never
   references `MFTLib.Mft`, `MFTLib.Broker`, or `MFTLib.Internal`. The README
   gets a sentence explaining the not-just-MFT scope.
```

Replace with:

```
5. One assembly, one NuGet package, name stays MFTLib. Layering is a namespace
   boundary enforced by an architecture test: `MFTLib.Tests` loads the built
   `MFTLib` assembly with ArchUnitNET and asserts that types in namespace
   `MFTLib.Index` depend on nothing in the exact namespace `MFTLib` or in
   `MFTLib.Interop`, apart from an explicit per-type allowlist of journal value
   types. An import rule cannot see this boundary: the forbidden folders share
   the flat `MFTLib` namespace, so the crossing needs no `using`. A deliberate
   violator in the test assembly proves the rule reports violations. The README
   gets a sentence explaining the not-just-MFT scope.
```

`:356` currently reads `5. Docs, README, aislop architecture rule, measurement, then the 0.3.0 track`. Replace `aislop architecture rule` with `architecture test`.

- [ ] **Step 2: Amend AGENTS.md**

Under the `**MFTLib** (C# Library)` bullet at `AGENTS.md:129`, add a sub-bullet in the same style as the ones at `:130-135`:

```markdown
    - **Index namespace boundary**: `MFTLib.Index` depends on nothing in the flat `MFTLib` namespace or in `MFTLib.Interop` beyond an allowlist of journal value types (`UsnJournalEntry`, `UsnJournalEntryOptions`, `UsnReason`). Enforced by `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`, an IL-level ArchUnitNET test over the built assembly, with a mandatory negative-control fixture. Not an aislop rule: the forbidden folders share the flat `MFTLib` namespace, so there is no `using` for an import rule to match. Growing the allowlist is a review decision.
```

- [ ] **Step 3: Amend README.md**

Two edits. First, the source-layout list at `:383-390` omits `MFTLib/Index` entirely; add it with the boundary sentence:

```markdown
- `MFTLib/Index` - the substrate-neutral packed index: block format, `FileIndex`, snapshots, queries, mutation, and the enumeration producer. It is not MFT-specific and depends on nothing else in the library beyond a few journal value types, a boundary an architecture test enforces.
```

Second, in the `## Build a live index with FileIndex` section at `:332-352`, add a paragraph covering the three consumer-visible changes from this branch:

```markdown
`FileEntry.Path` is a real filesystem path: the drive block's root directory joined
with the entry's name chain using the host separator. It can be opened, and
`FileIndex.Find` accepts it back, resolving a native path against the longest
matching indexed root. Disposing a `FileIndex` releases every block mapping it
holds, so the `.mlix` files are closed at a point the caller chooses; a `FileEntry`
held across that disposal reports `IsDisposed` and throws `ObjectDisposedException`
on every read. `DriveStatus.BlockSource` says whether a drive warm-started from
cache or was scanned, so a rebuild loop can skip the drives an open already scanned,
and `CacheDirectory.EnumerateCached` lists the drives a cache directory holds
without a consumer parsing block file names.
```

- [ ] **Step 4: Verify no doc still describes the old behaviour**

Run:

```bash
cd /home/schoen/MFTLib-worktrees/ruled-issues
grep -rn "aislop architecture rule\|architecture: false" README.md AGENTS.md docs/ .aislop/config.yml
grep -rn "logical path\|drive-letter path\|synthetic path\|rooted at the configured drive letter" README.md AGENTS.md docs/ MFTLib/
grep -rn "finalizer is the release path" MFTLib/ docs/
```

`.aislop/config.yml`'s `architecture: false` stays as it is: the boundary is now a test, so there is no aislop architecture rule to enable, and changing `.aislop/config.yml` is off-limits without the owner's consent. Every other hit is doc drift to fix.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-02-packed-index-design.md AGENTS.md README.md
git commit -F - <<'EOF'
docs: the index namespace boundary is a test, and Path is real (#118 #143 #145 #146 #144)

Spec decision 5 and the AGENTS architecture section describe the
ArchUnitNET test rather than an aislop rule, because the forbidden folders
share the flat MFTLib namespace and an import rule cannot see the crossing.
README picks up the real-path, deterministic-release, BlockSource and
cache-enumeration changes.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

---

## Phase 6: The gate

### Task 9: Full managed run on Linux plus the quality gate

This task produces the evidence the branch is judged on. It writes no production code. If it finds a failure, the fix belongs in the task that caused it, not here.

**Files:**
- Modify: `TEST-REPORT.md`

- [ ] **Step 1: Run the full Linux coverage script**

Run:

```bash
cd /home/schoen/MFTLib-worktrees/ruled-issues
timeout 1800 ./scripts/coverage-linux.sh
```

Expected shape of the output, in order: `==> [native] configuring (Debug + coverage)`, `==> [native] building`, `==> [native] clearing previous gcda data`, `==> [native] running smoke tests`, `==> [managed] dotnet test with coverlet`, then MSTest's summary line in the form `Passed!  - Failed: 0, Passed: <n>, Skipped: <k>, Total: <n plus k>, Duration: <d>`, then coverlet's per-module table, then `==> [native] coverage summary` with gcovr's line and branch percentages, then `==> [managed] coverage summary`. Exit status 0.

The managed pass runs with the filter the script builds at its lines 73-83, which excludes five whole test classes and four individual tests that need Windows behaviour. Those exclusions are pre-existing and are not this branch's to change.

Record: the passed and failed counts, the managed line and branch coverage from `coverage-report/managed/coverage.cobertura.xml`, and the wall time. If anything fails, capture the failing test's full name and message before doing anything else.

- [ ] **Step 2: Confirm every changed production line is covered**

The gate `TEST-REPORT.md` states is not a global percentage; it is `Every changed executable production line was covered`. Diff the branch against its base and check each changed production file against the cobertura report:

```bash
cd /home/schoen/MFTLib-worktrees/ruled-issues
git diff --stat 5462650..HEAD -- MFTLib/
```

For each of `MFTLib/Index/Snapshot.cs`, `FileEntry.cs`, `FileEntry.Navigation.cs`, `FileEntry.Open.cs`, `FileIndex.cs`, `FileIndex.Queries.cs`, `FileIndex.Scanning.cs`, `FileIndex.Rescan.cs`, `IndexNavigation.cs`, `LookupEngine.cs`, `DriveStatus.cs`, `BlockSource.cs`, `CacheDirectory.cs` and `CachedBlockFile.cs`, confirm the new and changed executable lines appear with a non-zero hit count in `coverage-report/managed/coverage.cobertura.xml`. Name any line that does not, and either add the test that reaches it or state why it is unreachable.

- [ ] **Step 3: Run the quality gate**

Run:

```bash
cd /home/schoen/MFTLib-worktrees/ruled-issues
aislop scan .
```

Expected shape: a per-engine table (format, lint, code-quality, ai-slop, security; architecture is disabled in `.aislop/config.yml`), a score out of 100, and a findings list. The bar is `TEST-REPORT.md`'s: zero findings in changed files. `.aislop/config.yml` sets `ci.failBelow: 100`, so the exit status is non-zero while any inherited finding stands; that is expected and is not this branch's failure as long as no finding names a file this branch touched.

Watch specifically for: `maxFileLoc: 400` on `IndexNavigationTests.cs` and `LookupEngineTests.cs`, both of which this branch grows; `maxParams: 6` on `FileIndex.DescribeDrive`, which Task 5 keeps at two by bundling; and a narrative-comment finding on any of the long doc comments the tasks add.

Note for the controller: per this plan's Global Constraints, lanes do not run aislop, jb/inspectcode, or `dotnet format`. This step exists so the gate task's report says what the controller's own run should find. If a lane is executing this task, report the commands and the intent rather than running the gates.

- [ ] **Step 4: State what is platform-conditional**

Windows evidence is owner-attended on the chonkers box and is not produced by this task. Write down, in the report, exactly which assertions are Linux-verified and which still need Windows:

Linux-verified by Step 1:
- every test in Phases 1 through 5, under the script's standing filter;
- `#145`'s file-release assertion through the `/proc/self/fd` branch of `BlockFileHoldAssertions`;
- `#143`'s enumeration-block case rule, which on Linux takes the ordinal branch;
- `#143`'s real-path round trip over a temp subtree;
- `#118`'s boundary test and negative control.

Needs Windows, owner-attended, `.\scripts\run-coverage.ps1`:
- `#145`'s `FileShare.None` branch of `BlockFileHoldAssertions.AssertNotHeld`. On Linux a held file can still be deleted, so the `File.Delete` half of `DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile` only proves anything on Windows.
- `#143`'s claim that Windows MFT blocks are byte-identical before and after. The Windows-only test classes that exercise real MFT blocks (`MftResultTests`, `MftVolumeTests`, `NativeCoverageTests`, `NativeParserCoverageTests`, `UsnJournalSyntheticTests`) do not compile on Linux at all, per `MFTLib.Tests/MFTLib.Tests.csproj`.
- `#143`'s enumeration-block case rule on a case-insensitive host, the `Assert.IsNotNull` branch of `Find_AnEnumerationBlockFollowsTheHostCaseRule`.
- `#118`'s "passes on Windows CI" half of the ruling's acceptance.
- The four individual tests `scripts/coverage-linux.sh` filters out by name at its lines 79-82.

- [ ] **Step 5: Rewrite TEST-REPORT.md**

Replace `TEST-REPORT.md` with this run's figures, keeping its existing table shape (`Status`, `Mode`, `Git`, `Tests`, `Timeout`, `Coverage`, `Coverage limits`, `Lint`, `Native binary`, `Platform`) and its `## Regression evidence`, `## Remaining diagnostics` and `## Commands` sections. Specifics for this run:

- `Git`: the branch name `feat/index-ruled-issues`, the head commit, and that it was verified before commit.
- `Platform`: Linux verified by `scripts/coverage-linux.sh`; Windows verification is owner-attended and pending, with Step 4's list of what it covers.
- `## Regression evidence`: for each of `#145`, `#143`, `#146` and `#144`, the one line saying which test failed before the change and with what message, and that it passed after. Those messages were recorded in each task's RED step. `#118`'s regression evidence is its negative control: the rule reported nothing when the fixture sat outside `namespace MFTLib.Index` and reported it once it was moved in.
- `## Commands`: the two commands from Step 1 and Step 3 of this task, plus the `TEST-REPORT.md`-quoted Windows command for the pending half.

- [ ] **Step 6: Commit**

```bash
git add TEST-REPORT.md
git commit -F - <<'EOF'
docs: test report for the index ruled-issues train

Linux full managed run through scripts/coverage-linux.sh plus the aislop
scan. Names the Windows assertions that are owner-attended and still
pending: the FileShare.None branch of the block-release check, the
case-insensitive branch of the enumeration case rule, the Windows-only MFT
test classes, and #118 on Windows CI.

Co-Authored-By: <harness> <model> <noreply@anthropic.com>
EOF
```

No `CHANGELOG.md` entry: this task changes no library behaviour.

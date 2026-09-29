### Task W40: Ranged block flush with a per-range callback (Sonnet)

Carved out of task C5 by orchestrator ruling W4-3 so that neither C5 nor B5 sweeps callers of
`BlockWriter.Complete` in wave 4. C5 later passes a real callback from the broker host; this task
builds the mechanism and updates every caller.

**Files:**
- Create: `MFTLib/Internal/Libc.cs` (`msync` for the non-Windows ranged flush), a test file
  `MFTLib.Tests/Index/BlockFileRangedFlushTests.cs`
- Modify: `MFTLib/Index/BlockFile.cs` (ranged flush), `MFTLib/Index/BlockWriter.cs`,
  `MFTLib/Internal/Kernel32.cs` (add `FlushViewOfFile`), and every caller of
  `BlockWriter.Complete` and `BlockFile.Flush` in production and test code (Grep `\.Complete\(`
  and `\.Flush\(`; at the base the production callers are
  `MFTLib/Index/FileIndex.Scanning.cs` and `MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs`,
  and the test callers are in about 22 test files plus
  `MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs`)

**Interfaces produced (exact signatures; the old one-argument and zero-argument forms are
deleted, not kept as overloads, and no default parameter value is added):**

```csharp
public void Flush(Action<long>? rangeFlushed);          // BlockFile: 64 MB ranges through FlushViewOfFile (Windows) or msync (elsewhere), on the view pointer plus offset
public void Complete(DateTime scanTimestampUtc, Action<long>? rangeFlushed); // BlockWriter; every caller updated
```

**Behavior:**
- `BlockFile.Flush` flushes the mapped view in consecutive ranges of at most 64 MB
  (`internal const long FlushRangeBytes = 64L * 1024 * 1024;` on `BlockFile`), in ascending offset
  order, covering the whole view exactly once; the last range is the remainder. Each range is
  flushed through `FlushViewOfFile` on Windows and `msync` with `MS_SYNC` elsewhere, called on the
  view's base pointer plus the range offset. After each range completes, `rangeFlushed`, when not
  null, is invoked with the number of bytes flushed so far (the end offset of that range). A
  failed native call throws (`Win32Exception` from the last error on Windows; `IOException`
  carrying `errno` elsewhere) and no callback is made for the failed range.
- On Windows, after the last range the file's metadata and data are made durable the way
  `MemoryMappedViewAccessor.Flush()` did it (that method calls `FlushViewOfFile` then
  `FlushFileBuffers`); keep whatever durability step the existing `Flush()` provided. Read the
  .NET source behavior before deciding and state in your report what the old call did and what
  the new code does.
- `msync` needs a page-aligned address. The view's base pointer may carry a pointer offset inside
  its first page; align the first range's start down to the page boundary the way the runtime
  does, and say in your report how you verified it.
- `BlockWriter.Complete` keeps its current behavior and passes `rangeFlushed` to `Block.Flush`.
- Every existing caller passes `null`. `RealBlockSectionWriter` passes `null` in this task; C5
  wires its reporter there.
- The disposal and access-scope rules of `BlockFile` and `BlockWriter` are unchanged: `Flush`
  still throws `ObjectDisposedException` after disposal.

**Tests (write first, see each fail for the stated reason):**
- `Flush_ViewLargerThanOneRange_ReportsEachRangeEndInOrder`: the range size must be injectable
  for the test so no test maps hundreds of megabytes. Use an internal seam (for example an
  internal overload or internal settable range size on the instance, reachable through
  `InternalsVisibleTo`); not a public member, not a static. With a small range size and a block
  whose view is a little over two ranges, the callback receives the ascending end offsets and the
  last one equals the view length.
- `Flush_ViewSmallerThanOneRange_ReportsOnceWithTheViewLength`.
- `Flush_NullCallback_Flushes` (the data written before the flush is read back from the file
  through a separate read after disposal).
- `Flush_AfterDispose_Throws`.
- `Complete_PassesTheCallbackToTheFlush`: the callback given to `Complete` receives the final
  offset equal to the view length, and the block is complete afterwards.
- `Flush_CallbackThrows_PropagatesAndLeavesTheBlockUsable`.
- If the native call can be failed through an existing seam without new production test-only
  surface, a failure test; otherwise say in the report that the failure branch has no test and
  which lines it leaves uncovered.

These tests run on Windows and on Linux. Do not add platform exclusions.

**Gate:** green (build, targeted tests, whole suite through `run-coverage.ps1 -NonInteractive`,
aislop with no finding beyond the four baseline warnings and the ruled 8-parameter
`JournalBrokerHost` constructor). The Linux run is done by the orchestrator on the merged head;
write `Libc.cs` carefully (`[DllImport("libc", SetLastError = true)]` or `LibraryImport`,
matching how `Kernel32.cs` declares imports).

**Commit message:** `Block flush runs in 64 MB ranges and reports each range`

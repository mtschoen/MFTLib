### Task A1: Native per-chunk thread allowance and parse cancellation (Opus)

Opus because the thread allowance and the cancellation flag are read by the chunk loop, the I/O thread and every parse worker, and a cancelled parse must free the double buffers, the worker slices and the partial result on every exit path. Implements amendments S3 (native side: the parser takes its thread count from a shared allowance, once per chunk) and R7.

**Where the rebalance happens (read at `c1d4378`).** The parser already creates its workers fresh for every chunk and joins them before the chunk ends: `ParseChunkParallel` sizes its slices and spawns `numThreads` workers (`MFTLibNative/mft/mft.parse_core.cpp:110-137`) and joins them (`:138-140`), and `ParseAllChunks` calls it once per chunk from its loop (`:323-369`, the call at `:331-336`). The count is a plain `unsigned` fixed once per scan at `:405` (`EffectiveThreadCount()`, `MFTLibNative/core/test_hooks.cpp:36-43`), passed to `ParseAllChunks` (`:420`) and to `ResolveAllPaths` (`:443`). Nothing in the parser has to be restructured to change the count between chunks. The plan changes two read points:

- The rebalance point is the top of the `while (currentChunkSize > 0)` iteration in `ParseAllChunks` (`:323`), before the I/O thread starts (`:326`) and before the `numThreads > 1` branch (`:331`): each iteration calls `EffectiveThreadCount(control)` and uses that value for this chunk only. A chunk in progress finishes with the count it started with, which is the accepted limit of owner ruling 6.
- Path resolution reads the control block again immediately before `ResolveAllPaths` (`:443`), so a scan late in its life resolves paths with its current share.

**How the next chunk's count is obtained.** The export takes one pointer to a caller-owned control block, `MftParseControl { int32_t cancelRequested; int32_t parseThreadAllowance; }` (spec 2.3), which the managed caller keeps pinned for the call; the broker's `ParseThreadAllowance` writes through to it while the parse runs, and the parser only reads it. The native code reads both fields atomically. `EffectiveThreadCount(const MftParseControl* control)` reads `parseThreadAllowance` once: 0, or a null control block, means every processor; any other value is clamped to `[1, hardware_concurrency]`; `SetMaxThreads` still caps the result (`test_hooks.cpp:39-41`). `cancelRequested` is read at the same loop top (before each chunk read), after each chunk's parse, by each worker between 4096-record sub-slices, and between path-resolution slices.

**Files:**
- Modify: `MFTLibNative/internal.h` (`unsigned EffectiveThreadCount(const MftParseControl* control)` replaces the declaration at `:44`; `void RecordChunkThreadCount(unsigned)` and `void RecordResolveThreadCount(unsigned)`), `MFTLibNative/core/test_hooks.cpp` (the new `EffectiveThreadCount`, the two record functions, the `GetChunkThreadCounts` and `GetResolveThreadCount` hooks, reset in `ResetTestState`), `MFTLibNative/mft/mft.parse_core.cpp` (`ParseMFTImpl` and `ParseAllChunks` take the control block in place of the fixed `numThreads`; the per-chunk read and the pre-resolution read above; a cancelled parse frees everything and returns a result whose `errorMessage` is `L"Parse cancelled"` and whose new `cancelled` field is 1), `MFTLibNative/mft/mft.parse.cpp` (both platform `ParseMFTRecordsWithProgress` exports gain the control-block parameter; `ParseMFTRecords` and `ParseMFTFromFile` pass null), `MFTLibNative/mft/mft_synthetic.cpp` (`:314` passes null), `MFTLibNative/mft_api.h:48` (`struct MftParseResult`: add `uint32_t cancelled` at the end of `MftParseResult` and declare `MftParseControl` beside it, bump `MFT_NATIVE_ABI_VERSION` because the stride check reads the struct), `MFTLib/Interop/MftParseResult.cs`, `MFTLib/Internal/MFTLibNative.cs` (P/Invoke and seam), `MFTLib/Mft/MftVolume.cs` (`StreamRecords` and `ReadRecordBatches` gain the allowance and a `CancellationToken`; a token registration sets `cancelRequested` in the pinned control block; a cancelled result throws `OperationCanceledException` from managed code)
- Create: `MFTLib/Mft/ParseThreadAllowance.cs`, `MFTLib.Tests/ParseThreadAllowanceTests.cs`
- Test: `MFTLib.Tests/NativeParserCoverageTests.cs`, `MFTLib.Tests/MftVolumeTests.cs`, `MFTLib.Tests/MftResultTests.cs`

**Interfaces produced:**

```cpp
struct MftParseControl { int32_t cancelRequested; int32_t parseThreadAllowance; }; // caller-owned, pinned for the call, read atomically
unsigned EffectiveThreadCount(const MftParseControl* control); // null or allowance 0: hardware_concurrency; else the value read once, clamped to [1, hardware_concurrency]; SetMaxThreads still caps
EXPORT MftParseResult* ParseMFTRecordsWithProgress(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags,
    uint32_t bufferSizeRecords, const MftParseControl* control, MftProgressCallback callback, void* context);
EXPORT unsigned GetChunkThreadCounts(unsigned* counts, unsigned capacity); // test hook: the count each chunk of the last ParseMFTImpl used, in order; returns how many were recorded (at most capacity)
EXPORT unsigned GetResolveThreadCount();                                   // test hook: the count path resolution of the last ParseMFTImpl used; 0 when it did not run
```

```csharp
namespace MFTLib;
public sealed class ParseThreadAllowance
{
    public ParseThreadAllowance(int count);            // count >= 1
    public int Count { get; set; }                     // below 1 throws ArgumentOutOfRangeException; while a parse runs, a write goes through to its control block
}
public MftResult StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress,
    ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);      // null: every processor
public IEnumerable<MftRecord[]> ReadRecordBatches(bool resolvePaths, int batchSize,
    IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);
```

`StreamRecords` and `ReadRecordBatches` pin one `MftParseControl` for the call, attach the allowance to it (an internal attach and detach on `ParseThreadAllowance`) and detach it before the block is freed; a null allowance leaves `parseThreadAllowance` 0. The broker's allocator is the only production writer of `Count`. The existing optional-parameter shapes of `StreamRecords` and `ReadRecordBatches` are replaced, not kept beside the new ones; every caller in `MFTLib`, `TestProgram`, `Benchmark` and the tests passes the new arguments (Grep each method name). README examples are updated in D3.

- [ ] **Step 1: Failing tests.** The native cases use a synthetic NTFS file as in `ParseMFTRecordsWithProgress_RealCallback_ReportsPerChunkProgress` (`:299`) with `bufferSizeRecords` 64 so several chunks run; the progress callback fires after each chunk is parsed (`mft.parse_core.cpp:358-361`), so a change it makes can only affect later chunks.
  - `NativeParserCoverageTests.ParseMFTRecordsWithProgress_AllowanceNull_EveryChunkUsesEveryCore`.
  - `..._AllowanceTwo_EveryChunkUsesAtMostTwoThreads`: every entry of `GetChunkThreadCounts` equals `Math.Min(2, Environment.ProcessorCount)`.
  - `..._AllowanceAboveCores_ClampsToCores`, `..._AllowanceZero_MeansEveryCore` (native level; the managed type rejects 0), `..._SetMaxThreadsStillCaps` (`SetMaxThreads(1)`, allowance 4, every chunk 1).
  - `..._AllowanceLoweredFromProgressCallback_LaterChunksUseTheNewCount`: the allowance starts at `Environment.ProcessorCount`; the callback sets it to 1 on its first invocation; the first recorded count is `Environment.ProcessorCount` (the chunk that callback followed keeps its count) and every later one is 1.
  - `..._AllowanceRaisedFromProgressCallback_LaterChunksUseTheNewCount`: the reverse.
  - `..._PathResolutionReadsAllowanceAfterLastChunk`: `MatchFlags.ResolvePaths`; the callback sets the allowance to 1 on its last parsing invocation; `GetResolveThreadCount()` is 1 while the last chunk's recorded count was `Environment.ProcessorCount`.
  - `..._CancelledDuringPathResolution_StopsBetweenSlices` (the flag is set from the resolution progress callback; the result is cancelled and freed).
  - `..._CancelledBeforeFirstChunk_ReturnsCancelledWithoutReading`: flag set before the call; the progress callback never fires; `cancelled` is 1.
  - `..._CancelledFromProgressCallback_StopsAfterThatChunk`: the callback sets the flag on its first invocation; exactly one parsing callback fires before return; `cancelled` is 1; `FreeMftResult` succeeds (no leak detector exists, so the native coverage run must cover the cancel exits).
  - `MftVolumeTests.ReadRecordBatches_AllowanceLoweredBetweenChunks_LaterChunksUseTheNewCount` (through the managed seam, `ParseThreadAllowance.Count` written from the progress callback) and `ReadRecordBatches_TokenCancelledDuringParse_ThrowsBeforeFirstBatch`.
  - `ParseThreadAllowanceTests.Count_WrittenDuringParse_IsVisibleToTheNativeControlBlock` (including after a garbage collection), `Constructor_CountBelowOne_Throws`, `Count_SetBelowOne_Throws`.
  - `MftResultTests.AbiStride_MatchesCancelledField`.
  - Classes touching the hooks keep or gain class-level `[DoNotParallelize]`.
- [ ] **Step 2: See them fail.**
- [ ] **Step 3: Implement.** Workers exit their slice loop when the flag is set; the chunk loop joins the I/O thread before returning; every allocation made by `ParseMFTImpl` is freed on the cancel path, as on the existing `!parsedOk` path (`mft.parse_core.cpp:425-434`).
- [ ] **Step 4: Verify** (standard) plus native Release and Debug builds, `.\scripts\native-coverage.ps1` in the background (the cancel branches must be covered), and `./init.sh --build` on Linux for the `#ifndef _WIN32` stub at `mft.parse.cpp:394`.
- [ ] **Step 5: Commit:** "Native parse rebalances its thread count at every chunk and stops promptly when cancelled".

**Gate:** green. **Depends on:** none.


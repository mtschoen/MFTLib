### Task A4: Bounded journal reads

Runs in wave 2, after A1 merges, because it edits `MFTLib/Internal/MFTLibNative.cs`, which A1 edits in wave 1.

**Files:** Modify `MFTLibNative/usn/usn_journal.cpp` (`ReadUsnJournal` gains `uint32_t maximumBufferReads`, 0 reads to the tip), `MFTLib/Internal/MFTLibNative.cs` (that P/Invoke only), `MFTLib/Journal/MftVolume.Journal.cs` (internal `ReadUsnJournalBounded`; public `ReadUsnJournal(since)` passes 0). Test: `MFTLib.Tests/UsnJournalSyntheticTests.cs`.

```cpp
EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId, uint32_t maximumBufferReads);
```
```csharp
internal (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournalBounded(UsnJournalCursor since, int maximumBufferReads);
```

- [ ] **Failing tests:** `ReadUsnJournal_MaximumBufferReadsOne_StopsAfterOneRead` (three buffers queued with `SetUsnIoSuccess`; a bounded read of 1 returns the first buffer's entries and `NextUsn`; a second call returns the second's); `ReadUsnJournal_MaximumBufferReadsZero_ReadsToTip`. See them fail; implement the loop bound (`for (uint32_t reads = 0; maximumBufferReads == 0 || reads < maximumBufferReads; reads++)`); verify (standard, plus native builds); commit "Journal reads take a buffer bound".

**Gate:** green. **Depends on:** A1 (shared file). **Parallel with:** B1, C1. C1 owns the broker's 64 MB scan chunk.


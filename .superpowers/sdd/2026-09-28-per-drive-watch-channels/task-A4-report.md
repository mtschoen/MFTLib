# Task A4 report: Bounded journal reads

Status: DONE. Commit f78a8a6 "Journal reads take a buffer bound" on task/265-A4 (base 305fab2).

## Implemented
- usn_journal.cpp: ReadUsnJournal takes uint32_t maximumBufferReads; loop is
  `for (uint32_t reads = 0; maximumBufferReads == 0 || reads < maximumBufferReads; reads++)`. Result struct unchanged, so ABI version untouched.
- MFTLibNative.cs: P/Invoke and `_readUsnJournal` seam gain a `uint` fourth argument (nothing of A1's touched).
- MftVolume.Journal.cs: internal `ReadUsnJournalBounded(since, maximumBufferReads)` (negative throws ArgumentOutOfRange); public `ReadUsnJournal(since)` passes 0. Broker host untouched.
- Tests added in UsnJournalSyntheticTests.cs: ReadUsnJournal_MaximumBufferReadsOne_StopsAfterOneRead, ReadUsnJournal_MaximumBufferReadsZero_ReadsToTip.
- Seam lambdas updated to 4 parameters in MFTLib.Tests/UsnJournalTests.cs (4 sites) and MFTLib.Tests/JournalBrokerHostRealSeamsTests.cs (1 site). These are outside the brief's file list but are forced by the seam signature; watch for a merge touch with C1 if it edits JournalBrokerHostRealSeamsTests.cs.

## TDD evidence
- RED: the tests do not compile before the method exists. Behavioral RED: with the bound replaced by `true` in the native loop (rebuilt, DLL copied), `dotnet test --filter MaximumBufferReads` -> Failed 1 (MaximumBufferReadsOne_StopsAfterOneRead), Passed 1 (Zero test, expected since unbounded == zero).
- GREEN: same filter -> Passed 2. `--filter "UsnJournal|JournalBrokerHostRealSeams"` -> Passed 109, Skipped 6, Failed 0.

## Verification
- run-coverage.ps1 -NonInteractive: total 1708, passed 1702, skipped 6, failed 0; line coverage 98%.
- aislop scan .: 99/100, exactly the four baseline warnings, nothing new.
- Native coverage run and Linux build: not run (the change is a loop bound in a Windows-only USN file; usn_journal.cpp is `_WIN32` code).

## Notes
- Building the vcxproj alone does not copy MFTLibNative.dll into MFTLib.Tests/bin (post-build xcopy uses $(SolutionDir), which resolves to MFTLibNative/ outside a solution build); I copied it by hand for targeted runs. run-coverage.ps1 handles it itself.
- Synthetic queue: when queued buffers run out the native code falls through to a real IOCTL on the fake handle and errors, which is what makes the bound-1 test discriminating.

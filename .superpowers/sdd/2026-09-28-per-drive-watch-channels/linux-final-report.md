# Linux final verification, commit f1a53db427d287a256a990e8b311d1c795d6ae33

Status: RED (1 managed test failed; coverage not produced)

## Commands (all over ssh llamabox)
1. cd ~/scratch/mftlib-265-w3 && git fetch origin impl/265-per-drive-channels && git checkout --detach f1a53db... && git clean -ffxd && git status --short
   -> HEAD is now at f1a53db "Wrong-reply test records Ended reasons in a queue instead of a captured counter"; rev-parse HEAD == f1a53db427d287a256a990e8b311d1c795d6ae33; status clean.
2. ./init.sh --build > ~/scratch/mftlib-265-w3-build.log (nohup)
   -> EXIT=0. Native (cmake/ninja, header mft_api.h changed) built: 9/9 steps, libMFTLibNative.so linked, native tests "=== 20 passed, 0 failed ===". Managed: Build succeeded, 0 errors, 1 warning (SourceLink info not available). Missing prereqs only aislop, gcovr.
3. scripts/coverage-linux.sh > ~/scratch/mftlib-265-w3-coverage.log (nohup)
   -> EXIT=1. Native part: "=== 20 passed, 0 failed ===".
   Managed: "Failed!  - Failed:     1, Passed:  1589, Skipped:    84, Total:  1674, Duration: 31 s - MFTLib.Tests.dll (net10.0)"
   Line coverage: NOT produced (coverlet writes output only on a green run).

## Failed test
ChannelFault_NoRecovery (MFTLib.Tests.Index.FileIndexWatchRecoveryTests, line 145)
  Assert.IsFalse failed. a channel fault queues no recovery
The assertion checks recoveryQueuedWhenRaised, captured in a WatchFaulted handler via index.TryGetRecoveryCompletionForTest at raise time; it was true, i.e. a recovery entry was already visible when the Channel fault event was raised.

## Extra runs (dotnet test --no-build, targeted)
- FileIndexWatchRecoveryTests + BlockFileRangedFlushTests together: 38 tests, only ChannelFault_NoRecovery failed (reproduced, so not a one-off in class-level run).
- ChannelFault_NoRecovery alone, 3 runs: Passed 3/3. So it is order/interaction dependent (fails when run with its class siblings or in the full suite, passes in isolation); likely a race or cross-test state in the recovery-queue visibility at raise time. Cause not confirmed; no code changed.

## Plan test classes on Linux (targeted runs, none excluded by the coverage filter)
- BlockFileRangedFlushTests: RAN on Linux, all passed (Flush_ViewLargerThanOneRange_ReportsEachRangeEndInOrder, Flush_ViewSmallerThanOneRange_ReportsOnceWithTheViewLength, Flush_NullCallback_Flushes, Flush_AfterDispose_Throws, Complete_PassesTheCallbackToTheFlush, Flush_CallbackThrows_PropagatesAndLeavesTheBlockUsable, FlushViewWithRetry_* x4, ClassifyPlatform_OnlyLinuxAndMacOsAreRecognized, SelectSynchronousFlag_ChoosesThePlatformValueAndRefusesOthers). The msync path executed and passed.
- JournalBrokerHostLivenessTests: 15 passed, 1 skipped
- BrokerProcessLivenessTests: 12 passed
- FileIndexCatchUpLossTests: 18 passed
- FileIndexConcurrentRescanTests: 8 passed
- FileIndexDisposalOrderTests: 3 passed
- FileIndexCallbackReentrancyTests: 21 passed
- FileIndexWatchRecoveryTests: 37 passed, 1 failed (ChannelFault_NoRecovery)
- FileIndexConcurrentOpenTests: 14 passed
- FileIndexBatchedOperationTests: 15 passed
- BrokerCrossDriveLivenessTests: 8 passed
- BrokerIndexWatchSourceTests: 10 passed

Logs on llamabox: ~/scratch/mftlib-265-w3-build.log, ~/scratch/mftlib-265-w3-coverage.log

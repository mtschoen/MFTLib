### Finding Verdicts

1. ADDRESSED (Important): The raw RED log supplies the complete literal targeted command and real failing output for `ReaderFrameLandingAfterStallClaimedTheRead_StallWinsExactlyOnce` (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/c7-red2.log:1-8`). The test still deterministically lets the stall claim the pending read before releasing the cancellation-ignoring frame (`MFTLib.Tests/BrokerProcessLivenessTests.cs:264-281`). This satisfies W40-R1 for the race regression.

2. ADDRESSED (Important): `ReadControlAsync` now explicitly awaits the frame reader's asynchronous disposal before calling `End(reason)`, so the stall timer drains before the control stream is closed and `Ended` is raised (`MFTLib/Broker/Client/BrokerProcess.Control.cs:224-250`). `ReaderDispose_WaitsForARunningStallCallback` advances the fake clock on another task, holds the executing callback, starts disposal, proves disposal remains incomplete, then releases the callback (`MFTLib.Tests/BrokerProcessLivenessTests.cs:284-303`). `ControlReaderEnd_DrainsStallTimerBeforeProcessEnds` closes the control pipe while that callback is held and proves both `Ended` and `HasEnded` remain unset until release (`MFTLib.Tests/BrokerProcessLivenessTests.cs:306-327`). The raw RED log gives exact commands and real failing output for both new tests (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/c7-red2.log:9-24`), satisfying W40-R1.

3. NOT ADDRESSED (Important): Most cited waits are now bounded: the race stream's release and inner read (`MFTLib.Tests/BrokerProcessLivenessTests.cs:423-430`), the callback gate through the bounded blocking helper (`MFTLib.Tests/TestSupport/TestGate.cs:25-33`), the timer fake's idle wait (`MFTLib.Tests/BrokerProcessLivenessTests.cs:493-505`), and `ScriptedBroker` cleanup (`MFTLib.Tests/TestSupport/ScriptedBroker.cs:63-72`). However, `CallbackDrainingTimer.DisposeAsync` still directly awaits `_inner.DisposeAsync()` without `WaitAsync(HangGuard)` (`MFTLib.Tests/BrokerProcessLivenessTests.cs:493-496`). If the inner timer disposal stalls, the helper can outlive or hang the failed test before it even signals `DisposeEntered`. The required all-await bound is therefore still incomplete.

### New Breakage in the Fix Diff

None separate from finding 3.

### Out-of-Scope Observations

The worktree reports HEAD `aacdaa9`, not the supplied `0fed951`. Both commits have the same tree (`e25497e1ebf5abf5494d67b699654004644777ef`), parent (`9c4178d`), and subject, so this does not change the reviewed diff or verdicts.

### Verdict

Findings remain open: 3.

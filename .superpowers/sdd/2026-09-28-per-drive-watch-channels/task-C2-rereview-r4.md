### Finding Verdicts

B. ADDRESSED. `BrokerFrameStream.ReadExactAsync` now invokes the optional callback immediately after the header read reports its first positive byte count. `BrokerFrameReader.ReadAsync` clears `FrameStarted` before each frame read, sets it through that callback, and leaves it set when the read later fails or is cancelled. `BrokerDriveChannel.IsOwnClose` now tolerates an `OperationCanceledException` or `ObjectDisposedException` only when `_closing` is cancelled and `FrameStarted` is false. A disposal that interrupts a read after any byte of a further frame was consumed therefore cannot retain the terminal result.

The four interleavings from task-C2-rereview-r3.md now resolve as follows:

1. Terminal frame, then own disposal while waiting for the first byte of the next frame: scan success. The new read reset `FrameStarted` to false, no positive byte count set it, and the own-close exception satisfies the filter.
2. Terminal frame, then peer truncation with no disposal: scan loss. A partial frame sets `FrameStarted`; EOF becomes `EndOfStreamException` wrapped in `BrokerChannelLostException`. An invalid length also has `FrameStarted` set and is wrapped from `InvalidDataException`. Neither is tolerated.
3. Terminal frame, then peer truncation and disposal at the same time: once any extra-frame byte has been consumed, scan loss on every completion ordering. EOF yields an untolerated `EndOfStreamException`; cancellation or pipe disposal yields an `OperationCanceledException` or `ObjectDisposedException`, but `FrameStarted` is true, so `IsOwnClose` rejects it.
4. No terminal frame yet, then own disposal: scan loss. The own-close filter still exists only around the post-terminal close check, so an earlier interrupted read propagates `BrokerChannelLostException`.

A complete terminal frame followed by clean EOF still returns the terminal result. If extra bytes are already buffered in the pipe but the pending read consumes zero bytes before own close wins, `FrameStarted` remains false and the scan succeeds. That is consistent with the required boundary: tolerance depends on bytes consumed by the interrupted read, not bytes available but unread. If the read returns any buffered byte first, the callback sets the flag and a later close fails the scan.

The flag is not read under a race that can misreport a positive read. The callback assignment occurs after the stream read returns a positive count and before `ReadExactAsync` can complete or propagate a later exception. The exception then crosses the awaited `BrokerFrameReader.ReadAsync` and `BrokerDriveChannel.ReadAsync` operations before the scan's catch filter reads `FrameStarted`. No second read starts on that reader between the failed read and the filter, so nothing can reset the flag in that interval.

The new regression test proves the required ordering. The terminal payload read is limited to that frame's remaining buffer, so it cannot consume the appended two-byte partial length prefix. `WhenReadPendingAfter(frames.WrittenCount)` can complete only after the next header read consumed those two bytes and its follow-up read for the other two prefix bytes became pending. Disposal is triggered only after that signal. The scan and disposal awaits are bounded explicitly; the helper reads and writes use their own `HangGuard` cancellation deadlines. Against the base code shown in the diff, `IsOwnClose` ignored partial consumption and the test would return the scan result instead of the expected exception, matching the report's recorded RED runs. The earlier success, truncation, and invalid-length cases remain compatible with the new predicate.

### New Breakage in the Fix Diff

None.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed, no new Critical/Important breakage

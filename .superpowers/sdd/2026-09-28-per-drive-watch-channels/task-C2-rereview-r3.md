### Finding Verdicts

A. ADDRESSED. `MFTLib.Tests/BrokerProcessTests.Disposal.cs:103-116` wraps the client drive pipe, writes the Cursor, and waits for `WhenReadPendingAfter(cursor.WrittenCount)` before disposal. `MFTLib.Tests/TestSupport/BrokerTestStreams.cs:143-170,195-207` signals only after the wrapped read has started, returned incomplete, and the preceding byte count has reached the complete Cursor length. The host remains open and writes nothing further, so that pending read can finish only through its cancellation token. `MFTLib/Broker/Client/BrokerDriveChannel.cs:61-70,83-92` links the read to `_closing` and cancels `_closing` during disposal. The report names the covering test and focused command at `task-C2-report.md:397-413`, and records three RED runs with `_closing.Cancel()` removed, each failing by timeout rather than producing the expected `BrokerChannelLostException`. The ordering is proven by a signal, every await is bounded, and the test cannot pass without the cancellation fix on another schedule.

B. NOT ADDRESSED. The filter at `MFTLib/Broker/Client/BrokerDriveChannel.cs:74-81` treats any post-terminal `OperationCanceledException` as an own close whenever `_closing` has been cancelled, and `MFTLib/Broker/Client/BrokerProcess.Scan.cs:127-135` then returns the terminal result. That does not establish that disposal interrupted a clean wait for the next frame's first byte. `MFTLib/Broker/Protocol/BrokerFrameStream.cs:59-75` can already have consumed part of an illegal extra frame and be awaiting its remaining bytes when disposal cancels the linked read. In that ordering, `BrokerDriveChannel.ReadAsync` wraps the cancellation as `BrokerChannelLostException` with an `OperationCanceledException` inner exception at `BrokerDriveChannel.cs:61-70`, `IsOwnClose` returns true, and the corrupted post-terminal channel is reported as scan success. The new tests at `MFTLib.Tests/BrokerProcessTests.Scan.cs:175-203` correctly cover truncation followed by peer close and an immediately invalid length, and the report supplies their RED evidence at `task-C2-report.md:434-446`, but neither test overlaps partial extra-frame input with process disposal.

The required interleavings resolve as follows:

1. Terminal frame, then own disposal while waiting for the first byte of the next frame: scan success. Disposal produces an `OperationCanceledException` or `ObjectDisposedException`, `_closing` is set, and the terminal result is retained.
2. Terminal frame, then peer truncation with no disposal: scan loss. EOF inside the extra frame becomes `EndOfStreamException`, then an uncaught `BrokerChannelLostException`. An invalid length likewise becomes an uncaught `BrokerChannelLostException` whose inner exception is `InvalidDataException`.
3. Terminal frame, then peer truncation and disposal at the same time: schedule-dependent. If EOF is observed first, the scan reports loss. If some extra-frame bytes have been consumed and disposal cancellation wins the pending read for the remainder, the scan reports success through `IsOwnClose`. This is success after a corrupted channel and leaves Finding B open.
4. No terminal frame yet, then own disposal: scan loss. The own-close filter surrounds only the post-terminal read, so the earlier read propagates `BrokerChannelLostException`.

A complete terminal frame followed by clean EOF returns the terminal result at `MFTLib/Broker/Client/BrokerProcess.Scan.cs:137`; the fix does not report loss for that valid close.

### New Breakage in the Fix Diff

None beyond the remaining Important defect in Finding B.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open: B.

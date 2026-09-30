# Broker lifetime

- Index contracts:
    - **VolumeBroker**: `BrokerProcess` owns one elevated process and its control pipe, with one
      UAC prompt per consumer session. Nonzero request ids route concurrent `OpenChannel`,
      `QueryVolume`, and `GrowUsnJournal` replies; an id remains reserved until its reply arrives
      or the process ends. Opening a drive operation creates a client-owned pipe, sends its name
      on the control pipe, waits for both the host connection and `ChannelOpened`, and writes one
      request. Each scan or watch then owns that drive channel, so closing or faulting it cancels
      only that operation. Control EOF ends the process and every channel;
      `BrokerProcess.Ended` and `HasEnded` report that lifecycle, and pending work fails with
      `BrokerChannelLostException`. The host admits concurrent scans under one processor-sized
      parse-thread budget and rebalances each `ParseThreadAllowance` when scans enter or leave;
      native parsing reads the allowance at each chunk and before path resolution. A scan writes
      `ScanReady` before bounded catch-up; a journal-proven loss ends it with `CatchUpLost`, while
      any unproven failure ends it with `Error`.
      One dedicated background thread visits every pipe every five seconds. An idle control pipe,
      a watch waiting on its volume, a queued scan, and a processing operation that has reported
      progress less than 30 seconds ago receive `Heartbeat`. This includes a `Processing` step
      that reports progress without writing an operation frame, such as bounded catch-up after
      `ScanReady` or block flush. Heartbeats do not reset the processing progress clock: at or
      beyond `ProcessingLimit` (30 seconds) without progress, the pipe writes `Stalled` and its
      channel is cancelled. A pipe that wrote an operation frame since the previous visit skips
      that visit. A pipe with a frame write in flight is also skipped, so it cannot delay other
      pipes; the client's 30-second no-frame limit then closes that pipe. Any frame resets the
      client limit. `BrokerMftBlockProducer` validates
      completed blocks and transfers them to the index, while `BrokerIndexWatchSource` opens one
      channel per drive watch. `ElevatedEntryPoint` and `BrokerLauncher` dispatch `--broker` mode.
      `BrokerDiagnostics` writes through a bounded background queue and filters the two diagnostic
      logs from journal batches unless `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in.

# Broker lifetime

- Index contracts:
    - **VolumeBroker**: `BrokerProcess` owns one elevated process and its control pipe, with one
      UAC prompt per consumer session. Nonzero request ids route concurrent `OpenChannel`,
      `QueryVolume`, and `GrowUsnJournal` replies; an id remains reserved until its reply arrives
      or the process ends. Opening a drive operation creates a client-owned pipe, sends its name
      on the control pipe, waits for both the host connection and `ChannelOpened`, and writes one
      request. Each scan or watch then owns that drive channel, so closing or faulting it cancels
      only that operation. Control EOF ends the process and every channel;
      the `BrokerProcess.Ended` task completes with the reason, and pending work fails with
      `BrokerChannelLostException`. The host admits concurrent scans under one processor-sized
      parse-thread budget and rebalances each `ParseThreadAllowance` when scans enter or leave;
      native parsing reads the allowance at each chunk and before path resolution. A scan writes
      `ScanReady` before bounded catch-up; a catch-up that held ends it with `ScanCompleted`
      carrying the advanced cursor, a journal-proven loss ends it with `CatchUpLost`, while
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
      client limit. `BrokerSession` owns the one `BrokerProcess` of a consumer session: its launch
      is lazy and shared, runs on the thread pool and never on the caller's synchronization
      context, only disposal cancels it, a launch that loses to disposal (checked before and
      after `Connecting`) never runs the launcher, a failed launch is not cached, a process that
      ended stays ended, `Ended` continuations run outside the session's gate, and disposal
      reclaims a process that arrives after it began.
      `BrokerMftBlockProducer` validates
      completed blocks and transfers them to the index, while `BrokerIndexWatchSource` opens one
      channel per drive watch. `ElevatedEntryPoint` and `BrokerLauncher` dispatch `--broker` mode.
      `BrokerDiagnostics` writes through a bounded background queue and filters the two diagnostic
      logs from journal batches unless `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in.

## Diagnostics configuration

After early child dispatch, clients call `BrokerDiagnostics.Enable(role, logDirectory)`
with an application-owned directory they create themselves. Environment-only opt-in
(`MFTLIB_BROKER_DIAG=1`) uses the OS temp directory. The launcher forwards the resolved
absolute log path as quoted `--diag-log` alongside `--diag`. Child dispatch derives its
directory from that path, enables the broker role, and supplies the client path to
journal self-filtering before invoking the runner. The path must already be normalized,
fully qualified, and named `broker-diagnostics.log`. Missing or valueless paths,
directory-only arguments (including an existing directory named `broker-diagnostics.log`),
different file names, `.` or `..` components, invalid characters,
and reserved device directory names in the ordinary Windows namespace are handled broker launches that exit with
code 1 without serving a session or falling through to application startup. Without `--diag`, dispatch ignores
the diagnostics arguments and does not configure diagnostics. Extended-length Windows paths
allow literal reserved device directory names while retaining the component checks.

### Task A2: Asynchronous diagnostics with channel tags

Implements amendment R10: a slow diagnostics write must not block any drive's frame path (today `LogFrame` runs inside the frame-write path, `JournalBrokerHost.cs:190-198`, and `Log` appends synchronously, `BrokerDiagnostics.cs:133-137`).

**Files:**
- Modify: `MFTLib/Broker/BrokerDiagnostics.cs`, callers `MFTLib/Broker/Host/JournalBrokerHost.cs` (`:192`, `:264`), `MFTLib/Broker/Client/JournalBrokerClient.Transport.cs` (its `LogFrame` and `Log` calls)
- Create: `MFTLib/Broker/BrokerDiagnosticsWriter.cs`
- Test: `MFTLib.Tests/BrokerDiagnosticsTests.cs`

**Interfaces produced:**

```csharp
internal const string ControlChannel = "control";
internal static string DriveChannel(char driveLetter, int sequence); // "C#3"
public static void Log(string channel, string message);             // line: "{utc:O}  [{role}:{pid}:{channel}]  {message}"
internal static void LogFrame(string channel, string direction, byte kind, int length);
internal static Task FlushForTestAsync(CancellationToken cancellationToken);
internal sealed class BrokerDiagnosticsWriter // one per process
{
    public const int Capacity = 8192;                       // records
    public BrokerDiagnosticsWriter(Action<string> appendLine); // production: File.AppendAllText to LogPath
    public bool TryEnqueue(string line);                    // never blocks
}
```

`Log` and `LogFrame` format the line (timestamp taken at the call) and `TryEnqueue` it into a bounded `Channel<string>` (`BoundedChannelFullMode.DropWrite`, single reader). One background task drains the channel and appends. Overflow policy: a record that finds the buffer full is dropped and counted; when the writer next appends, it first writes `[{role}:{pid}:diagnostics]  {count} records dropped: buffer full`. A failing append is counted the same way and never throws. The existing callers pass `BrokerDiagnostics.ControlChannel`; C1 and C2 pass real channel tags.

- [ ] **Step 1: Failing tests:** `Log_CarriesChannelTag`; `Log_ConcurrentWritersFromEightChannels_LoseNoLine` (eight tasks released on a `TestGate`, 200 lines each; after `FlushForTestAsync`, 1600 lines, each tag 200); `Log_BlockedSink_DoesNotBlockCaller` (the writer's `appendLine` waits on a `TestGate`; a second channel's 100 `Log` calls all return while the gate is closed); `Log_BufferFull_DropsAndReportsCount` (capacity reached behind a closed gate; after release the log holds a "records dropped" line with the right count); `Log_SinkThrowsThenRecovers_CountsFailureAndReportsOnNextAppend` (the writer's `appendLine` throws on the first two lines, then succeeds; no `Log` call throws; the next successful append is preceded by a "records dropped" line counting both failures).
- [ ] **Step 2: See them fail;** implement; verify (standard).
- [ ] **Step 3: Commit:** "Broker diagnostics tag every line and write through a bounded background queue".

**Gate:** green. **Depends on:** none.


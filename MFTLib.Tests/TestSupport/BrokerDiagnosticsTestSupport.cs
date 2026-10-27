namespace MFTLib.Tests.TestSupport;

static class BrokerDiagnosticsTestSupport
{
    // Producers must have stopped. Completion includes closing an owned file sink.
    public static Task DrainAsync() =>
        BrokerDiagnostics.CompleteAsync().WaitAsync(HangGuard, CancellationToken.None);
}

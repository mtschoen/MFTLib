namespace MFTLib.Tests.TestSupport;

static class BrokerDiagnosticsTestSupport
{
    // Teardown must drain before resetting shared state even after cancellation; HangGuard bounds the wait.
    public static Task DrainAsync() =>
        BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard, CancellationToken.None);
}

using System.Reflection;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_LaunchDeclined_ThrowsAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => false); // simulates a declined UAC prompt

        var exception =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

        StringAssert.Contains(exception.Message, "declined");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_NullLaunchBroker_ThrowsArgumentNullException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            JournalBrokerClient.SpawnAndConnectAsync(null!));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_NegativeTimeout_ThrowsArgumentOutOfRangeException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => true);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromSeconds(-5)));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_BrokerNeverConnects_TimesOutAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var launchBroker = new Func<string, bool>(_ => true); // launch started, but broker never connects

        var exception =
            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromMilliseconds(50)));

        StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
        StringAssert.Contains(exception.Message, "mftlib-broker-");
        StringAssert.Contains(exception.Message, "launched, but never connected");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_DefaultTimeoutOverridden_TimesOutAndDisposesServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        try
        {
            JournalBrokerClient._connectTimeout = TimeSpan.FromMilliseconds(50);
            var launchBroker = new Func<string, bool>(_ => true);

            var exception =
                await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                    JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

            StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
            StringAssert.Contains(exception.Message, "mftlib-broker-");
            StringAssert.Contains(exception.Message, "launched, but never connected");
        }
        finally
        {
            JournalBrokerClient.ResetToDefaults();
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_CallerCancellationRequested_ThrowsOperationCanceledException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled
        var launchBroker = new Func<string, bool>(_ => true);

        try
        {
            await JournalBrokerClient.SpawnAndConnectAsync(launchBroker, TimeSpan.FromSeconds(30), cts.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task DisposeAsync_PipeAlreadyClosed_SwallowsShutdownWriteFailure()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await clientSide.DisposeAsync(); // pipe already gone before DisposeAsync tries to write Shutdown

        await client.DisposeAsync(); // must not throw
    }

    [TestMethod]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await client.SendStartWatchAsync(
                new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cancellation.Token);

            // The double dispose below is the behavior under test, so it is deliberately not
            // expressed as an `await using`: disposing twice is the assertion, not incidental cleanup.
            await client.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
            await client.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_DemuxCtsInconsistentWithDemuxTask_ThrowsInvalidOperationException()
    {
        // _demuxCts and _demuxTask are always set together in SendStartWatchAsync and
        // cleared together at the end of a stop, so there is no public-API path to a
        // state where one is set without the other. Reflection simulates that violated
        // invariant (e.g. a future bug) to exercise StopLiveWatchAsync's own guard.
        var (clientSide, _) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        var demuxCtsField =
            typeof(JournalBrokerClient).GetField("_demuxCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousCts = (CancellationTokenSource)demuxCtsField.GetValue(client)!;
        demuxCtsField.SetValue(client, null);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(client.StopLiveWatchAsync);

        await previousCts.CancelAsync();
        previousCts.Dispose();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NotWatching_IsNoOp()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.StopLiveWatchAsync(); // no SendStartWatchAsync was ever called

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_PipeAlreadyClosed_SwallowsEndWatchWriteFailure()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        await clientSide.DisposeAsync(); // pipe already gone; WriteEndWatch will fail

        await client.StopLiveWatchAsync(); // must not throw or hang
        _ = serverSide;
    }

    [TestMethod]
    public void TryNormalizeDriveLetter_Null_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            JournalBrokerClient.TryNormalizeDriveLetter(null!, out _));
    }

    [TestMethod]
    public void TryNormalizeDriveLetter_InvalidDrive_ReturnsFalseAndAnEmptyNormalizedDrive()
    {
        var normalized = JournalBrokerClient.TryNormalizeDriveLetter("not a drive", out var normalizedDrive);

        Assert.IsFalse(normalized);
        Assert.AreEqual(string.Empty, normalizedDrive);
    }

    [DataTestMethod]
    [DataRow("C", "C")]
    [DataRow("c:\\", "C")]
    [DataRow("\\\\.\\d:", "D")]
    public void TryNormalizeDriveLetter_ValidDrive_ReturnsTrueAndUppercaseLetter(
        string drive, string expectedNormalizedDrive)
    {
        var normalized = JournalBrokerClient.TryNormalizeDriveLetter(drive, out var normalizedDrive);

        Assert.IsTrue(normalized);
        Assert.AreEqual(expectedNormalizedDrive, normalizedDrive);
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_NoAckWithinTimeout_ForcesDemuxDown()
    {
        JournalBrokerClient._endWatchAckTimeout = TimeSpan.FromMilliseconds(50);

        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });

        // The broker side never sends EndWatchAck (a wedged broker); StopLiveWatchAsync
        // must not hang - it forces the demux down once the (shrunk) timeout elapses.
        await client.StopLiveWatchAsync();
        Assert.IsTrue(client.LastStopTimedOut, "StopLiveWatchAsync must force demux down via timeout when broker sends no ack.");

        _ = serverSide;
    }

}

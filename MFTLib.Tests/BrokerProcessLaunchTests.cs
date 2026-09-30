using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The tests change process-wide state: diagnostics environment variables, BrokerDiagnostics
// settings and BrokerProcess's connect timeout.
[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public class BrokerProcessLaunchTests
{
    [TestCleanup]
    public void Cleanup()
    {
        BrokerProcess.ResetToDefaults();
    }

    [TestMethod]
    public async Task LaunchAsync_DiagEnvVarSet_AppendsDiagFlag()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        try
        {
            var arguments = await CaptureDeclinedLaunchArgumentsAsync();

            StringAssert.Contains(arguments, " --diag ");
            StringAssert.Contains(arguments, $" --diag-log \"{BrokerDiagnostics.LogPath}\"");
            Assert.IsFalse(arguments.Contains("--diag-include-self"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        }
    }

    [TestMethod]
    public async Task LaunchAsync_BrokerDiagnosticsEnabledProgrammatically_AppendsDiagFlag()
    {
        // BrokerDiagnostics.Enable("client") activates diagnostics without setting
        // MFTLIB_BROKER_DIAG; LaunchAsync must forward --diag and --diag-log for it as well.
        BrokerDiagnostics.Enable("client");
        try
        {
            var arguments = await CaptureDeclinedLaunchArgumentsAsync();

            StringAssert.Contains(arguments, " --diag ");
            StringAssert.Contains(arguments, $" --diag-log \"{BrokerDiagnostics.LogPath}\"");
            Assert.IsFalse(arguments.Contains("--diag-include-self"));
        }
        finally
        {
            BrokerDiagnostics.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task LaunchAsync_RelativeLogDirectory_ForwardsResolvedFullPath()
    {
        var originalDirectory = BrokerDiagnostics.LogDirectory;
        BrokerDiagnostics.LogDirectory = "relative-logs";
        BrokerDiagnostics.Enable("client");
        try
        {
            var arguments = await CaptureDeclinedLaunchArgumentsAsync();

            var expectedPath = BrokerDiagnostics.LogPath;
            Assert.IsTrue(Path.IsPathRooted(expectedPath));
            StringAssert.Contains(arguments, $" --diag-log \"{expectedPath}\"");
        }
        finally
        {
            BrokerDiagnostics.LogDirectory = originalDirectory;
            BrokerDiagnostics.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task LaunchAsync_IncludeSelfEnvVarSet_AppendsIncludeSelfFlag()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF", "1");
        try
        {
            StringAssert.EndsWith(await CaptureDeclinedLaunchArgumentsAsync(), "--diag-include-self");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF", null);
        }
    }

    [TestMethod]
    public async Task LaunchAsync_IncludeSelfSetProgrammatically_AppendsIncludeSelfFlag()
    {
        BrokerDiagnostics.Enable("client");
        BrokerDiagnostics.IncludeSelfEntries = true;
        try
        {
            StringAssert.EndsWith(await CaptureDeclinedLaunchArgumentsAsync(), "--diag-include-self");
        }
        finally
        {
            BrokerDiagnostics.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task LaunchAsync_DiagEnvVarUnset_OmitsAllDiagnosticsFlags()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF", null);

        Assert.IsFalse((await CaptureDeclinedLaunchArgumentsAsync()).Contains("--diag"));
    }

    [TestMethod]
    public async Task LaunchAsync_EndToEnd_UsesRealPipeAndRealBlockSeams()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        Task? brokerTask = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cancellation.Token;
        var launchBroker = new Func<string, bool>(arguments =>
        {
            var parts = arguments.Split(' ');
            var pipeName = parts[Array.IndexOf(parts, "--pipe") + 1];
            brokerTask = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);

                // A minimal fake host over the real control pipe, real drive pipes and the
                // client-created named block section.
                var host = new JournalBrokerHost(
                    _ => new UsnJournalCursor(7UL, 0L),
                    (_, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".", null)]],
                    (_, cursor, _) => (Array.Empty<UsnJournalEntry>(), cursor),
                    queryVolumeInfo: _ => new NtfsVolumeInformation(1024 * 1000, 1024, 512, 4096, 100, 10));
                await host.ServeAsync(pipe, DefaultElevatedEntryRunner.ConnectDrivePipeAsync,
                    new RealBlockSectionWriter(), CancellationToken.None);
            });
            return true;
        });

        var process = await BrokerProcess.LaunchAsync(launchBroker, cancellation.Token);
        try
        {
            var result = await process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
                cancellation.Token);
            // The real section factory built this block; a block returned from a scan is the caller's.
            using var block = result.Block.Block;

            Assert.AreEqual(new UsnJournalCursor(7UL, 0L), result.ArmedCursor);
            Assert.IsTrue(block.Header.RowCount > 0);
        }
        finally
        {
            await process.DisposeAsync();
        }

        await brokerTask!.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    public async Task LaunchAsync_NullLaunchBroker_ThrowsArgumentNull()
    {
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            BrokerProcess.LaunchAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task LaunchAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            BrokerProcess.LaunchAsync(_ => true, TimeSpan.FromSeconds(-5), CancellationToken.None));
    }

    [TestMethod]
    public async Task LaunchAsync_DefaultTimeoutOverridden_TimesOut()
    {
        BrokerProcess._connectTimeout = TimeSpan.FromMilliseconds(50);

        // LaunchAsync takes no clock: its connect timeout is a CancellationTokenSource on the system
        // timer, so the 50 ms wait is real. The hang guard bounds it, so a timer that never fires
        // fails the test instead of running into the runner's global timeout.
        var exception = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            BrokerProcess.LaunchAsync(_ => true, CancellationToken.None).WaitAsync(HostChannelHarness.HangGuard));

        StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
        StringAssert.Contains(exception.Message, "mftlib-broker-");
        StringAssert.Contains(exception.Message, "launched, but never connected");
    }

    [TestMethod]
    public async Task LaunchAsync_CallerCancellationRequested_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            await BrokerProcess.LaunchAsync(_ => true, TimeSpan.FromSeconds(30), cancellation.Token);
            Assert.Fail("Expected an OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task DisposeAsync_ControlPipeAlreadyClosed_DoesNotThrow()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        var process = CreateProcess(clientSide);
        await clientSide.DisposeAsync();

        await process.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        var process = CreateProcess(clientSide);

        // Disposing twice is the behavior under test, so it is not expressed as an `await using`.
        await process.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
        await process.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
    }

    // No scan runs on these processes, so their section factory is never called.
    static BrokerProcess CreateProcess(Stream control)
    {
        return new BrokerProcess(control, new NamedPipeBrokerPipeFactory(),
            (_, _) => throw new AssertFailedException("No scan runs in this test."), TimeProvider.System);
    }

    // Declines the launch at once; the tests only read the command line it was given.
    static async Task<string> CaptureDeclinedLaunchArgumentsAsync()
    {
        string? captured = null;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BrokerProcess.LaunchAsync(arguments =>
            {
                captured = arguments;
                return false;
            }, CancellationToken.None));

        Assert.IsNotNull(captured);
        return captured;
    }
}

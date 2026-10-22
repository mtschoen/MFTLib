using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The tests change process-wide diagnostics settings and broker connection timeout seams.
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

            Assert.Contains(" --diag ", arguments);
            Assert.Contains($" --diag-log \"{BrokerDiagnostics.LogPath}\"", arguments);
            Assert.DoesNotContain("--diag-include-self", arguments);
        }
        finally
        {
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        }
    }

    [TestMethod]
    public async Task LaunchAsync_BrokerDiagnosticsEnabledProgrammatically_AppendsDiagFlag()
    {
        // Programmatic enablement activates diagnostics without setting
        // MFTLIB_BROKER_DIAG; LaunchAsync must forward --diag and --diag-log for it as well.
        BrokerDiagnostics.Enable("client", Path.Combine(Path.GetTempPath(), "client logs " + Guid.NewGuid().ToString("N")));
        try
        {
            var arguments = await CaptureDeclinedLaunchArgumentsAsync();

            Assert.Contains(" --diag ", arguments);
            Assert.Contains($" --diag-log \"{BrokerDiagnostics.LogPath}\"", arguments);
            Assert.DoesNotContain("--diag-include-self", arguments);
        }
        finally
        {
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task LaunchAsync_RelativeLogDirectory_ForwardsResolvedFullPath()
    {
        BrokerDiagnostics.Enable("client", "relative-logs");
        try
        {
            var arguments = await CaptureDeclinedLaunchArgumentsAsync();

            var expectedPath = BrokerDiagnostics.LogPath;
            Assert.IsTrue(Path.IsPathRooted(expectedPath));
            Assert.Contains($" --diag-log \"{expectedPath}\"", arguments);
        }
        finally
        {
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
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
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF", null);
        }
    }

    [TestMethod]
    public async Task LaunchAsync_IncludeSelfSetProgrammatically_AppendsIncludeSelfFlag()
    {
        BrokerDiagnostics.Enable("client", Path.GetTempPath());
        BrokerDiagnostics.IncludeSelfEntries = true;
        try
        {
            StringAssert.EndsWith(await CaptureDeclinedLaunchArgumentsAsync(), "--diag-include-self");
        }
        finally
        {
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }

    [TestMethod]
    public async Task LaunchAsync_DiagEnvVarUnset_OmitsAllDiagnosticsFlags()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG_INCLUDE_SELF", null);

        Assert.DoesNotContain("--diag", await CaptureDeclinedLaunchArgumentsAsync());
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
                var host = new JournalBrokerHost(new ScriptedVolumeAccess
                {
                    CursorQuery = _ => new UsnJournalCursor(7UL, 0L),
                    RecordBatches = (_, _, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".")]],
                    JournalRead = (_, cursor, _) => (Array.Empty<UsnJournalEntry>(), cursor),
                    VolumeInformation = _ => new NtfsVolumeInformation(1024 * 1000, 1024)
                });
                await host.ServeAsync(pipe, DefaultElevatedEntryRunner.ConnectDrivePipeAsync,
                    new RealBlockSectionWriter(TimeProvider.System), CancellationToken.None);
            });
            return true;
        });

        var process = await BrokerProcess.LaunchAsync(launchBroker, cancellation.Token);
        try
        {
            var result = await process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
                null, cancellation.Token);
            // The real section factory built this block; a block returned from a scan is the caller's.
            using var block = result.Block.Block;

            Assert.AreEqual(new UsnJournalCursor(7UL, 0L), result.ArmedCursor);
            Assert.IsGreaterThan(0u, block.Header.RowCount);
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
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            BrokerProcess.LaunchAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task LaunchAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            BrokerProcess.LaunchAsync(_ => true, TimeSpan.FromSeconds(-5), CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LaunchAsync_NeverConnects_TimesOut(bool explicitTimeout)
    {
        var clock = new TimerSignalingClock();
        var duration = TimeSpan.FromMilliseconds(50);
        using var cancellation = new CancellationTokenSource();
        if (!explicitTimeout)
        {
            BrokerProcess._connectTimeout = duration;
        }

        BrokerProcess._connectTimeProvider = clock;
        try
        {
            var launch = explicitTimeout
                ? BrokerProcess.LaunchAsync(_ => true, duration, cancellation.Token)
                : BrokerProcess.LaunchAsync(_ => true, cancellation.Token);
            var registration = clock.TimerCreated(duration);
            Assert.IsTrue(registration.IsCompletedSuccessfully,
                "The connection deadline must be registered on the injected clock.");
            var timer = await registration;

            clock.Advance(duration - TimeSpan.FromTicks(1));
            Assert.IsFalse(timer.Fired);
            Assert.IsFalse(launch.IsCompleted);

            clock.Advance(TimeSpan.FromTicks(1));
            Assert.IsTrue(timer.Fired);
            var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => launch);

            Assert.Contains("Timed out waiting 50ms", exception.Message);
            Assert.Contains("mftlib-broker-", exception.Message);
            Assert.Contains("launched, but never connected", exception.Message);
        }
        finally
        {
            await cancellation.CancelAsync();
            BrokerProcess.ResetToDefaults();
        }
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
        var (clientSide, _) = new InMemoryPipePair();
        var process = CreateProcess(clientSide);
        await clientSide.DisposeAsync();

        await process.DisposeAsync().AsTask().WaitAsync(HangGuard);
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
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BrokerProcess.LaunchAsync(arguments =>
            {
                captured = arguments;
                return false;
            }, CancellationToken.None));

        Assert.IsNotNull(captured);
        return captured;
    }
}

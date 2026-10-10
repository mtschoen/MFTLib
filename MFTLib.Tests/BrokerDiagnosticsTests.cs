using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class BrokerDiagnosticsTests
{
    string _temporaryRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "BrokerDiagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.ResetToDefaults();
        // Best-effort cleanup only: a locked file or already-missing directory must
        // not fail the test.
        try
        {
            Directory.Delete(_temporaryRoot, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public void IsolationReset_RestoresTheDefaultDiagnosticsState()
    {
        BrokerDiagnostics.Enable("test", _temporaryRoot);
        Assert.IsTrue(BrokerDiagnostics.Enabled);

        BrokerDiagnosticsIsolation.Reset();

        Assert.IsFalse(BrokerDiagnostics.Enabled);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), BrokerDiagnostics.LogFileName), BrokerDiagnostics.LogPath);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Log_WhenEnabled_AppendsTimestampedLine(bool viaIsolation)
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        var message = viaIsolation ? "isolation-forwarded" : "cold-scan-broker-ok";
        if (viaIsolation)
        {
            BrokerDiagnosticsIsolation.Log(BrokerDiagnostics.ControlChannel, message);
            await BrokerDiagnosticsIsolation.FlushAsync(CancellationToken.None);
        }
        else
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, message);
            await BrokerDiagnostics.FlushAsync(CancellationToken.None);
        }

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(await File.ReadAllTextAsync(path), message);
    }

    [TestMethod]
    public async Task Log_EnvironmentOptIn_UsesDefaultDirectoryAndClientRole()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        var lines = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Add));

        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "environment-opt-in");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        Assert.AreEqual(Path.Combine(Path.GetTempPath(), BrokerDiagnostics.LogFileName), BrokerDiagnostics.LogPath);
        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], "[client:");
        StringAssert.Contains(lines[0], "environment-opt-in");
    }

    [TestMethod]
    public void Log_WhenDisabled_WritesNothing()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "should-not-appear");

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task Enable_ForcesLoggingRegardlessOfEnvVar_AndTagsRoleInLogLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Enable("broker", _temporaryRoot);
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "forced-on");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(await File.ReadAllTextAsync(path), "[broker:");
    }

    [TestMethod]
    public async Task Log_WhenAppendFails_SwallowsExceptionAndDoesNotThrow()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        // File.AppendAllText does not create missing directories, so pointing
        // diagnostics at one that was never created makes the write throw.
        var missingDirectory = Path.Combine(_temporaryRoot, "missing-subdir");
        BrokerDiagnostics.Enable("client", missingDirectory);

        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "should-not-throw");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);
        Assert.IsFalse(Directory.Exists(missingDirectory));
    }

    [TestMethod]
    public async Task LogFrame_WhenEnabled_AppendsFrameTraceLine()
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        BrokerDiagnostics.LogFrame(BrokerDiagnostics.DriveChannel('C', 3), "read", 6, 42);
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        var line = await File.ReadAllTextAsync(path);
        StringAssert.Contains(line, "frame read kind=6 len=42 t=");
        StringAssert.Contains(line, ":C#3]");
    }

    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Log_CarriesChannelTag()
    {
        BrokerDiagnostics.Enable("broker", _temporaryRoot);
        var lines = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Add, () => "broker"));

        BrokerDiagnostics.Log(BrokerDiagnostics.DriveChannel('D', 12), "hello");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.AreEqual(1, lines.Count);
        StringAssert.Matches(lines[0], new System.Text.RegularExpressions.Regex(
            @"^\d{4}-\d\d-\d\dT[\d:.]+Z  \[broker:\d+:D#12\]  hello$"));
    }

    [TestMethod]
    public async Task Log_ConcurrentWritersFromEightChannels_LoseNoLine()
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        var lines = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Add));
        var start = new TestGate();

        var writers = Enumerable.Range(0, 8).Select(channelIndex => Task.Run(async () =>
        {
            await start.WaitForReleaseAsync(CancellationToken.None);
            var channel = BrokerDiagnostics.DriveChannel((char)('C' + channelIndex), channelIndex);
            for (var lineIndex = 0; lineIndex < 200; lineIndex++)
            {
                BrokerDiagnostics.Log(channel, "line " + lineIndex);
            }
        })).ToArray();
        start.Release();
        await Task.WhenAll(writers).WaitAsync(TestTimeout);
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.AreEqual(1600, lines.Count);
        for (var channelIndex = 0; channelIndex < 8; channelIndex++)
        {
            var tag = $":{(char)('C' + channelIndex)}#{channelIndex}]";
            Assert.AreEqual(200, lines.Count(line => line.Contains(tag, StringComparison.Ordinal)), tag);
        }
    }

    [TestMethod]
    public async Task Log_BlockedSink_DoesNotBlockCaller()
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        var gate = new TestGate();
        var lines = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(line =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            lines.Add(line);
        }));

        BrokerDiagnostics.Log(BrokerDiagnostics.DriveChannel('C', 1), "parks the sink");
        await gate.Entered.WaitAsync(TestTimeout);
        var otherChannel = BrokerDiagnostics.DriveChannel('D', 1);
        var callerTask = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                BrokerDiagnostics.Log(otherChannel, "queued " + index);
            }
        });
        await callerTask.WaitAsync(TestTimeout);

        gate.Release();
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);
        Assert.AreEqual(101, lines.Count);
    }

    [TestMethod]
    public async Task Log_BufferFull_DropsAndReportsCount()
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        var gate = new TestGate();
        var lines = new List<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(line =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
            lines.Add(line);
        }));

        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "parks the sink");
        await gate.Entered.WaitAsync(TestTimeout);
        for (var index = 0; index < BrokerDiagnosticsWriter.Capacity + 5; index++)
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "fill " + index);
        }

        gate.Release();
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.AreEqual(1, lines.Count(line => line.Contains("5 records dropped: buffer full", StringComparison.Ordinal)));
        Assert.AreEqual(1 + BrokerDiagnosticsWriter.Capacity + 1, lines.Count);
    }

    [TestMethod]
    public async Task Log_SinkThrowsThenRecovers_CountsFailureAndReportsOnNextAppend()
    {
        BrokerDiagnostics.Enable("client", _temporaryRoot);
        var lines = new List<string>();
        var attempts = 0;
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(line =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw new IOException("sink offline");
            }

            lines.Add(line);
        }));

        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "first");
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "second");
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "third");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.AreEqual(2, lines.Count);
        StringAssert.Contains(lines[0], "2 records dropped: buffer full");
        StringAssert.Contains(lines[1], "third");
    }
}

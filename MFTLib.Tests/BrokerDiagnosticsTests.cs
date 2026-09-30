using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class BrokerDiagnosticsTests
{
    string _originalLogDirectory = null!;
    string _temporaryRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _originalLogDirectory = BrokerDiagnostics.LogDirectory;
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "BrokerDiagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
        BrokerDiagnostics.LogDirectory = _temporaryRoot;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.LogDirectory = _originalLogDirectory;
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
    public async Task Log_WhenEnabled_AppendsTimestampedLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "cold-scan-broker-ok");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.log");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(await File.ReadAllTextAsync(path), "cold-scan-broker-ok");
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
        BrokerDiagnostics.Enable("broker");
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
        // LogDirectory at one that was never created makes the write throw.
        BrokerDiagnostics.LogDirectory = Path.Combine(_temporaryRoot, "missing-subdir");

        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "should-not-throw");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LogFrame_WhenEnabled_AppendsFrameTraceLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
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
        BrokerDiagnostics.Enable("broker");
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
        BrokerDiagnostics.Enable("client");
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
        BrokerDiagnostics.Enable("client");
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
        BrokerDiagnostics.Enable("client");
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
        BrokerDiagnostics.Enable("client");
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

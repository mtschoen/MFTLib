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
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "broker-diagnostics.client.log"), BrokerDiagnostics.LogPath);
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

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.client.log");
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

        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "broker-diagnostics.client.log"), BrokerDiagnostics.LogPath);
        Assert.AreEqual(1, lines.Count);
        StringAssert.Contains(lines[0], "[client:");
        StringAssert.Contains(lines[0], "environment-opt-in");
    }

    [TestMethod]
    public void Log_WhenDisabled_WritesNothing()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "should-not-appear");

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.client.log");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task Enable_ForcesLoggingRegardlessOfEnvVar_AndTagsRoleInLogLine()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.Enable("broker", _temporaryRoot);
        BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel, "forced-on");
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.broker.log");
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
        BrokerDiagnostics.LogFrame("test-C-3", "read", 6, 42);
        await BrokerDiagnostics.FlushAsync(CancellationToken.None);

        var path = Path.Combine(_temporaryRoot, "broker-diagnostics.client.log");
        Assert.IsTrue(File.Exists(path));
        var line = await File.ReadAllTextAsync(path);
        StringAssert.Contains(line, "frame read kind=6 len=42 t=");
        StringAssert.Contains(line, ":test-C-3]");
    }

    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [DataTestMethod]
    [DataRow("ui")]
    [DataRow("Client-2_test")]
    [DataRow("-")]
    [DataRow("_")]
    public void Enable_ValidRole_UsesRoleSpecificFile(string role)
    {
        BrokerDiagnostics.Enable(role, _temporaryRoot);
        Assert.AreEqual(Path.Combine(_temporaryRoot, $"broker-diagnostics.{role}.log"), BrokerDiagnostics.LogPath);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("../ui")]
    [DataRow("ui\\broker")]
    [DataRow("ui.log")]
    [DataRow("ui role")]
    [DataRow("ui:")]
    [DataRow("ui\0")]
    [DataRow("\u00e9")]
    public void Enable_InvalidRole_RejectsWithoutChangingDiagnostics(string? role)
    {
        var originalPath = BrokerDiagnostics.LogPath;
        Assert.ThrowsException<ArgumentException>(() => BrokerDiagnostics.Enable(role!, _temporaryRoot));
        Assert.IsFalse(BrokerDiagnostics.Enabled);
        Assert.AreEqual(originalPath, BrokerDiagnostics.LogPath);
    }

    [TestMethod]
    public async Task Log_DifferentRoles_HeldFileDoesNotDropOtherRoleRecords()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file sharing is the subject of this regression.");
        }

        BrokerDiagnostics.Enable("ui", _temporaryRoot);
        var clientPath = BrokerDiagnostics.LogPath;
        BrokerDiagnostics.Enable("broker", _temporaryRoot);
        var brokerPath = BrokerDiagnostics.LogPath;
        Exception? appendFailure = null;
        var clientWriter = new BrokerDiagnosticsWriter(line =>
        {
            try
            {
                File.AppendAllText(clientPath, line + Environment.NewLine);
            }
            catch (Exception exception)
            {
                appendFailure = exception;
                throw;
            }
        }, () => "ui");
        var brokerWriter = new BrokerDiagnosticsWriter(
            line => File.AppendAllText(brokerPath, line + Environment.NewLine), () => "broker");
        try
        {
            using (new FileStream(clientPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                Assert.IsTrue(clientWriter.TryEnqueue("held-client-record"));
                for (var index = 0; index < 20; index++)
                {
                    Assert.IsTrue(brokerWriter.TryEnqueue($"broker-record-{index}"));
                }

                await Task.WhenAll(clientWriter.FlushAsync(CancellationToken.None),
                    brokerWriter.FlushAsync(CancellationToken.None)).WaitAsync(TestTimeout);
            }

            Assert.IsNotNull(appendFailure);
            var failure = appendFailure;
            Assert.IsTrue(clientWriter.TryEnqueue("client-recovered"));
            await clientWriter.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);
            var clientLines = await File.ReadAllLinesAsync(clientPath);
            StringAssert.Contains(clientLines[0], $"1 records dropped: append failed: {failure.GetType().Name}: {failure.Message}");
            Assert.IsFalse(clientLines[0].Contains("buffer full", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { "client-recovered" }, clientLines.Skip(1).ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(index => $"broker-record-{index}").ToArray(),
                await File.ReadAllLinesAsync(brokerPath));
        }
        finally
        {
            clientWriter.Complete();
            brokerWriter.Complete();
            await Task.WhenAll(clientWriter.FlushAsync(CancellationToken.None),
                brokerWriter.FlushAsync(CancellationToken.None)).WaitAsync(TestTimeout);
        }
    }

    [TestMethod]
    public async Task Log_CarriesChannelTag()
    {
        BrokerDiagnostics.Enable("broker", _temporaryRoot);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(lines.Enqueue, () => "broker"));
        var host = new JournalBrokerHost(new ScriptedVolumeAccess { CursorQuery = _ => default });
        await using var broker = new InProcessBroker(host);
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var drive in new[] { 'C', 'D', 'C' })
        {
            await using var channel = await broker.Process.OpenChannelAsync(drive,
                writer => BrokerProtocol.WriteStartWatch(writer, default), CancellationToken.None).WaitAsync(TestTimeout);
            Assert.AreEqual(BrokerFrameKind.Error,
                (await channel.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TestTimeout))?.Kind);
            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(TestTimeout);
            var traces = lines.Where(line => line.Contains($" kind={(byte)BrokerFrameKind.StartWatch} ", StringComparison.Ordinal))
                .Select(line => System.Text.RegularExpressions.Regex.Match(line,
                    @"^\d{4}-\d\d-\d\dT[\d:.]+Z  \[broker:\d+:(?<tag>[^\]]+)\]  frame (?<direction>read|write) kind=\d+ len=17 t=\d+$"))
                .ToArray();
            CollectionAssert.AreEquivalent(new[] { "read", "write" },
                traces.Select(trace => trace.Groups["direction"].Value).ToArray());
            var tag = traces[0].Groups["tag"].Value;
            Assert.AreEqual(tag, traces[1].Groups["tag"].Value, $"Client and host tags differ for drive {drive}.");
            Assert.IsTrue(seenTags.Add(tag), "Each channel must have its own identity, including repeated drives.");
            lines.Clear();
        }
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
            var channel = $"test-{(char)('C' + channelIndex)}-{channelIndex}";
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
            var tag = $":test-{(char)('C' + channelIndex)}-{channelIndex}]";
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

        BrokerDiagnostics.Log("test-C-1", "parks the sink");
        await gate.Entered.WaitAsync(TestTimeout);
        var otherChannel = "test-D-1";
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
    public async Task Log_QueueFull_DropsAndReportsCount()
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

        Assert.AreEqual(1, lines.Count(line => line.Contains("5 records dropped: queue full", StringComparison.Ordinal)));
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
        StringAssert.Contains(lines[0], "2 records dropped: append failed: IOException: sink offline");
        StringAssert.Contains(lines[1], "third");
    }
}

// Holds the existing benchmark CLI, worker-memory, report, and comparison regression fixture in one partial module.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Benchmark;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public partial class BenchmarkRunnerTests
{
    [TestMethod]
    [DataRow("--synthetic", "0")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "4294967295")]
    [DataRow("--unknown", "10")]
    [DataRow("--synthetic", "10", "--iterations", "0")]
    [DataRow("--synthetic", "10", "--iterations", "invalid")]
    [DataRow("--synthetic", "10", "--iterations")]
    [DataRow("--synthetic", "10", "--out")]
    [DataRow("--synthetic", "10", "--cache-directory", "other")]
    [DataRow("--cache-directory", "")]
    [DataRow("--synthetic")]
    [DataRow]
    public async Task Run_Index_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe index", _output.ToString());
    }

    [TestMethod]
    public void PrintIndexMedian_UsesElapsedQueryTime()
    {
        const int iterations = 3;
        const string expected = "5.000";
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var output = new StringWriter();
        var runner = new BenchmarkRunner(output, clock);
        var durations = new Queue<double>([9, 1, 5]);
        runner.PrintIndexMedian("exact-search", () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(durations.Dequeue()));
            return Array.Empty<FileEntry>();
        }, iterations, output.WriteLine);
        Assert.Contains($"Median: {expected} ms; Matches: 0", output.ToString());
        Assert.IsEmpty(durations);
    }

    [TestMethod]
    [DataRow(1, 3)]
    [DataRow(10, 4)]
    public async Task Run_Index_Synthetic_ReportsGeometryAndControlledMedians(int rows, int iterations)
    {
        Assert.AreEqual(0, await _runner.RunAsync(["INDEX", "--synthetic", rows.ToString(CultureInfo.InvariantCulture), "--iterations", iterations.ToString(CultureInfo.InvariantCulture)]));
        var output = _output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains($"Rows: {rows}", output);
        Assert.Contains($"Slot capacity: {BlockLayout.ComputeSlotCapacity((uint)rows)}", output);
        Assert.Contains("File bytes:", output);
        Assert.Contains("Name-pool bytes:", output);
        Assert.Contains("Name-pool capacity bytes:", output);
        Assert.Contains("--- exact-search ---", output);
        Assert.Contains("--- substring-search ---", output);
        Assert.Contains($"Matches: {(rows == 1 ? 0 : 1)}", output);
        Assert.Contains("  Peak private bytes: 100\n  Peak working set: 300\n  Memory measurement: windows-os-peak", output);
        Assert.Contains("  Peak private bytes: 200\n  Peak working set: 300\n  Memory measurement: windows-os-peak", output);
        Assert.IsLessThan(
            output.IndexOf("--- substring-search ---", StringComparison.Ordinal),
            output.IndexOf("Peak private bytes: 100", StringComparison.Ordinal));
        Assert.IsGreaterThan(
            output.IndexOf("--- substring-search ---", StringComparison.Ordinal),
            output.IndexOf("Peak private bytes: 200", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("--synthetic", "15")]
    [DataRow("--synthetic", "invalid")]
    [DataRow("--synthetic", "10000001")]
    [DataRow("--dump", "")]
    [DataRow("--synthetic", "64", "--iterations", "0")]
    [DataRow("--unknown", "64")]
    [DataRow]
    public async Task Run_NativeTimings_InvalidArguments_ReportUsage(params string[] arguments)
    {
        Assert.AreEqual(1, await _runner.RunAsync(["native-timings", .. arguments]));
        Assert.Contains("Usage: Benchmark.exe native-timings", _output.ToString());
    }

    [TestMethod]
    public async Task Run_NativeTimings_Synthetic_PrintsTheIoFixupParseTotalBreakdown()
    {
        var runner = new BenchmarkRunner(_output);
        Assert.AreEqual(0, await runner.RunAsync(["native-timings", "--synthetic", "4096", "--iterations", "2"]));
        var output = _output.ToString();
        Assert.Contains("Commit:", output);
        Assert.Contains("--- native-parse-timings ---", output);
        Assert.MatchesRegex(@"(?m)^  Median: [0-9.]+ ms\r?$", output);
        Assert.MatchesRegex(@"(?m)^  Peak private bytes: [1-9][0-9]*\r?$", output);
        Assert.MatchesRegex(@"(?m)^  Peak working set: [1-9][0-9]*\r?$", output);
        Assert.Contains("Memory measurement:", output);
        Assert.Contains("Records: 4096 examined", output);
        Assert.Contains("(median of 2 runs)", output);
        var milliseconds = new Dictionary<string, double>();
        foreach (var label in new[] { "Native I/O", "Native fixup", "Native parse", "Native total" })
        {
            var match = Regex.Match(output, Regex.Escape(label) + @":\s+([0-9.]+) ms");
            Assert.IsTrue(match.Success, $"{label} line is present.");
            milliseconds[label] = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        foreach (var label in new[] { "Native I/O", "Native fixup", "Native parse" })
        {
            Assert.IsGreaterThanOrEqualTo(milliseconds[label], milliseconds["Native total"], $"The total bounds {label}.");
        }
        Assert.MatchesRegex(MyRegex(), output);
        Assert.Contains("buffered", output);
        Assert.DoesNotContain("disk is the bottleneck", output, "The verdict names a stage, not the disk.");

        var reportPath = Path.Combine(_directory, "native-report.txt");
        await File.WriteAllTextAsync(reportPath, output, TestContext.CancellationToken);
        Assert.AreEqual(0, await runner.RunAsync(["compare", reportPath, reportPath, "native-parse-timings"]));
    }

    [TestMethod]
    public async Task Run_NativeTimings_MissingDump_ReportsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mft");
        Assert.AreEqual(1, await _runner.RunAsync(["native-timings", "--dump", path]));
        Assert.Contains("Error:", _output.ToString());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScenarioMemoryProcess.MeasureAsync(
                new ScenarioMemoryRequest("native-parse-timings", path, 1), TestContext.CancellationToken));
        Assert.Contains("native-parse-timings", exception.Message);
    }

    [TestMethod]
    public async Task Run_Index_DefaultIterations_ReportsThreeSamples()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        Assert.Contains("median of 3 runs", _output.ToString());
    }

    [TestMethod]
    public async Task Run_Index_CacheDirectory_UsesEachStoredTagAndLeavesBlocksIntact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            void WriteBlock(char drive, ulong scanShape, ProducerKind producer = ProducerKind.Enumeration) =>
                WriteCacheBlock(directory, directory, drive, scanShape, producer);

            var expectedShape = SyntheticScanShape.Default(ProducerKind.Enumeration);
            WriteBlock('T', expectedShape);
            WriteBlock('U', expectedShape);

            // One block per row-shaping option the benchmark does not supply (issue #678): each must
            // be declined as an unsupported scan shape and its bytes left intact.
            var declined = new (char Drive, ulong Shape, ProducerKind Producer)[]
            {
                ('V', ScanShape.ForEnumeration(["child.txt"], new DirectoryWalkOptions(), directory), ProducerKind.Enumeration),
                ('W', ScanShape.ForEnumeration([], new DirectoryWalkOptions(), directory), ProducerKind.Enumeration),
                ('X', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryNames = ["build"] }, directory), ProducerKind.Enumeration),
                ('Y', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(directory, "build")] }, directory), ProducerKind.Enumeration),
                ('Z', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryAttributes = FileAttributes.Hidden }, directory), ProducerKind.Enumeration),
                ('S', ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PruneDotDirectories = true }, directory), ProducerKind.Enumeration),
                ('M', ScanShape.ForMft(["child.txt"], includeFreed: false), ProducerKind.Mft),
                ('N', ScanShape.ForMft(null, includeFreed: true), ProducerKind.Mft)
            };
            foreach (var (drive, shape, producer) in declined)
            {
                WriteBlock(drive, shape, producer);
            }

            var requests = new List<ScenarioMemoryRequest>();
            var runner = new BenchmarkRunner(_output, TimeProvider.System, request =>
            {
                requests.Add(request);
                Assert.IsNotNull(request.Index);
                var blockPath = Path.Combine(request.InputPath, $"{request.Index.Drive}-00000001.mlix");
                using var lockHandle = BlockOwnerLock.TryAcquire(blockPath);
                Assert.IsNotNull(lockHandle, "Parent must release block ownership before starting the memory worker.");
                return FixedMemoryAsync(request);
            });

            var originals = Directory.GetFiles(directory, "*.mlix").ToDictionary(path => path, File.ReadAllBytes);
            Assert.HasCount(2 + declined.Length, originals);
            Assert.AreEqual(1, await runner.RunAsync(["index", "--cache-directory", directory]));
            var output = _output.ToString();
            Assert.Contains("Drive: T", output);
            Assert.Contains("Drive: U", output);
            Assert.Contains("Rows: 2", output);
            Assert.Contains("Matches: 1", output);
            var usedNameBytes = (directory.Length + "file-0000000001".Length) * 2;
            var fileBytes = new FileInfo(Path.Combine(directory, "T-00000001.mlix")).Length;
            Assert.Contains(string.Create(CultureInfo.InvariantCulture,
                $"Name-pool bytes: {usedNameBytes} ({100.0 * usedNameBytes / fileBytes:F2}% of file)"), output);
            foreach (var (drive, _, _) in declined)
            {
                Assert.Contains($"{drive}-00000001.mlix: unsupported scan shape", output);
            }
            foreach (var original in originals)
            {
                Assert.AreSequenceEqual(original.Value, await File.ReadAllBytesAsync(original.Key, TestContext.CancellationToken));
            }
            CollectionAssert.AreEqual(
                new (char, string, uint, uint, ulong)[]
                {
                    ('T', "exact-search", new CacheTag("TEST", 'T').PackedFourCc, 'T', expectedShape),
                    ('T', "substring-search", new CacheTag("TEST", 'T').PackedFourCc, 'T', expectedShape),
                    ('U', "exact-search", new CacheTag("TEST", 'U').PackedFourCc, 'U', expectedShape),
                    ('U', "substring-search", new CacheTag("TEST", 'U').PackedFourCc, 'U', expectedShape)
                },
                requests.Select(request => (
                    request.Index!.Drive,
                    request.Scenario,
                    request.Index.PackedFourCc,
                    request.Index.TagVersion,
                    request.Index.ScanShape)).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_Index_EmptyOrCorruptCache_FailsWithoutScanning(bool corrupt)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            if (corrupt)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "T-00000001.mlix"), "corrupt", TestContext.CancellationToken);
            }
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            Assert.Contains(corrupt ? "Invalid" : "No cache blocks", _output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_Index_OfflineOrLockedCache_ReportsUnavailable(bool locked)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "T-00000001.mlix");
        try
        {
            using (var block = TestBlocks.Create(path, slotCapacity: 8, namePoolCapacity: 4096,
                volumeSerial: 1, scanShape: SyntheticScanShape.Default(ProducerKind.Enumeration)))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(0, Path.Combine(directory, "missing-root"),
                    new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                writer.Complete(DateTime.UtcNow, null);
            }
            using var owner = locked ? BlockOwnerLock.TryAcquire(path) : null;
            Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", directory]));
            Assert.Contains(locked ? "InUse" : "Offline", _output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Run_Index_MftCache_OpensWithJournalIsolationEnabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"benchmark-index-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var drive = Path.GetPathRoot(directory)![0];
        try
        {
            using (var block = TestBlocks.Create(Path.Combine(directory, $"{drive}-00000001.mlix"),
                ProducerKind.Mft, 8, 4096, volumeSerial: 1, rootRow: 5,
                scanShape: SyntheticScanShape.Default(ProducerKind.Mft)))
            {
                var writer = new BlockWriter(block);
                writer.TryWriteRow(5, "", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0));
                block.Header.UsnJournalIdentifier = 123;
                block.Header.UsnNextUsn = 456;
                writer.Complete(DateTime.UtcNow, null);
            }
            Assert.AreEqual(0, await _runner.RunAsync(["index", "--cache-directory", directory]),
                _output.ToString());
            Assert.Contains("Rows: 6", _output.ToString());
            Assert.Contains("Name-pool bytes: 0 (0.00% of file)", _output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Run_NoVerb_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync([]));
        Assert.Contains("Usage: Benchmark.exe <index|compare|native-timings|enumeration-walk>", _output.ToString());
    }

    [TestMethod]
    public async Task Run_Index_PrintsSystemInfo()
    {
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        var output = _output.ToString();
        Assert.Contains("System Info", output);
        Assert.Contains("CPU:", output);
        Assert.Contains("Disk:", output);
        var informational = typeof(BenchmarkRunner).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        Assert.IsTrue(separator >= 0, "The benchmark build must carry SourceRevisionId.");
        Assert.Contains($"Commit: {informational[(separator + 1)..]}", output);
        Assert.ThrowsExactly<InvalidOperationException>(() => BenchmarkRunner.ParseBuildCommit("1.0.0"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BenchmarkRunner.ParseBuildCommit("1.0.0+"));
    }

    [TestMethod]
    [DataRow(true, 900L, "windows-os-peak")]
    [DataRow(false, 800L, "linux-sampled-vmdata-10ms")]
    public async Task MemoryCapture_RetainsThePeakAndSamplesBeforeFinish(
        bool operatingSystemPeak, long expectedPrivate, string expectedMethod)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var readings = new Queue<MemoryReading>(
        [
            new MemoryReading(100, 300, 400),
            new MemoryReading(800, 900, 1000),
            new MemoryReading(200, 900, 1000)
        ]);
        await using var capture = new ScenarioMemoryCapture(
            () => readings.Count > 1 ? readings.Dequeue() : readings.Peek(),
            operatingSystemPeak,
            clock);
        capture.Sample();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        var result = await capture.FinishAsync();
        Assert.AreEqual(expectedPrivate, result.PeakPrivateBytes);
        Assert.AreEqual(1000L, result.PeakWorkingSetBytes);
        Assert.AreEqual(expectedMethod, result.Method);
        Assert.AreEqual(Environment.ProcessId, result.ProcessId);
    }

    [TestMethod]
    public async Task MemoryCapture_SamplerFailureCannotProduceAResult()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var readings = new Queue<MemoryReading>([new MemoryReading(100, 0, 200)]);
        await using (var capture = new ScenarioMemoryCapture(
            () => readings.TryDequeue(out var reading) ? reading : throw new IOException("counter unavailable"),
            false, clock))
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            var exception = await Assert.ThrowsAsync<IOException>(() => capture.FinishAsync());
            Assert.Contains("counter unavailable", exception.Message);
            Assert.ThrowsExactly<IOException>(() => capture.Sample());
        }

        await using var zeroCapture = new ScenarioMemoryCapture(
            () => new MemoryReading(0, 0, 0), true, clock);
        var zeroException = await Assert.ThrowsAsync<InvalidOperationException>(() => zeroCapture.FinishAsync());
        Assert.Contains("Process memory counters are unavailable.", zeroException.Message);
        Assert.ThrowsExactly<PlatformNotSupportedException>(
            () => ScenarioMemoryCapture.EnsureSupportedPlatform(isWindows: false, isLinux: false));
    }

    [TestMethod]
    public async Task MemoryProcess_UsesANewProcessForEveryScenario()
    {
        var startInfo = ScenarioMemoryProcess.CreateStartInfo(typeof(BenchmarkRunner).Assembly.Location);
        CollectionAssert.Contains(startInfo.ArgumentList, "--measure-memory");

        var cache = Directory.CreateDirectory(Path.Combine(_directory, "cache with spaces")).FullName;
        var expectedShape = SyntheticScanShape.Default(ProducerKind.Enumeration);
        WriteCacheBlock(cache, cache, 'T', expectedShape);
        var input = new IndexMemoryInput('T', cache, 1, ProducerKind.Enumeration,
            new CacheTag("TEST", 'T').PackedFourCc, 'T', expectedShape);
        var first = await ScenarioMemoryProcess.MeasureAsync(
            new ScenarioMemoryRequest("exact-search", cache, 1, input), TestContext.CancellationToken);
        var second = await ScenarioMemoryProcess.MeasureAsync(
            new ScenarioMemoryRequest("substring-search", cache, 1, input), TestContext.CancellationToken);
        Assert.AreNotEqual(Environment.ProcessId, first.ProcessId);
        Assert.AreNotEqual(Environment.ProcessId, second.ProcessId);
        Assert.IsTrue(first.PeakPrivateBytes > 0 && second.PeakPrivateBytes > 0);
        Assert.IsTrue(first.PeakWorkingSetBytes > 0 && second.PeakWorkingSetBytes > 0);
        Assert.AreEqual(first.Method, second.Method);
        Assert.IsTrue(File.Exists(Path.Combine(cache, "T-00000001.mlix")));

        var walkRoot = Directory.CreateDirectory(Path.Combine(_directory, "walk-root")).FullName;
        foreach (var walkScenario in new[] { "enumeration-walk-sequential", "enumeration-walk-parallel" })
        {
            var walkResult = await ScenarioMemoryProcess.MeasureAsync(
                new ScenarioMemoryRequest(walkScenario, walkRoot, 1, Rows: 1), TestContext.CancellationToken);
            Assert.AreNotEqual(Environment.ProcessId, walkResult.ProcessId);
            Assert.IsGreaterThan(0, walkResult.PeakPrivateBytes);
            Assert.IsGreaterThan(0, walkResult.PeakWorkingSetBytes);
        }
        Assert.IsTrue(Directory.Exists(walkRoot));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ScenarioMemoryProcess.MeasureAsync(
                new ScenarioMemoryRequest("exact-search", cache, 1, input), cancelled.Token));

        var missingCache = Path.Combine(_directory, "missing-cache");
        var missingFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScenarioMemoryProcess.MeasureAsync(
                new ScenarioMemoryRequest("exact-search", missingCache, 1, input), TestContext.CancellationToken));
        Assert.Contains("exact-search", missingFailure.Message);

        foreach (var payload in new[]
        {
            "",
            "null",
            "{invalid",
            "{\"Scenario\":\"exact-search\",\"InputPath\":\"\",\"Iterations\":1}",
            "{\"Scenario\":\"unknown\",\"InputPath\":\"x\",\"Iterations\":1}",
            "{\"Scenario\":\"exact-search\",\"InputPath\":\"x\",\"Iterations\":1}",
            "{\"Scenario\":\"native-parse-timings\",\"InputPath\":\"x\",\"Iterations\":1,\"Rows\":1}",
            "{\"Scenario\":\"enumeration-walk-sequential\",\"InputPath\":\"x\",\"Iterations\":1,\"Rows\":0}"
        })
        {
            using var workerOut = new StringWriter();
            using var workerError = new StringWriter();
            Assert.AreEqual(1, await BenchmarkRunner.RunMemoryWorkerAsync(
                new StringReader(payload), workerOut, workerError));
            Assert.AreEqual("", workerOut.ToString());
            Assert.StartsWith("Error:", workerError.ToString());
        }

        Assert.ThrowsExactly<InvalidOperationException>(
            () => ScenarioMemoryProcess.ParseWorkerResult("exact-search", 1, 0, "{bad", ""));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ScenarioMemoryProcess.ParseWorkerResult(
                "exact-search", 1, 0, "{\"PeakPrivateBytes\":0,\"PeakWorkingSetBytes\":1,\"Method\":\"m\",\"ProcessId\":1}", ""));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ScenarioMemoryProcess.ParseWorkerResult(
                "exact-search", 1, 0, "{\"PeakPrivateBytes\":1,\"PeakWorkingSetBytes\":1,\"Method\":\"m\",\"ProcessId\":2}", ""));
    }

    [TestMethod]
    public async Task Run_Index_WithoutOut_WritesNoFile()
    {
        var beforeFiles = Directory.GetFiles(_directory);
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2"]));
        Assert.AreSequenceEqual(beforeFiles, Directory.GetFiles(_directory));
        Assert.IsFalse(_output.ToString().Contains("Report saved to", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Run_Index_Failure_WritesNoReport()
    {
        var report = Path.Combine(_directory, "report.txt");
        await File.WriteAllTextAsync(report, "keep me", TestContext.CancellationToken);
        var emptyCache = Directory.CreateDirectory(Path.Combine(_directory, "empty-cache")).FullName;
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--cache-directory", emptyCache, "--out", report]));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(report, TestContext.CancellationToken));

        var failingRunner = new BenchmarkRunner(_output, TimeProvider.System,
            _ => throw new InvalidOperationException("worker failed"));
        Assert.AreEqual(1, await failingRunner.RunAsync(["index", "--synthetic", "2", "--out", report]));
        Assert.Contains("worker failed", _output.ToString());
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(report, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Run_Index_WriteFailure_ReportsError()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["index", "--synthetic", "2", "--out", _directory]));
        Assert.Contains("Error:", _output.ToString());
    }

    [TestMethod]
    public async Task Run_OutputIsBorrowedAndReportsAreRealFiles()
    {
        await using var output = new StringWriter();
        var runner = new BenchmarkRunner(output);
        var path = Path.Combine(_directory, "report with spaces.txt");
        Assert.AreEqual(0, await runner.RunAsync(["index", "--synthetic", "2", "--out", path]));
        var saved = await File.ReadAllTextAsync(path, TestContext.CancellationToken);
        Assert.Contains("--- exact-search ---", saved);
        Assert.Contains("--- substring-search ---", saved);
        Assert.AreEqual(2, Regex.Count(saved, @"(?m)^  Peak private bytes: [1-9][0-9]*\r?$"));
        Assert.AreEqual(2, Regex.Count(saved, @"(?m)^  Peak working set: [1-9][0-9]*\r?$"));
        Assert.Contains($"Commit: {BenchmarkRunner.GetBuildCommit()}", saved);
        Assert.Contains($"Report saved to {Path.GetFullPath(path)}", output.ToString());
        Assert.AreEqual(0, await runner.RunAsync(["compare", path, path, "exact-search"]));
        var changed = new Regex(@"(?m)^(  Peak private bytes: )([0-9]+)(\r?)$")
            .Replace(saved, match =>
            {
                var bytes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var grown = checked((long)decimal.Floor(bytes * 1.25m) + 1);
                return match.Groups[1].Value + grown.ToString(CultureInfo.InvariantCulture) + match.Groups[3].Value;
            }, 1);
        var grownPath = Path.Combine(_directory, "real-memory-growth.txt");
        await File.WriteAllTextAsync(grownPath, changed, TestContext.CancellationToken);
        Assert.AreEqual(1, await runner.RunAsync(["compare", path, grownPath, "exact-search"]));
        Assert.AreEqual(0, await runner.RunAsync(["compare", path, grownPath, "substring-search"]));
        await output.WriteLineAsync("still owned by caller");
        Assert.Contains("still owned by caller", output.ToString());
    }

    [TestMethod]
    public async Task Compare_IndexReportAgainstItself_Passes_AndGrowthBeyondTheBoundFails()
    {
        var baselinePath = Path.Combine(_directory, "baseline.txt");
        Assert.AreEqual(0, await _runner.RunAsync(["index", "--synthetic", "2", "--out", baselinePath]));

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(0, await _runner.RunAsync(["compare", baselinePath, baselinePath, scenario]));
        }

        var saved = await File.ReadAllTextAsync(baselinePath, TestContext.CancellationToken);
        var changed = new Regex(@"(?m)^(  Peak private bytes: )([0-9]+)(\r?)$")
            .Replace(saved, match =>
            {
                var bytes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var grown = checked((long)decimal.Floor(bytes * 1.25m) + 1);
                return match.Groups[1].Value + grown.ToString(CultureInfo.InvariantCulture) + match.Groups[3].Value;
            }, 1);
        var memoryGrowthPath = Path.Combine(_directory, "memory-growth.txt");
        await File.WriteAllTextAsync(memoryGrowthPath, changed, TestContext.CancellationToken);
        Assert.AreEqual(1, await _runner.RunAsync(["compare", baselinePath, memoryGrowthPath, "exact-search"]));
        Assert.AreEqual(0, await _runner.RunAsync(["compare", baselinePath, memoryGrowthPath, "substring-search"]));
        Assert.Contains("Peak private bytes", _output.ToString());
        Assert.Contains("FAILED", _output.ToString());

        var literalBaseline = Path.Combine(_directory, "literal-baseline.txt");
        var grownPath = Path.Combine(_directory, "grown.txt");
        await File.WriteAllTextAsync(literalBaseline,
            ScenarioReport("4.000") + ScenarioReport("4.000", scenario: "substring-search"),
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(grownPath,
            ScenarioReport("5.001") + ScenarioReport("5.001", scenario: "substring-search"),
            TestContext.CancellationToken);

        foreach (var scenario in new[] { "exact-search", "substring-search" })
        {
            Assert.AreEqual(1, await _runner.RunAsync(["compare", literalBaseline, grownPath, scenario]));
        }

        Assert.Contains("FAILED", _output.ToString());
    }

    [TestMethod]
    [DataRow("4.000", "5.000", 100L, 125L, 0)]
    [DataRow("0.020", "0.025", 100L, 100L, 0)]
    [DataRow("4.000", "5.001", 100L, 100L, 1)]
    [DataRow("0.020", "0.026", 100L, 100L, 1)]
    [DataRow("4.000", "4.000", 100L, 126L, 1)]
    [DataRow("4.000", "1.000", 100L, 126L, 1)]
    [DataRow("4.000", "5.001", 100L, 50L, 1)]
    public async Task Compare_GrowthAgainstTheBound(
        string before, string after, long beforePrivate, long afterPrivate, int expectedExitCode)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.AreEqual(expectedExitCode, await Compare(
                ScenarioReport(before, beforePrivate),
                ScenarioReport(after, afterPrivate)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public async Task Compare_SpeedupAndZeroBaseline()
    {
        Assert.AreEqual(0, await Compare(ScenarioReport("4.000"), ScenarioReport("1.000")));
        Assert.AreEqual(0, await Compare(ScenarioReport("0.000"), ScenarioReport("0.000")));
        Assert.AreEqual(1, await Compare(ScenarioReport("0.000"), ScenarioReport("0.001")));
        Assert.AreEqual(1, await Compare(
            ScenarioReport("0.0000000000000000000000000001"),
            ScenarioReport("79228162514264337593543950335")));
        Assert.Contains("invalid measurement range", _output.ToString());
    }

    [TestMethod]
    public async Task Compare_ReadsTheNamedScenarioBlock()
    {
        var report = (ScenarioReport("1.000") + ScenarioReport("9.000", scenario: "substring-search"))
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.AreEqual(0, await Compare(report, report.Replace("1.000", "1.100", StringComparison.Ordinal)));
        var measurements = _runner.ReadScenarioMeasurements(
            Path.Combine(_directory, "before.txt"), "exact-search", "Baseline before");
        Assert.AreEqual(200L, measurements?.PeakWorkingSetBytes);
        Assert.AreEqual(1, await Compare(report, report.Replace("9.000", "99.000", StringComparison.Ordinal), "substring-search"));

        var firstBlockMissingPrivate =
            "--- exact-search ---\r\n  Median: 1.000 ms\r\n  Peak working set: 200\r\n  Memory measurement: windows-os-peak\r\n" +
            ScenarioReport("9.000", scenario: "substring-search").Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.AreEqual(1, await Compare(firstBlockMissingPrivate, report));
        Assert.Contains("no peak private bytes", _output.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--- exact-search ---\n  Matches: 1")]
    [DataRow("--- other ---\n  Median: 1.000 ms")]
    [DataRow("--- exact-search ---\n  Median: 1.000 ms; Extra: 1")]
    public async Task Compare_MissingScenarioOrMedian_ReturnsOne(string afterReport)
    {
        Assert.AreEqual(1, await Compare(ScenarioReport("1.000"), afterReport));
        Assert.Contains("no median for scenario 'exact-search'", _output.ToString());
    }

    [TestMethod]
    [DataRow("", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "no peak private bytes")]
    [DataRow("  Peak private bytes: -1\n", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "invalid peak private bytes")]
    [DataRow("  Peak private bytes: 0\n", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "invalid peak private bytes")]
    [DataRow("  Peak private bytes: nope\n", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "invalid peak private bytes")]
    [DataRow("  Peak private bytes: 9223372036854775808\n", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "invalid peak private bytes")]
    [DataRow("  Peak private bytes: 100\n  Peak private bytes: 100\n", "  Peak working set: 200\n", "  Memory measurement: windows-os-peak\n", "invalid peak private bytes")]
    [DataRow("  Peak private bytes: 100\n", "", "  Memory measurement: windows-os-peak\n", "invalid peak working set")]
    [DataRow("  Peak private bytes: 100\n", "  Peak working set: 0\n", "  Memory measurement: windows-os-peak\n", "invalid peak working set")]
    [DataRow("  Peak private bytes: 100\n", "  Peak working set: 200\n", "", "no memory measurement method")]
    [DataRow("  Peak private bytes: 100\n", "  Peak working set: 200\n", "  Memory measurement: \n", "no memory measurement method")]
    [DataRow("  Peak private bytes: 100\n", "  Peak working set: 200\n", "  Memory measurement: linux-sampled-vmdata-10ms\n", "memory measurement methods differ")]
    public async Task Compare_InvalidMemory_ReturnsOne(
        string privateLine, string workingSetLine, string methodLine, string message)
    {
        var invalid = "--- exact-search ---\n  Median: 1.000 ms\n" + privateLine + workingSetLine + methodLine;
        Assert.AreEqual(1, await Compare(invalid, ScenarioReport("1.000")));
        if (message != "memory measurement methods differ")
        {
            Assert.Contains("Baseline before", _output.ToString());
        }
        Assert.Contains(message, _output.ToString());
        _output.GetStringBuilder().Clear();
        Assert.AreEqual(1, await Compare(ScenarioReport("1.000"), invalid));
        if (message != "memory measurement methods differ")
        {
            Assert.Contains("Benchmark after", _output.ToString());
        }
        Assert.Contains(message, _output.ToString());
    }

    [TestMethod]
    public async Task Compare_MissingFiles_ReturnsOne()
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        Assert.Contains($"Baseline before file not found: {before}", _output.ToString());
        await File.WriteAllTextAsync(before, ScenarioReport("1.000"), TestContext.CancellationToken);
        Assert.AreEqual(1, await _runner.RunAsync(["compare", before, after, "exact-search"]));
        Assert.Contains($"Benchmark after file not found: {after}", _output.ToString());
    }

    [TestMethod]
    public async Task Compare_MissingArguments_ReportsUsage()
    {
        Assert.AreEqual(1, await _runner.RunAsync(["compare", "before.txt", "after.txt"]));
        Assert.Contains("Usage: Benchmark.exe compare", _output.ToString());
    }

    static string ScenarioReport(string median, long privateBytes = 100, long workingSet = 200,
        string scenario = "exact-search", string method = "windows-os-peak") =>
        string.Create(CultureInfo.InvariantCulture,
            $"--- {scenario} ---\n  Median: {median} ms; Matches: 1\n  Peak private bytes: {privateBytes}\n  Peak working set: {workingSet}\n  Memory measurement: {method}\n");

    static Task<ScenarioMemoryResult> FixedMemoryAsync(ScenarioMemoryRequest request) =>
        Task.FromResult(new ScenarioMemoryResult(
            request.Scenario == "exact-search" ? 100 : 200, 300, "windows-os-peak", Environment.ProcessId));

    async Task<int> Compare(string beforeReport, string afterReport, string scenario = "exact-search")
    {
        var before = Path.Combine(_directory, "before.txt");
        var after = Path.Combine(_directory, "after.txt");
        await File.WriteAllTextAsync(before, beforeReport, TestContext.CancellationToken);
        await File.WriteAllTextAsync(after, afterReport, TestContext.CancellationToken);
        return await _runner.RunAsync(["compare", before, after, scenario]);
    }

    string _directory = null!;
    StringWriter _output = null!;
    BenchmarkRunner _runner = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("benchmark-host-").FullName;
        _output = new StringWriter();
        _runner = new BenchmarkRunner(_output, TimeProvider.System, FixedMemoryAsync);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _output.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [GeneratedRegex(@"Bound: the (read \(I/O\)|parse \(CPU\)) stage dominates")]
    private static partial Regex MyRegex();
}

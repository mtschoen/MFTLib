using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using TestProgram;

namespace MFTLib.Tests;

// The modes that exercise the direct MftVolume API beyond the journal reads: name lookups, streamed
// results, volume sizing and the journal grow. A volume's parse is routed to the deterministic
// fixture image through the native seam, so the real MftVolume and MftResult code runs.
[TestClass]
[DoNotParallelize]
public class DriveScannerDirectApiTests
{
    string _fixturePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"driveScannerDirect-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }

        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecordsWithProgress = (_, filter, flags, buffer, control, callback) =>
            MFTLibNative._parseMftFromFile(_fixturePath, filter, flags, buffer, control, callback);
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        File.Delete(_fixturePath);
    }

    [TestMethod]
    public void Run_FindName_ContainsWithFreed_PrintsFreedRecordsWithTheirProperties()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        ElevatedScanner(lines).Run(["find-name", "T", "--name", "deleted", "--contains", "--include-freed"]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Found 8 records matching deleted in ", StringComparison.Ordinal)));
        var freed = lines.Single(line => line.Contains("[record 12 sequence 14 parent 5]"));
        StringAssert.Contains(freed, "freed");
        StringAssert.Contains(freed, "deleted-dir");
        Assert.IsTrue(lines.Contains("=== Drive T: done ==="));
    }

    [TestMethod]
    public void Run_FindName_ExactWithoutPaths_PrintsOneRecordWithItsSizeAndModifiedTime()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        ElevatedScanner(lines).Run(["find-name", "T", "--name", "resident.txt", "--no-paths"]);

        var record = lines.Single(line => line.StartsWith("  resident.txt [record 6 ", StringComparison.Ordinal));
        StringAssert.Contains(record, "parent 5] file in use");
        StringAssert.Contains(record, "size 37 modified ");
        Assert.IsFalse(record.Contains(" name "), "A name that is the whole display is not repeated.");
    }

    [TestMethod]
    public void Run_FindName_BufferSize_OpensTheVolumeWithThatBufferAndParsesWithIt()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();
        uint? openedWith = null;
        uint parsedWith = 0;
        var scanner = ElevatedScanner(lines);
        scanner._openVolumeWithBuffer = (letter, buffer) =>
        {
            openedWith = buffer;
            return MftVolume.Open(letter, buffer);
        };
        var route = MFTLibNative._parseMftRecordsWithProgress;
        MFTLibNative._parseMftRecordsWithProgress = (handle, filter, flags, buffer, control, callback) =>
        {
            parsedWith = buffer;
            return route(handle, filter, flags, buffer, control, callback);
        };

        scanner.Run(["find-name", "T", "--name", "resident.txt", "--buffer-size", "512"]);

        Assert.AreEqual(512u, openedWith);
        Assert.AreEqual(512u, parsedWith);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Run_ReadRecords_EachOptionCombination_ReadsThroughItsOverload(bool noPaths, bool timings)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();
        var arguments = new List<string> { "read-records", "T" };
        if (noPaths)
        {
            arguments.Add("--no-paths");
        }

        if (timings)
        {
            arguments.Add("--timings");
        }

        ElevatedScanner(lines).Run([.. arguments]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Read 8 records (", StringComparison.Ordinal)));
        Assert.AreEqual(timings, lines.Any(line => line.StartsWith("  Native: ", StringComparison.Ordinal)));
        Assert.AreEqual(!noPaths, lines.Any(line => line.Contains(" name nodata.dat ")));
    }

    [TestMethod]
    public void Run_StreamRecords_Defaults_ReportsEveryWayTheResultCanBeRead()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        ElevatedScanner(lines).Run(["stream-records", "T", "--threads", "2", "--batch-size", "3"]);

        Assert.AreEqual("Parse thread allowance: 2", lines.Single(line => line.StartsWith("Parse thread", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 24 records, 8 kept, ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  24 records; native IO ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Enumerated in place: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("Materialized 8 records in 3 batches"));
        Assert.IsTrue(lines.Contains("ToArray holds 8 records"));
        var retained = lines.IndexOf("Retained after the result was disposed:");
        Assert.IsTrue(retained > 0, "The first record is materialized and printed after the result is disposed.");
        StringAssert.Contains(lines[retained + 1], "[record ");
    }

    [TestMethod]
    public void Run_StreamRecords_NameFilter_RetainsTheMatchAfterDisposal()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        ElevatedScanner(lines).Run(["stream-records", "T", "--name", "resident.txt", "--no-paths"]);

        Assert.IsTrue(lines.Contains("Materialized 1 records in 1 batches"));
        var retained = lines.IndexOf("Retained after the result was disposed:");
        StringAssert.StartsWith(lines[retained + 1], "  resident.txt [record 6 ");
    }

    [TestMethod]
    public void Run_StreamRecords_NothingMatches_SaysThereIsNoRecordToRetain()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        ElevatedScanner(lines).Run(["stream-records", "T", "--name", "no-such-name"]);

        Assert.IsTrue(lines.Contains("Materialized 0 records in 0 batches"));
        Assert.IsTrue(lines.Contains("No record to retain."));
    }

    [TestMethod]
    public void Run_StreamRecords_ReportsTheLastSampleOfEachPhaseInOrder()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._streamRecords = (_, filter, flags, progress, parseThreads, cancellationToken) =>
        {
            progress!.Report(new MftScanProgress(MftScanPhase.Parsing, 10, 24, TimeSpan.FromMilliseconds(2)));
            progress.Report(new MftScanProgress(MftScanPhase.Parsing, 24, 24, TimeSpan.FromMilliseconds(5)));
            progress.Report(new MftScanProgress(MftScanPhase.ResolvingPaths, 8, 8, TimeSpan.FromMilliseconds(7)));
            return MftVolume.StreamMftFromFile(_fixturePath, filter, flags, new(progress, parseThreads, CancellationToken: cancellationToken));
        };

        scanner.Run(["stream-records", "T"]);

        var parsing = lines.IndexOf("  Parsing: 24 of 24 records after 5ms");
        var resolving = lines.IndexOf("  ResolvingPaths: 8 of 8 records after 7ms");
        var parsed = lines.FindIndex(line => line.StartsWith("Parsed ", StringComparison.Ordinal));
        Assert.IsTrue(parsing >= 0 && parsing < resolving && resolving < parsed);
        Assert.IsFalse(lines.Any(line => line.Contains("Parsing: 10 of")), "Only the last sample of a phase prints.");
    }

    [TestMethod]
    public void Run_StreamRecords_NoProgressReported_PrintsNoPhaseLines()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();
        // A native parse that never invokes the progress callback.
        MFTLibNative._parseMftRecordsWithProgress = (_, filter, flags, buffer, control, _) =>
            MFTLibNative._parseMftFromFile(_fixturePath, filter, flags, buffer, control, null);

        ElevatedScanner(lines).Run(["stream-records", "T"]);

        Assert.IsFalse(lines.Any(line => line.StartsWith("  Parsing:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_StreamRecords_TimeoutElapses_ReportsTheCancellation()
    {
        var lines = new List<string>();
        var cancellation = new CancellationTokenSource();
        TimeSpan? requested = null;
        CancellationToken? passed = null;
        var expectedToken = cancellation.Token;
        var scanner = ElevatedScanner(lines);
        scanner._createTimedCancellation = duration =>
        {
            requested = duration;
            return cancellation;
        };
        scanner._streamRecords = (_, _, _, _, _, token) =>
        {
            passed = token;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new AssertFailedException("The scan should have been cancelled.");
        };

        scanner.Run(["stream-records", "T", "--timeout-seconds", "4"]);

        Assert.AreEqual(TimeSpan.FromSeconds(4), requested);
        Assert.AreEqual(expectedToken, passed);
        Assert.IsTrue(lines.Contains("Scan cancelled before it finished."));
        Assert.IsFalse(lines.Any(line => line.StartsWith("Parsed ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("=== Drive T: done ==="));
    }

    [TestMethod]
    public void Run_StreamRecords_CancellationNobodyRequested_IsAnOrdinaryError()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._streamRecords = (_, _, _, _, _, _) => throw new OperationCanceledException("native stop");

        scanner.Run(["stream-records", "T"]);

        Assert.IsTrue(lines.Contains("Error on drive T: native stop"));
    }

    [TestMethod]
    public void Run_StreamRecords_WithoutATimeout_NeverCreatesATimedCancellation()
    {
        var scanner = ElevatedScanner([]);
        var cancelledAtStart = true;
        scanner._createTimedCancellation = _ => throw new AssertFailedException("No timeout was requested.");
        scanner._streamRecords = (_, _, _, _, _, token) =>
        {
            cancelledAtStart = token.IsCancellationRequested;
            throw new IOException("stop here");
        };

        scanner.Run(["stream-records", "T"]);

        Assert.IsFalse(cancelledAtStart);
    }

    [TestMethod]
    public void Run_VolumeInfo_PrintsTheSizingAndTheDerivedRecordCount()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        string? queried = null;
        scanner._queryVolumeInformation = letter =>
        {
            queried = letter;
            return new NtfsVolumeInformation(1024 * 1000, 1024);
        };

        scanner.Run(["volume-info", "t:"]);

        Assert.AreEqual("t", queried);
        Assert.IsTrue(lines.Contains("MFT valid data length 1024000 bytes"));
        Assert.IsTrue(lines.Contains("Bytes per file record segment 1024"));
        Assert.IsTrue(lines.Contains("Approximate MFT record count 1000"));
        Assert.IsTrue(lines.Contains("=== Drive t: done ==="));
    }

    [TestMethod]
    public void Run_VolumeInfo_QueryFails_PrintsTheErrorAndCarriesOn()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._queryVolumeInformation = _ => throw new IOException("Access denied");

        var result = scanner.Run(["volume-info", "T"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Error on drive T: Access denied"));
    }

    [TestMethod]
    public void Run_UsnGrow_PrintsTheSettingsBeforeAndAfterTheRequestedGrow()
    {
        var lines = new List<string>();
        var order = new List<string>();
        (long Maximum, long Delta)? requested = null;
        var scanner = ElevatedScanner(lines);
        scanner._openVolumeWithBuffer = (_, _) => throw new AssertFailedException("No buffer option was given.");
        scanner._queryJournalSettings = _ =>
        {
            order.Add("query");
            return new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 1024 };
        };
        scanner._growJournal = (_, maximum, delta) =>
        {
            order.Add("grow");
            requested = (maximum, delta);
            return new UsnJournalSettings { MaximumSize = 8192, AllocationDelta = 2048 };
        };

        scanner.Run(["usn-grow", "T", "--maximum-size", "8000", "--allocation-delta", "2048"]);

        CollectionAssert.AreEqual(new[] { "query", "grow" }, order);
        Assert.AreEqual((8000L, 2048L), requested);
        var before = lines.IndexOf("Before: maximum size 4096 bytes, allocation delta 1024 bytes");
        var request = lines.IndexOf("Requesting maximum size 8000 bytes, allocation delta 2048 bytes");
        var after = lines.IndexOf("After: maximum size 8192 bytes, allocation delta 2048 bytes");
        Assert.IsTrue(before >= 0 && before < request && request < after);
    }

    [TestMethod]
    public void Run_UsnGrow_GrowRefused_PrintsTheErrorAfterTheBeforeSettings()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._queryJournalSettings = _ => new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 1024 };
        scanner._growJournal = (_, _, _) => throw new InvalidOperationException("only growth is permitted");

        scanner.Run(["usn-grow", "T", "--maximum-size", "10", "--allocation-delta", "1"]);

        Assert.IsTrue(lines.Contains("Before: maximum size 4096 bytes, allocation delta 1024 bytes"));
        Assert.IsTrue(lines.Contains("Error on drive T: only growth is permitted"));
        Assert.IsFalse(lines.Any(line => line.StartsWith("After:", StringComparison.Ordinal)));
    }

    [DataTestMethod]
    [DataRow("usn-query")]
    [DataRow("usn-read")]
    [DataRow("read-records")]
    [DataRow("volume-info")]
    public void Run_OtherModes_NeverGrowTheJournal(string mode)
    {
        var scanner = ElevatedScanner([]);
        var growInvocations = 0;
        scanner._growJournal = (_, _, _) =>
        {
            growInvocations++;
            return new UsnJournalSettings { MaximumSize = 1, AllocationDelta = 1 };
        };
        scanner._queryJournal = _ => new UsnJournalCursor(7, 1000);
        scanner._queryJournalSettings = _ => new UsnJournalSettings { MaximumSize = 1, AllocationDelta = 1 };
        scanner._readJournal = (_, since) => ([], since);
        scanner._readAllRecords = (_, _, _) => ([], null);
        scanner._queryVolumeInformation = _ => new NtfsVolumeInformation(0, 0);

        var result = scanner.Run([mode, "T"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(0, growInvocations);
    }

    [TestMethod]
    public void Run_UsnRead_PrintsEveryEntryPropertyAndTheFlagsItsHelpersReport()
    {
        var lines = new List<string>();
        var scanner = ElevatedScanner(lines);
        scanner._queryJournal = _ => new UsnJournalCursor(7, 1000);
        scanner._readAllRecords = (_, _, _) => ([], null);
        scanner._readJournal = (_, since) =>
        (
            [
                JournalEntries.Create(20, 1200, "made.txt", UsnReason.FileCreate | UsnReason.Close),
                JournalEntries.Create(21, 1300, "gone.txt", UsnReason.FileDelete),
                JournalEntries.Create(22, 1400, "moved.txt", UsnReason.RenameNewName,
                    FileAttributes.Archive)
            ],
            since);

        scanner.Run(["usn-read", "T"]);

        var created = lines.Single(line => line.Contains("made.txt"));
        StringAssert.StartsWith(created, "  USN 1200 1970-01-01 00:00:00Z [FileCreate, Close] made.txt (record 20) parent 5 sequence 0");
        StringAssert.EndsWith(created, "flags [create close]");
        StringAssert.EndsWith(lines.Single(line => line.Contains("gone.txt")), "flags [delete]");
        var moved = lines.Single(line => line.Contains("moved.txt"));
        StringAssert.Contains(moved, " Archive ");
        StringAssert.EndsWith(moved, "flags [rename]");
    }

    [TestMethod]
    public void FormatRecord_UnknownSizeAndNoTimestamp_SaysSo()
    {
        var record = new MftRecord(30, 5, new MftRecordFields(0x8001, FileAttributes.Hidden, 0, 0, 9), "ghost", "T:\\ghost");

        var line = DriveScanner.FormatRecord(record);

        Assert.AreEqual(
            "  T:\\ghost name ghost [record 30 sequence 9 parent 5] file in use Hidden size unknown modified none", line);
    }

    [TestMethod]
    public void Constructor_ElevationSeams_DefaultToTheLibraryProvider()
    {
        var scanner = new DriveScanner();

        Assert.AreEqual(ElevationUtilities.DefaultProvider.IsElevated(), scanner._isElevated());
        Assert.AreEqual(ElevationUtilities.DefaultProvider.CanSelfElevate(), scanner._canSelfElevate());
    }

    static DriveScanner ElevatedScanner(List<string> lines)
    {
        return new DriveScanner
        {
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _writeLine = lines.Add
        };
    }
}

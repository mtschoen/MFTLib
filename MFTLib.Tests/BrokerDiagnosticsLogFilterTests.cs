using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerDiagnosticsLogFilterTests
{
    const string OwnLogPath = @"C:\broker-diag\broker-diagnostics.log";
    const string ClientLogPath = @"C:\client-diag\broker-diagnostics.log";
    const ulong OwnLogReference = 9101;
    const ulong ClientLogReference = 9102;

    [TestInitialize]
    public void Setup()
    {
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
            path == OwnLogPath ? OwnLogReference :
            path == ClientLogPath ? ClientLogReference :
            null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        BrokerDiagnosticsLogFilter.ResetToDefaults();
        BrokerDiagnostics.ResetToDefaults();
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
    }

    static UsnJournalEntry Entry(ulong recordNumber, string fileName) =>
        JournalEntryFactory.Create(recordNumber, 110, fileName);

    [TestMethod]
    public void Filter_DropsBothLogEntries_KeepsUnrelated()
    {
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, ClientLogPath);
        var entries = new[]
        {
            Entry(OwnLogReference, "broker-diagnostics.log"),
            Entry(ClientLogReference, "broker-diagnostics.log"),
            Entry(4242, "unrelated.txt")
        };

        var kept = filter.Filter("C", entries);

        Assert.AreEqual(1, kept.Length);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    public void Filter_WhenLogFileNotYetResolvable_KeepsEntries_ThenDropsOnceResolvable()
    {
        // The log file may not exist when the watch arms; the filter must retry
        // resolution on each batch instead of giving up.
        var resolvable = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        BrokerDiagnosticsLogFilter._resolveFileReference = _ => resolvable.Value ? OwnLogReference : null;
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null);
        var entries = new[] { Entry(OwnLogReference, "broker-diagnostics.log") };

        Assert.AreEqual(1, filter.Filter("C", entries).Length);

        resolvable.Value = true;
        Assert.AreEqual(0, filter.Filter("C", entries).Length);
    }

    [TestMethod]
    public void Filter_ScopesReferencesToTheLogPathsDrive()
    {
        const string otherDriveClientPath = @"D:\client-diag\broker-diagnostics.log";
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
            path == OwnLogPath ? OwnLogReference :
            path == otherDriveClientPath ? ClientLogReference :
            null;
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, otherDriveClientPath);
        var entries = new[] { Entry(ClientLogReference, "broker-diagnostics.log") };

        // The D-rooted path never resolves for a C batch, so the entry is kept there...
        Assert.AreEqual(1, filter.Filter("C", entries).Length);
        // ...and resolves for a D batch, where it is dropped.
        Assert.AreEqual(0, filter.Filter("D", entries).Length);
    }

    [TestMethod]
    public void Filter_WhenNothingMatches_ReturnsTheSameArrayInstance()
    {
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null);
        var entries = new[] { Entry(4242, "unrelated.txt") };

        Assert.AreSame(entries, filter.Filter("C", entries));
    }

    [TestMethod]
    public void Filter_ExtendedDriveRootPath_ResolvesAndFiltersMatchingEntries()
    {
        const string extendedPath = @"\\?\C:\broker-diag\broker-diagnostics.log";
        var resolvedPath = string.Empty;
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
        {
            resolvedPath = path;
            return path == extendedPath ? OwnLogReference : null;
        };

        var filter = new BrokerDiagnosticsLogFilter(extendedPath, null);
        var entries = new[]
        {
            Entry(OwnLogReference, "broker-diagnostics.log"),
            Entry(4242, "unrelated.txt")
        };

        var kept = filter.Filter("C", entries);

        Assert.AreEqual(extendedPath, resolvedPath);
        Assert.AreEqual(1, kept.Length);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    public void Filter_DeviceDriveRootPath_ResolvesAndFiltersMatchingEntries()
    {
        const string devicePath = @"\\.\C:\broker-diag\broker-diagnostics.log";
        var resolvedPath = string.Empty;
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
        {
            resolvedPath = path;
            return path == devicePath ? OwnLogReference : null;
        };

        var filter = new BrokerDiagnosticsLogFilter(devicePath, null);
        var entries = new[]
        {
            Entry(OwnLogReference, "broker-diagnostics.log"),
            Entry(4242, "unrelated.txt")
        };

        var kept = filter.Filter("C", entries);

        Assert.AreEqual(devicePath, resolvedPath);
        Assert.AreEqual(1, kept.Length);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    public void Filter_WhenLogFileReplaced_RefreshesFileReference_WhilePreservingRenameHandling()
    {
        const ulong initialReference = 9101;
        const ulong replacementReference = 9103;
        var currentReference = new System.Runtime.CompilerServices.StrongBox<ulong>(initialReference);
        var resolveCount = 0;

        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
        {
            if (path == OwnLogPath)
            {
                resolveCount++;
                return currentReference.Value;
            }

            return null;
        };

        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null);

        // Batch 1: initial log file entries are dropped.
        var batch1 = new[]
        {
            Entry(initialReference, "broker-diagnostics.log"),
            Entry(4242, "unrelated1.txt")
        };
        var kept1 = filter.Filter("C", batch1);
        Assert.AreEqual(1, resolveCount);
        Assert.AreEqual(1, kept1.Length);
        Assert.AreEqual("unrelated1.txt", kept1[0].FileName);

        // Simulate log rotation / replacement: file at OwnLogPath now has replacementReference.
        currentReference.Value = replacementReference;

        // Batch 2: contains an entry from the renamed original file AND an entry from the new replacement file.
        var batch2 = new[]
        {
            Entry(initialReference, "broker-diagnostics.log.bak"),
            Entry(replacementReference, "broker-diagnostics.log"),
            Entry(4243, "unrelated2.txt")
        };
        var kept2 = filter.Filter("C", batch2);
        Assert.AreEqual(2, resolveCount);
        Assert.AreEqual(1, kept2.Length);
        Assert.AreEqual("unrelated2.txt", kept2[0].FileName);

        // Batch 3: further writes to the replacement file continue to be dropped.
        var batch3 = new[]
        {
            Entry(replacementReference, "broker-diagnostics.log"),
            Entry(4244, "unrelated3.txt")
        };
        var kept3 = filter.Filter("C", batch3);
        Assert.AreEqual(3, resolveCount);
        Assert.AreEqual(1, kept3.Length);
        Assert.AreEqual("unrelated3.txt", kept3[0].FileName);
    }

    [TestMethod]
    public void LogPath_WhenLogDirectoryIsRelative_ResolvesToFullPathInOriginatingProcess()
    {
        var originalDirectory = BrokerDiagnostics.LogDirectory;
        try
        {
            BrokerDiagnostics.LogDirectory = "logs";
            var path = BrokerDiagnostics.LogPath;
            Assert.IsTrue(Path.IsPathRooted(path));
            StringAssert.EndsWith(path, Path.Combine("logs", BrokerDiagnostics.LogFileName));
        }
        finally
        {
            BrokerDiagnostics.LogDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public void TryGetDriveLetter_ValidDriveFormats_ReturnsTrueAndUppercaseLetter()
    {
        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"C:\logs\diag.log", out var drive1));
        Assert.AreEqual("C", drive1);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"c:\logs\diag.log", out var drive2));
        Assert.AreEqual("C", drive2);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"D:/logs/diag.log", out var drive3));
        Assert.AreEqual("D", drive3);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"\\?\E:\logs\diag.log", out var drive4));
        Assert.AreEqual("E", drive4);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"\\.\F:\logs\diag.log", out var drive5));
        Assert.AreEqual("F", drive5);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"\??\G:\logs\diag.log", out var drive6));
        Assert.AreEqual("G", drive6);
    }

    [TestMethod]
    public void TryGetDriveLetter_InvalidFormats_ReturnsFalse()
    {
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter(null!, out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter("", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter("   ", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"\\?\UNC\server\share\file.log", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"\\server\share\file.log", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter(@"1:\logs\diag.log", out _));
    }

    [TestMethod]
    public void CreateLogFilter_WhenDiagnosticsEnabled_ReturnsFilterThatDropsBothLogs()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        var originalDirectory = BrokerDiagnostics.LogDirectory;
        BrokerDiagnostics.LogDirectory = @"C:\broker-diag";
        // Key the seam on the combined path so the test is portable (Path.Combine joins
        // with the host separator).
        var ownPath = BrokerDiagnostics.LogPath;
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
            path == ownPath ? OwnLogReference :
            path == ClientLogPath ? ClientLogReference :
            null;
        try
        {
            BrokerDiagnostics.ClientLogPath = ClientLogPath;

            var filter = BrokerDiagnostics.CreateLogFilter();

            Assert.IsNotNull(filter);
            var entries = new[]
            {
                Entry(OwnLogReference, "broker-diagnostics.log"),
                Entry(ClientLogReference, "broker-diagnostics.log"),
                Entry(4242, "unrelated.txt")
            };
            var kept = filter.Filter("C", entries);
            Assert.AreEqual(1, kept.Length);
            Assert.AreEqual("unrelated.txt", kept[0].FileName);
        }
        finally
        {
            BrokerDiagnostics.LogDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public void CreateLogFilter_WhenDiagnosticsOff_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);

        Assert.IsNull(BrokerDiagnostics.CreateLogFilter());
    }

    [TestMethod]
    public void CreateLogFilter_WhenIncludeSelfEntries_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.IncludeSelfEntries = true;

        Assert.IsNull(BrokerDiagnostics.CreateLogFilter());
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ResolveFileReference_RealFile_MatchesThatFilesJournalEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("GetFileInformationByHandle is Windows-only.");
        }

        // Exercise the real CreateFileW + GetFileInformationByHandle path, not the seam.
        BrokerDiagnosticsLogFilter._resolveFileReference = BrokerDiagnosticsLogFilter.ResolveFileReference;
        var directory = Path.Combine(Path.GetTempPath(), "BrokerDiagFilterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var logPath = Path.Combine(directory, "broker-diagnostics.log");
            File.WriteAllText(logPath, "seed");
            var reference = BrokerDiagnosticsLogFilter.ResolveFileReference(logPath);
            Assert.IsTrue(reference.HasValue);

            var filter = new BrokerDiagnosticsLogFilter(logPath, null);
            var matching = UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = reference.Value & 0xFFFFFFFFFFFF,
                SequenceNumber = (ushort)(reference.Value >> 48),
                ParentRecordNumber = 5,
                Usn = 110,
                Timestamp = DateTime.UnixEpoch,
                Reason = UsnReason.Close,
                FileAttributes = FileAttributes.Normal,
                FileName = "broker-diagnostics.log"
            });
            var unrelated = Entry(4242, "unrelated.txt");
            var drive = Path.GetPathRoot(logPath)![0].ToString();

            var kept = filter.Filter(drive, new[] { matching, unrelated });

            Assert.AreEqual(1, kept.Length);
            Assert.AreEqual("unrelated.txt", kept[0].FileName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ResolveFileReference_ForAMissingFile_ReturnsNull()
    {
        // Exercise the real CreateFileW + GetFileInformationByHandle path, not the seam:
        // a file that does not exist cannot be opened, so the reference stays null.
        BrokerDiagnosticsLogFilter._resolveFileReference = BrokerDiagnosticsLogFilter.ResolveFileReference;
        var missing = Path.Combine(Path.GetTempPath(),
            $"mftlib-missing-{Guid.NewGuid():N}", "broker-diagnostics.log");

        Assert.IsNull(BrokerDiagnosticsLogFilter.ResolveFileReference(missing));
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TryGetDriveLetter_ForARelativePath_ResolvesAgainstTheCurrentDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The relative-path fallback runs only on Windows.");
        }

        var resolved = BrokerDiagnosticsLogFilter.TryGetDriveLetter("relative-name.log", out var driveLetter);

        Assert.IsTrue(resolved);
        var currentRoot = Path.GetPathRoot(Environment.CurrentDirectory);
        Assert.IsNotNull(currentRoot);
        Assert.AreEqual(char.ToUpperInvariant(currentRoot[0]).ToString(), driveLetter);
    }

    [TestMethod]
    public void TryGetDriveLetter_ForAPathGetFullPathRejects_ReturnsFalse()
    {
        // An embedded NUL makes Path.GetFullPath throw ArgumentException on Windows;
        // the filter treats the path as unmatchable rather than letting it escape.
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDriveLetter("bad\0path.log", out var driveLetter));
        Assert.AreEqual(string.Empty, driveLetter);
    }

    [TestMethod]
    public void Filter_WithAWhitespaceOwnLogPath_KeepsItVerbatimAndFiltersNothing()
    {
        // A whitespace log path is kept as-is (nothing to normalize) and can never
        // resolve to a drive's file reference, so the filter stays inert.
        var filter = new BrokerDiagnosticsLogFilter("   ", null);
        var entries = new[] { Entry(4242, "unrelated.txt") };

        var kept = filter.Filter("C", entries);

        Assert.AreEqual(1, kept.Length);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    public void Filter_WithAnOwnLogPathGetFullPathRejects_KeepsTheRawPathAndFiltersNothing()
    {
        // Same contract as the whitespace case, through the GetFullPath catch: the raw
        // path is kept and the filter stays inert rather than throwing from its ctor.
        var filter = new BrokerDiagnosticsLogFilter("bad\0path.log", null);
        var entries = new[] { Entry(4242, "unrelated.txt") };

        var kept = filter.Filter("C", entries);

        Assert.AreEqual(1, kept.Length);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }
}

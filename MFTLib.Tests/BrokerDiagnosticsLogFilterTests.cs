using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class BrokerDiagnosticsLogFilterTests
{
    const string OwnLogPath = @"C:\broker-diag\broker-diagnostics.broker.log";
    const string ClientLogPath = @"C:\broker-diag\broker-diagnostics.ui.log";
    const ulong OwnLogReference = 9101;
    const ulong ClientLogReference = 9102;

    [TestCleanup]
    public void Cleanup()
    {
        BrokerDiagnostics.Dependencies.ResetToDefaults();
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
    }

    static ulong? ResolveTestFileReference(string path) =>
        path == OwnLogPath ? OwnLogReference :
        path == ClientLogPath ? ClientLogReference : null;

    static UsnJournalEntry Entry(ulong reference, string fileName) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = reference & 0xFFFFFFFFFFFF,
            SequenceNumber = (ushort)(reference >> 48),
            ParentRecordNumber = 5,
            Usn = 110,
            TimestampUtc = DateTime.UnixEpoch,
            Reason = UsnReason.Close,
            FileAttributes = FileAttributes.Normal,
            FileName = fileName
        });

    [TestMethod]
    [DataRow('C')]
    [DataRow('c')]
    public void Filter_DropsBothLogEntries_KeepsUnrelated(char driveChar)
    {
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, ClientLogPath, ResolveTestFileReference);
        var otherFilter = new BrokerDiagnosticsLogFilter(OwnLogPath, null, _ => 4242UL);
        var entries = new[]
        {
            Entry(OwnLogReference, "broker-diagnostics.broker.log"),
            Entry(ClientLogReference, "broker-diagnostics.ui.log"),
            Entry(4242, "unrelated.txt")
        };

        var kept = filter.Filter(driveChar, entries);

        Assert.AreSequenceEqual(["unrelated.txt"], [.. kept.Select(entry => entry.FileName)]);
        Assert.AreSequenceEqual(
            ["broker-diagnostics.broker.log", "broker-diagnostics.ui.log"], [.. otherFilter.Filter(driveChar, entries).Select(entry => entry.FileName)]);
        Assert.AreSequenceEqual(["unrelated.txt"], [.. filter.Filter(driveChar, entries).Select(entry => entry.FileName)]);
    }

    [TestMethod]
    public void Filter_WhenLogFileNotYetResolvable_KeepsEntries_ThenDropsOnceResolvable()
    {
        // The log file may not exist when the watch arms; the filter must retry
        // resolution on each batch instead of giving up.
        var resolvable = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null, _ => resolvable.Value ? OwnLogReference : null);
        var entries = new[] { Entry(OwnLogReference, "broker-diagnostics.broker.log") };

        Assert.HasCount(1, filter.Filter('C', entries));

        resolvable.Value = true;
        Assert.IsEmpty(filter.Filter('C', entries));
    }

    [TestMethod]
    public void Filter_ScopesReferencesToTheLogPathsDrive()
    {
        const string otherDriveClientPath = @"D:\client-diag\broker-diagnostics.ui.log";
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, otherDriveClientPath, path =>
            path == OwnLogPath ? OwnLogReference :
            path == otherDriveClientPath ? ClientLogReference :
            null);
        var entries = new[] { Entry(ClientLogReference, "broker-diagnostics.ui.log") };

        // The D-rooted path never resolves for a C batch, so the entry is kept there...
        Assert.HasCount(1, filter.Filter('C', entries));
        // ...and resolves for a D batch, where it is dropped.
        Assert.IsEmpty(filter.Filter('D', entries));
    }

    [TestMethod]
    public void Filter_WhenNothingMatches_ReturnsTheSameArrayInstance()
    {
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null, ResolveTestFileReference);
        var entries = new[] { Entry(4242, "unrelated.txt") };

        Assert.AreSame(entries, filter.Filter('C', entries));
    }

    [TestMethod]
    [DataRow(@"\\?\C:\broker-diag\broker-diagnostics.broker.log", DisplayName = "extended drive root")]
    [DataRow(@"\\.\C:\broker-diag\broker-diagnostics.broker.log", DisplayName = "device drive root")]
    public void Filter_PrefixedDriveRootPath_ResolvesAndFiltersMatchingEntries(string prefixedPath)
    {
        var resolvedPath = string.Empty;
        var filter = new BrokerDiagnosticsLogFilter(prefixedPath, null, path =>
        {
            resolvedPath = path;
            return path == prefixedPath ? OwnLogReference : null;
        });
        var entries = new[]
        {
            Entry(OwnLogReference, "broker-diagnostics.broker.log"),
            Entry(4242, "unrelated.txt")
        };

        var kept = filter.Filter('C', entries);

        Assert.AreEqual(prefixedPath, resolvedPath);
        Assert.HasCount(1, kept);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    [DataRow(9103UL)]
    [DataRow(9101UL)]
    public void Filter_WhenLogFileReplaced_RefreshesFileReference_WhilePreservingRenameHandling(ulong replacementRecordNumber)
    {
        const ulong initialReference = (3UL << 48) | OwnLogReference;
        var replacementReference = (4UL << 48) | replacementRecordNumber;
        const ulong recycledReference = (5UL << 48) | OwnLogReference;
        var currentReference = new System.Runtime.CompilerServices.StrongBox<ulong>(initialReference);
        var filter = new BrokerDiagnosticsLogFilter(OwnLogPath, null,
            path => path == OwnLogPath ? currentReference.Value : null);
        var first = new[]
        {
            Entry(initialReference, "broker-diagnostics.broker.log"),
            Entry(recycledReference, "recycled.txt")
        };
        Assert.AreSequenceEqual(["recycled.txt"], [.. filter.Filter('C', first).Select(entry => entry.FileName)]);
        currentReference.Value = replacementReference;
        var replaced = new[]
        {
            Entry(initialReference, "renamed.log"),
            Entry(replacementReference, "broker-diagnostics.broker.log"),
            Entry(recycledReference, "recycled.txt"),
            Entry(4242, "unrelated.txt")
        };
        Assert.AreSequenceEqual(["recycled.txt", "unrelated.txt"], [.. filter.Filter('C', replaced).Select(entry => entry.FileName)]);
        Assert.AreSequenceEqual(["recycled.txt", "unrelated.txt"], [.. filter.Filter('C', replaced).Select(entry => entry.FileName)]);
    }

    [TestMethod]
    public void LogPath_WhenLogDirectoryIsRelative_ResolvesToFullPathInOriginatingProcess()
    {
        try
        {
            BrokerDiagnostics.Enable("client", "logs");
            var path = BrokerDiagnostics.LogPath;
            Assert.IsTrue(Path.IsPathRooted(path));
            Assert.EndsWith(Path.Combine("logs", BrokerDiagnostics.LogFileName), path);
        }
        finally
        {
            BrokerDiagnostics.Dependencies.ResetToDefaults();
        }
    }

    [TestMethod]
    public void TryGetDrive_ValidDriveFormats_ReturnsTrueAndVolumeKey()
    {
        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive(@"C:\logs\diag.log", out var drive1));
        Assert.AreEqual(new VolumeKey('C'), drive1);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive(@"c:\logs\diag.log", out var drive2));
        Assert.AreEqual(new VolumeKey('C'), drive2);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive("D:/logs/diag.log", out var drive3));
        Assert.AreEqual(new VolumeKey('D'), drive3);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive(@"\\?\E:\logs\diag.log", out var drive4));
        Assert.AreEqual(new VolumeKey('E'), drive4);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive(@"\\.\F:\logs\diag.log", out var drive5));
        Assert.AreEqual(new VolumeKey('F'), drive5);

        Assert.IsTrue(BrokerDiagnosticsLogFilter.TryGetDrive(@"\??\G:\logs\diag.log", out var drive6));
        Assert.AreEqual(new VolumeKey('G'), drive6);
    }

    [TestMethod]
    public void TryGetDrive_InvalidFormats_ReturnsFalse()
    {
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive(null!, out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive("", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive("   ", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive(@"\\?\UNC\server\share\file.log", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive(@"\\server\share\file.log", out _));
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive(@"1:\logs\diag.log", out _));
    }

    [TestMethod]
    public void CreateLogFilter_WhenDiagnosticsEnabled_ReturnsFilterThatDropsBothLogs()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.Enable("broker", @"C:\broker-diag");
        // Key the seam on the combined path so the test is portable (Path.Combine joins
        // with the host separator).
        var ownPath = BrokerDiagnostics.LogPath;
        try
        {
            BrokerDiagnostics.ClientLogPath = ClientLogPath;

            var filter = BrokerDiagnostics.CreateLogFilter(path =>
                path == ownPath ? OwnLogReference :
                path == ClientLogPath ? ClientLogReference :
                null);

            Assert.IsNotNull(filter);
            var entries = new[]
            {
                Entry(OwnLogReference, "broker-diagnostics.broker.log"),
                Entry(ClientLogReference, "broker-diagnostics.ui.log"),
                Entry(4242, "unrelated.txt")
            };
            var kept = filter.Filter('C', entries);
            Assert.HasCount(1, kept);
            Assert.AreEqual("unrelated.txt", kept[0].FileName);
        }
        finally
        {
            BrokerDiagnostics.Dependencies.ResetToDefaults();
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
    [OSCondition(OperatingSystems.Windows)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ResolveFileReference_RealFile_MatchesThatFilesJournalEntries()
    {
        // Exercise the real CreateFileW + GetFileInformationByHandle path, not the seam.
        using var directoryOwner = new TempDirectory("BrokerDiagFilterTests");
        var directory = directoryOwner.Path;
        var logPath = Path.Combine(directory, "broker-diagnostics.broker.log");
        File.WriteAllText(logPath, "seed");
        var reference = BrokerDiagnosticsLogFilter.ResolveFileReference(logPath);
        Assert.IsTrue(reference.HasValue);

        var filter = new BrokerDiagnosticsLogFilter(logPath, null);
        var refVal = reference.GetValueOrDefault();
        var matching = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = refVal & 0xFFFFFFFFFFFF,
            SequenceNumber = (ushort)(refVal >> 48),
            ParentRecordNumber = 5,
            Usn = 110,
            TimestampUtc = DateTime.UnixEpoch,
            Reason = UsnReason.Close,
            FileAttributes = FileAttributes.Normal,
            FileName = "broker-diagnostics.broker.log"
        });
        var unrelated = Entry(4242, "unrelated.txt");
        var drive = Path.GetPathRoot(logPath)![0];

        var kept = filter.Filter(drive, [matching, unrelated]);

        Assert.HasCount(1, kept);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }

    [TestMethod]
    public void ResolveFileReference_ForAMissingFile_ReturnsNull()
    {
        // Exercise the real CreateFileW + GetFileInformationByHandle path, not the seam:
        // a file that does not exist cannot be opened, so the reference stays null.
        using var missingDirectory = new TempDirectory("mftlib-missing", create: false);
        var missing = Path.Combine(missingDirectory.Path, "broker-diagnostics.broker.log");

        Assert.IsNull(BrokerDiagnosticsLogFilter.ResolveFileReference(missing));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TryGetDrive_ForARelativePath_ResolvesAgainstTheCurrentDrive()
    {
        var resolved = BrokerDiagnosticsLogFilter.TryGetDrive("relative-name.log", out var drive);

        Assert.IsTrue(resolved);
        var currentRoot = Path.GetPathRoot(Environment.CurrentDirectory);
        Assert.IsNotNull(currentRoot);
        Assert.AreEqual(new VolumeKey(currentRoot[0]), drive);
    }

    [TestMethod]
    public void TryGetDrive_ForAPathGetFullPathRejects_ReturnsFalse()
    {
        // An embedded NUL makes Path.GetFullPath throw ArgumentException on Windows;
        // the filter treats the path as unmatchable rather than letting it escape.
        Assert.IsFalse(BrokerDiagnosticsLogFilter.TryGetDrive("bad\0path.log", out var drive));
        Assert.AreEqual(default(VolumeKey), drive);
    }

    [TestMethod]
    [DataRow("   ", DisplayName = "whitespace path")]
    [DataRow("bad\0path.log", DisplayName = "path GetFullPath rejects")]
    public void Filter_WithAnUnresolvableOwnLogPath_KeepsTheRawPathAndFiltersNothing(string ownLogPath)
    {
        // A whitespace path is kept as-is (nothing to normalize); an embedded NUL makes
        // Path.GetFullPath throw and the raw path is kept rather than throwing from the ctor.
        // Neither can resolve to a drive's file reference, so the filter stays inert.
        var filter = new BrokerDiagnosticsLogFilter(ownLogPath, null);
        var entries = new[] { Entry(4242, "unrelated.txt") };

        var kept = filter.Filter('C', entries);

        Assert.HasCount(1, kept);
        Assert.AreEqual("unrelated.txt", kept[0].FileName);
    }
}

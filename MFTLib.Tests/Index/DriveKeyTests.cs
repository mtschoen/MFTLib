using System.Reflection;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class DriveKeyTests
{
    [TestMethod]
    [DataRow('c')]
    [DataRow('C')]
    [DataRow('z')]
    [DataRow('Z')]
    public void Constructor_NormalizesAsciiLetter(char letter)
    {
        var key = new DriveKey(letter);
        DriveKey converted = letter;
        Assert.AreEqual(char.ToUpperInvariant(letter), key.Letter);
        Assert.AreEqual(key, converted);
        Assert.AreEqual(new DriveKey(char.ToUpperInvariant(letter)), key);
        Assert.AreEqual(key.Letter.ToString(), key.ToString());
    }

    [TestMethod]
    [DataRow('\0')]
    [DataRow('1')]
    [DataRow(':')]
    [DataRow('@')]
    [DataRow('[')]
    [DataRow('`')]
    [DataRow('{')]
    [DataRow('\u00e9')]
    public void Constructor_RejectsNonAsciiLetters(char letter) =>
        Assert.ThrowsExactly<ArgumentException>(() => new DriveKey(letter));

    [TestMethod]
    public void DefaultKey_RejectsLetterAndFormatting()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = default(DriveKey).Letter);
        Assert.ThrowsExactly<ArgumentException>(() => default(DriveKey).ToString());
    }

    [TestMethod]
    public void ConversionOperators_AcceptCharOnly()
    {
        var operators = typeof(DriveKey).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit").ToArray();
        Assert.HasCount(1, operators);
        Assert.AreEqual("op_Implicit", operators[0].Name);
        Assert.AreEqual(typeof(char), operators[0].GetParameters().Single().ParameterType);
        Assert.AreEqual(typeof(DriveKey), operators[0].ReturnType);
    }

    [TestMethod]
    public void NotificationRecords_RejectDefaultKeysOnConstructionAndWithInitialization()
    {
        var settings = new UsnJournalSettings { MaximumSize = 65536, AllocationDelta = 4096 };
        var cachedBlockFile = new CachedBlockFile('C', 1, "unused", 0, DateTime.UnixEpoch);
        var driveStatus = new DriveStatus('C', DriveState.Offline);
        var driveOpened = new IndexDriveOpened('C', 1, 1);
        var scanProgress = new IndexScanProgress('C', IndexScanPhase.Finished, 0);
        var checkpointLoss = new JournalCheckpointLoss('C', JournalCheckpointLossDetection.LiveWatch,
            JournalCheckpointLossCause.CheckpointTrimmed, settings);
        var watchFault = new WatchFault(WatchFaultKind.Channel, 'C', new InvalidOperationException());
        Assert.ThrowsExactly<ArgumentException>(() => cachedBlockFile with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => driveStatus with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => driveOpened with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => scanProgress with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => checkpointLoss with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => watchFault with { Drive = default });
    }

    [TestMethod]
    public void PublicRecords_RejectDefaultKeysOnConstructionAndWithInitialization()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new IndexedDrive(default, "unused", 0));
        Assert.ThrowsExactly<ArgumentException>(() => new IndexRecordKey(default, 5, ProducerKind.Mft));
        Assert.ThrowsExactly<ArgumentException>(() => new DriveOperationResult(default, DriveOperationOutcome.Succeeded, null));
        Assert.ThrowsExactly<ArgumentException>(() => new WatchStateChange(default, new DriveWatchStatus(), null));
        var drive = new IndexedDrive('c', "unused", 0);
        var recordKey = new IndexRecordKey('C', 5, ProducerKind.Mft);
        var operationResult = new DriveOperationResult('C', DriveOperationOutcome.Succeeded, null);
        var stateChange = new WatchStateChange('C', new DriveWatchStatus(), null);
        Assert.ThrowsExactly<ArgumentException>(() => drive with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => recordKey with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => operationResult with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => stateChange with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticNotifications.CreateWatchStateChange(default, new DriveWatchStatus(), null));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticNotifications.CreateDriveOperationResult(default, DriveOperationOutcome.Succeeded, null));
    }

    [TestMethod]
    public void CacheSelections_RejectDefaultEvenWhenDirectoryIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mftlib-missing-drive-key-" + Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.InspectCached(directory, [default]));
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.DeleteCached(directory, [default]));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void DumpFactory_RejectsDefaultKey() =>
        Assert.ThrowsExactly<ArgumentException>(() => MftIndexSources.FromMftDumpFile("unused.mft", default));

    [TestMethod]
    public void TestPackageRecords_NormalizeAndRejectDefaultConstructionAndInitialization()
    {
        var constructor = typeof(InProcessBrokerScan).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 2);
        Assert.IsTrue(constructor.IsAssembly, "Scan observations are constructed inside the harness.");
        IReadOnlyCollection<string> retainedFileNames = [".git", "HEAD"];
        var scan = new InProcessBrokerScan('c', retainedFileNames);
        Assert.AreSame(retainedFileNames, scan.RetainedFileNames);
        Assert.IsNull(new InProcessBrokerScan('c', null).RetainedFileNames);
        Assert.IsEmpty(new InProcessBrokerScan('c', Array.Empty<string>()).RetainedFileNames!);
        Assert.AreSequenceEqual(["marker"], [.. (scan with { RetainedFileNames = new[] { "marker" } }).RetainedFileNames!]);
        var start = new ScriptedWatchStart('c', default);
        Assert.AreEqual('C', scan.Drive);
        Assert.AreEqual('C', start.Drive);
        Assert.AreEqual('D', (scan with { Drive = 'd' }).Drive);
        Assert.AreEqual('D', (start with { Drive = 'd' }).Drive);
        Assert.ThrowsExactly<ArgumentException>(() => new InProcessBrokerScan(default, null));
        Assert.ThrowsExactly<ArgumentException>(() => new ScriptedWatchStart(default, default));
        Assert.ThrowsExactly<ArgumentException>(() => scan with { Drive = default });
        Assert.ThrowsExactly<ArgumentException>(() => start with { Drive = default });
    }

    [TestMethod]
    [DataRow("C", 'C')]
    [DataRow("c", 'C')]
    [DataRow("C:", 'C')]
    [DataRow("c:", 'C')]
    [DataRow("C:\\", 'C')]
    [DataRow("c:\\", 'C')]
    [DataRow("C:/", 'C')]
    [DataRow("c:/", 'C')]
    [DataRow("C\\", 'C')]
    [DataRow("c\\", 'C')]
    [DataRow("C/", 'C')]
    [DataRow("c/", 'C')]
    [DataRow(" \tC\r\n", 'C')]
    [DataRow(" \tc: \r\n", 'C')]
    [DataRow(" \tc:\\ \r\n", 'C')]
    [DataRow("\u00a0c:/\u00a0", 'C')]
    [DataRow("a", 'A')]
    [DataRow("Z:/", 'Z')]
    public void TryParse_AcceptsDriveSpecs(string text, char expectedLetter)
    {
        Assert.IsTrue(DriveKey.TryParse(text, out var driveKey));
        Assert.AreEqual(expectedLetter, driveKey.Letter);
        Assert.AreEqual(new DriveKey(expectedLetter), driveKey);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow(@"\\server\share\")]
    [DataRow("//server/share/")]
    [DataRow(@"\\?\C:\")]
    [DataRow(@"\\.\C:")]
    [DataRow("CD")]
    [DataRow("CC:")]
    [DataRow("C,D")]
    [DataRow("C:\\folder")]
    [DataRow("C:/folder")]
    [DataRow("C:folder")]
    [DataRow("C/folder")]
    [DataRow("C\\folder")]
    [DataRow("C::")]
    [DataRow("C://")]
    [DataRow(@"C:\\")]
    [DataRow("C//")]
    [DataRow("C/:")]
    [DataRow("C :")]
    [DataRow("C: /")]
    [DataRow("C\t:")]
    [DataRow("1:")]
    [DataRow("@")]
    [DataRow("[")]
    [DataRow("`")]
    [DataRow("{")]
    [DataRow("\u00e9:")]
    [DataRow("\uff23:")]
    [DataRow("\0")]
    [DataRow("C\0")]
    [DataRow(":")]
    [DataRow("/")]
    [DataRow("\\")]
    public void TryParse_RejectsNonDriveSpecs(string? text)
    {
        Assert.IsFalse(DriveKey.TryParse(text, out var driveKey));
        Assert.AreEqual(default(DriveKey), driveKey);
    }
}

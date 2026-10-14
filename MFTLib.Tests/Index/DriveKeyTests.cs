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
        Assert.ThrowsException<ArgumentException>(() => new DriveKey(letter));

    [TestMethod]
    public void DefaultKey_RejectsLetterAndFormatting()
    {
        Assert.ThrowsException<ArgumentException>(() => _ = default(DriveKey).Letter);
        Assert.ThrowsException<ArgumentException>(() => default(DriveKey).ToString());
    }

    [TestMethod]
    public void ConversionOperators_AcceptCharOnly()
    {
        var operators = typeof(DriveKey).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit").ToArray();
        Assert.AreEqual(1, operators.Length);
        Assert.AreEqual("op_Implicit", operators[0].Name);
        Assert.AreEqual(typeof(char), operators[0].GetParameters().Single().ParameterType);
        Assert.AreEqual(typeof(DriveKey), operators[0].ReturnType);
    }

    [TestMethod]
    public void PublicRecords_RejectDefaultKeysOnConstructionAndWithInitialization()
    {
        Assert.ThrowsException<ArgumentException>(() => new IndexedDrive(default, "unused", 0));
        Assert.ThrowsException<ArgumentException>(() => new IndexRecordKey(default, 5, ProducerKind.Mft));
        Assert.ThrowsException<ArgumentException>(() => new DriveOperationResult(default, DriveOperationOutcome.Succeeded, null));
        Assert.ThrowsException<ArgumentException>(() => new WatchStateChange(default, new DriveWatchStatus(), null));
        var drive = new IndexedDrive('c', "unused", 0);
        Assert.ThrowsException<ArgumentException>(() => drive with { Drive = default });
        Assert.ThrowsException<ArgumentException>(() => new IndexRecordKey('C', 5, ProducerKind.Mft) with { Drive = default });
        Assert.ThrowsException<ArgumentException>(() => new DriveOperationResult('C', DriveOperationOutcome.Succeeded, null) with { Drive = default });
        Assert.ThrowsException<ArgumentException>(() => new WatchStateChange('C', new DriveWatchStatus(), null) with { Drive = default });
        Assert.ThrowsException<ArgumentException>(() =>
            SyntheticNotifications.CreateWatchStateChange(default, new DriveWatchStatus(), null));
        Assert.ThrowsException<ArgumentException>(() =>
            SyntheticNotifications.CreateDriveOperationResult(default, DriveOperationOutcome.Succeeded, null));
    }

    [TestMethod]
    public void CacheSelections_RejectDefaultEvenWhenDirectoryIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mftlib-missing-drive-key-" + Guid.NewGuid().ToString("N"));
        Assert.ThrowsException<ArgumentException>(() => CacheDirectory.InspectCached(directory, [default]));
        Assert.ThrowsException<ArgumentException>(() => CacheDirectory.DeleteCached(directory, [default]));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void DumpFactory_RejectsDefaultKey() =>
        Assert.ThrowsException<ArgumentException>(() => MftIndexSources.FromMftDumpFile("unused.mft", default));

    [TestMethod]
    public void TestPackageRecords_NormalizeAndRejectDefaultConstructionAndInitialization()
    {
        var scan = new InProcessBrokerScan('c', null);
        var start = new ScriptedWatchStart('c', default);
        Assert.AreEqual('C', scan.Drive);
        Assert.AreEqual('C', start.Drive);
        Assert.AreEqual('D', (scan with { Drive = 'd' }).Drive);
        Assert.AreEqual('D', (start with { Drive = 'd' }).Drive);
        Assert.ThrowsException<ArgumentException>(() => new InProcessBrokerScan(default, null));
        Assert.ThrowsException<ArgumentException>(() => new ScriptedWatchStart(default, default));
        Assert.ThrowsException<ArgumentException>(() => scan with { Drive = default });
        Assert.ThrowsException<ArgumentException>(() => start with { Drive = default });
    }
}

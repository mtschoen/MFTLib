using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The dump factory checks its two arguments and nothing else: it makes the path absolute once
///     and never opens the file, so a missing or invalid dump is a scan failure, not a factory one.
/// </summary>
[TestClass]
public class MftIndexSourcesDumpTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\t")]
    public void FromMftDumpFile_BlankPath_ThrowsArgumentExceptionNamingFilePath(string? filePath)
    {
        var failure = Assert.ThrowsException<ArgumentException>(
            () => MftIndexSources.FromMftDumpFile(filePath!, 'D'));

        Assert.AreEqual("filePath", failure.ParamName);
        StringAssert.StartsWith(failure.Message, "A dump file path is required.");
    }

    [TestMethod]
    [DataRow('1')]
    [DataRow(' ')]
    [DataRow(':')]
    [DataRow('\0')]
    [DataRow('é')]
    [DataRow('Ω')]
    [DataRow('@')]
    [DataRow('[')]
    [DataRow('\u017F')]
    public void FromMftDumpFile_KeyThatIsNotAnAsciiLetter_ThrowsArgumentOutOfRangeNamingDriveLetter(char driveLetter)
    {
        var failure = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => MftIndexSources.FromMftDumpFile("volume.mft", driveLetter));

        Assert.AreEqual("driveLetter", failure.ParamName);
        Assert.AreEqual(driveLetter, failure.ActualValue);
        StringAssert.StartsWith(failure.Message, "The logical drive key must be an ASCII letter.");
    }

    [TestMethod]
    [DataRow('d', 'D')]
    [DataRow('D', 'D')]
    [DataRow('z', 'Z')]
    [DataRow('A', 'A')]
    public void FromMftDumpFile_Key_IsTakenInUpperCase(char driveLetter, char expected)
    {
        var source = MftIndexSources.FromMftDumpFile("volume.mft", driveLetter);

        Assert.AreEqual(expected, source.DumpIdentity!.DriveLetter);
        Assert.AreEqual($"dump:/{expected}", source.DumpIdentity.Root);
    }

    [TestMethod]
    public void FromMftDumpFile_RelativePathOfAMissingFile_IsMadeAbsoluteWithoutOpeningIt()
    {
        var relative = Path.Combine("no-such-directory", $"{Guid.NewGuid():N}.mft");

        var source = MftIndexSources.FromMftDumpFile(relative, 'D');

        Assert.AreEqual(Path.GetFullPath(relative), source.DumpIdentity!.DumpFilePath);
        Assert.IsTrue(Path.IsPathFullyQualified(source.DumpIdentity.DumpFilePath));
        Assert.IsFalse(File.Exists(source.DumpIdentity.DumpFilePath));
    }

    [TestMethod]
    public void FromMftDumpFile_Source_CannotWatchAndIsNotUnavailable()
    {
        var source = MftIndexSources.FromMftDumpFile("volume.mft", 'D');

        Assert.IsNull(source.WatchSource);
        Assert.IsNull(source.UnavailableReason);
        Assert.IsNotNull(source.Producer);
    }

    [TestMethod]
    public void FromMftDumpFile_TwoCalls_ReturnSeparateSources()
    {
        var first = MftIndexSources.FromMftDumpFile("volume.mft", 'D');
        var second = MftIndexSources.FromMftDumpFile("volume.mft", 'D');

        Assert.AreNotSame(first, second);
        Assert.AreEqual(first.DumpIdentity, second.DumpIdentity);
    }
}

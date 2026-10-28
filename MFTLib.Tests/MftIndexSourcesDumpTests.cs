using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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
        var failure = Assert.ThrowsExactly<ArgumentException>(
            () => MftIndexSources.FromMftDumpFile(filePath!, 'D'));

        Assert.AreEqual("filePath", failure.ParamName);
        Assert.StartsWith("A dump file path is required.", failure.Message);
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
    public void DriveKeyConversion_RejectsNonAsciiLettersBeforeDumpCreation(char driveLetter)
    {
        var failure = Assert.ThrowsExactly<ArgumentException>(
            () => MftIndexSources.FromMftDumpFile("volume.mft", driveLetter));

        Assert.AreEqual("letter", failure.ParamName);
        Assert.StartsWith("The volume key must be an ASCII letter.", failure.Message);
    }

    [TestMethod]
    [DataRow('d', 'D')]
    [DataRow('D', 'D')]
    [DataRow('z', 'Z')]
    [DataRow('A', 'A')]
    public void FromMftDumpFile_Key_IsTakenInUpperCase(char driveLetter, char expected)
    {
        var source = MftIndexSources.FromMftDumpFile("volume.mft", driveLetter);

        Assert.AreEqual(expected, source.DumpIdentity!.Volume);
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
    public void FromMftDumpFile_Source_HasProducerAndCannotWatch()
    {
        var source = MftIndexSources.FromMftDumpFile("volume.mft", 'D');

        Assert.IsNull(source.WatchSource);
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

    [TestMethod]
    public async Task DumpProducer_ARequestMinimumRaisesOnlyThatRegion()
    {
        using var directory = new TempDirectory("mftlib-dump-minimum");
        var dumpPath = MftDumpFixture.WriteFile(directory.Path, MftDumpFixture.Standard());
        var producer = MftIndexSources.FromMftDumpFile(dumpPath, 'D').Producer;

        using var first = (await ProduceAsync(producer, directory.Path, default, TestContext.CancellationToken)).Block;
        using var second = (await ProduceAsync(producer, directory.Path,
            new BlockCapacities(0, 2 * first.NameEntryCapacity, 0), TestContext.CancellationToken)).Block;

        Assert.AreEqual(first.SlotCapacity, second.SlotCapacity);
        Assert.AreEqual(2 * first.NameEntryCapacity, second.NameEntryCapacity);
        Assert.AreEqual(first.NamePoolCapacity, second.NamePoolCapacity);

        static Task<MftBlockProduceResult> ProduceAsync(MftBlockProducer producer, string directoryPath,
            BlockCapacities minimum, CancellationToken cancellationToken)
        {
            var request = new MftBlockProduceRequest
            {
                Volume = 'D',
                VolumeSerial = 0,
                BlockPath = Path.Combine(directoryPath, $"{Guid.NewGuid():N}.mlix"),
                DeleteOnClose = true,
                MinimumCapacities = minimum
            };
            return producer(request, cancellationToken).WaitAsync(HangGuard, cancellationToken);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}

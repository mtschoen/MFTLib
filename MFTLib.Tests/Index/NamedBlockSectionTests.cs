using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NamedBlockSectionTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    string _directory = null!;
    string _blockPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-named-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _blockPath = Path.Combine(_directory, "T-0BADF00D.mlix");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Windows can hold a just-unmapped file briefly; a leftover temp directory is harmless.
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void CreateThenOpenExisting_SeesTheSameRows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped sections require Windows.");
            return;
        }

        var sectionName = NamedBlockSection.BuildSectionName('T');
        var options = new BlockFileCreateOptions
        {
            Path = _blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(64),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(1024)
        };

        var (creatorBlock, lifetime) = NamedBlockSection.Create(options, sectionName);
        using (lifetime)
        using (creatorBlock)
        {
            using var openedBlock = NamedBlockSection.OpenExisting(sectionName, creatorBlock.Length);
            var writer = new BlockWriter(openedBlock);
            Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(5, RowFlags.InUse | RowFlags.Directory,
                (uint)FileAttributes.Directory, 0, Moment.Ticks, 0)));

            // The creator's mapping and the opener's mapping are the same pages, so the row
            // the opener wrote is visible through the creator's view with no flush.
            Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, creatorBlock.Rows[5].Flags);
            Assert.AreEqual(5u, creatorBlock.Header.RootRow);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Create_MappingConstructionFails_DeletesTheFileAndDisposesTheStream()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped sections require Windows.");
            return;
        }

        var sectionName = NamedBlockSection.BuildSectionName('T');
        var firstOptions = new BlockFileCreateOptions
        {
            Path = _blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = 64,
            NamePoolCapacity = 256
        };
        var (firstBlock, firstLifetime) = NamedBlockSection.Create(firstOptions, sectionName);
        using (firstLifetime)
        using (firstBlock)
        {
            // A second create under the same already-published section name makes
            // MemoryMappedFile.CreateFromFile itself throw, before ownership transfers,
            // exercising the branch that deletes the second file and disposes its stream.
            var secondPath = Path.Combine(_directory, "second.mlix");
            var secondOptions = new BlockFileCreateOptions
            {
                Path = secondPath,
                VolumeSerial = 0x0BADF00E,
                ProducerKind = ProducerKind.Mft,
                SlotCapacity = 64,
                NamePoolCapacity = 256
            };

            Assert.ThrowsException<IOException>(() => NamedBlockSection.Create(secondOptions, sectionName));

            Assert.IsFalse(File.Exists(secondPath), "a failed create must not leave a header-less file behind");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void OpenExisting_UnknownSectionName_ThrowsFileNotFound()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped sections require Windows.");
            return;
        }

        Assert.ThrowsException<FileNotFoundException>(() =>
            NamedBlockSection.OpenExisting(NamedBlockSection.BuildSectionName('Z'), 4096));
    }
}

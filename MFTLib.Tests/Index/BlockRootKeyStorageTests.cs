using System.Buffers.Binary;
using System.Text;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class BlockRootKeyStorageTests : IDisposable
{
    const uint Serial = 0x0BADF00D;
    const int CountOffset = BlockLayout.HeaderFieldBytes;
    readonly TempDirectory _directoryOwner = new("mftlib-rootkey-storage");

    public void Dispose() => _directoryOwner.Dispose();

    static RootKey Folder(string name) =>
        RootKey.FromDirectory(Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "Stored", name));

    string Seed(RootKey root) =>
        SyntheticBlock.WriteCached(_directoryOwner.Path, root, Serial,
            new SyntheticBlockOptions { ProducerKind = ProducerKind.Enumeration, RootRow = 0 },
            [new SyntheticRow(0, root.ToString(), 0) { IsDirectory = true }]);

    static void WriteBytes(string path, int offset, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Position = offset;
        stream.Write(bytes);
    }

    static void StoreText(string path, string text, int? declaredCount = null)
    {
        var bytes = new byte[sizeof(ushort) + (text.Length * sizeof(char))];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(declaredCount ?? text.Length));
        Encoding.Unicode.GetBytes(text, bytes.AsSpan(sizeof(ushort)));
        WriteBytes(path, CountOffset, bytes);
    }

    [TestMethod]
    public void WrittenBlock_CarriesFormatVersionEight()
    {
        var bytes = File.ReadAllBytes(Seed(new VolumeKey('T')));

        Assert.AreEqual(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [TestMethod]
    public void KeyText_FollowsTheDeclaredHeaderAsCountThenCodeUnits()
    {
        var key = Folder("Stored Folder");
        var bytes = File.ReadAllBytes(Seed(key));
        var text = key.ToString();

        Assert.AreEqual(text.Length, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(CountOffset)));
        Assert.AreEqual(text, Encoding.Unicode.GetString(bytes, CountOffset + 2, text.Length * sizeof(char)));
    }

    [TestMethod]
    public void KeyText_VolumeRootStoresItsLetterTheSameWay()
    {
        var bytes = File.ReadAllBytes(Seed(new VolumeKey('t')));

        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(CountOffset)));
        Assert.AreEqual("T", Encoding.Unicode.GetString(bytes, CountOffset + 2, sizeof(char)));
    }

    [TestMethod]
    public void OpenCanonical_RoundTripsTheStoredKeyForBothShapes()
    {
        RootKey[] keys = [Folder("Stored Folder"), new VolumeKey('T')];
        foreach (var key in keys)
        {
            using var block = BlockFile.OpenCanonical(Seed(key), Serial, out var validation);

            Assert.IsNull(validation);
            Assert.IsNotNull(block);
            Assert.AreEqual(key, block.Root);
        }
    }

    [TestMethod]
    public void MaximumLengthKey_StoresAndReadsBack()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var key = RootKey.FromDirectory(root + new string('x', RootKey.MaximumTextUnits - root.Length));

        using var block = BlockFile.OpenCanonical(Seed(key), Serial, out var validation);

        Assert.IsNull(validation);
        Assert.AreEqual(key, block!.Root);
    }

    [TestMethod]
    public void Create_DefaultRoot_ThrowsAndLeavesNoFile()
    {
        var path = Path.Combine(_directoryOwner.Path, "default-root.mlix");

        Assert.ThrowsExactly<ArgumentException>(() => BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            Root = default,
            VolumeSerial = Serial,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = 16,
            NameEntryCapacity = 16,
            NamePoolCapacity = 4096
        }));

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Open_ZeroCount_IsRejectedAsInconsistent()
    {
        var path = Seed(new VolumeKey('T'));
        StoreText(path, string.Empty);

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.InconsistentRegions, validation);
    }

    [TestMethod]
    [DataRow(1984)]
    [DataRow(2048)]
    [DataRow(ushort.MaxValue)]
    public void Open_CountThatDoesNotFitThePage_IsRejectedAsInconsistent(int declaredCount)
    {
        var path = Seed(new VolumeKey('T'));
        StoreText(path, "T", declaredCount);

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.InconsistentRegions, validation);
    }

    [TestMethod]
    [DataRow("t")]
    [DataRow("T:")]
    [DataRow("relative")]
    [DataRow("T\0")]
    public void Open_TextThatDoesNotRoundTripToTheSameKey_IsRejectedAsInconsistent(string text)
    {
        var path = Seed(new VolumeKey('T'));
        StoreText(path, text);

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.InconsistentRegions, validation);
    }

    [TestMethod]
    public void Open_DirectoryTextWithTrailingSeparator_IsRejectedBecauseItIsNotNormalized()
    {
        var key = Folder("Stored Folder");
        var path = Seed(key);
        StoreText(path, key + Path.DirectorySeparatorChar.ToString());

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.InconsistentRegions, validation);
    }

    [TestMethod]
    public void OpenCanonical_NameThatTheStoredKeyDoesNotDerive_IsRejected()
    {
        var path = Seed(Folder("Stored Folder"));
        var misnamed = Path.Combine(_directoryOwner.Path, CacheDirectory.BlockFileName(Folder("Another"), Serial));
        File.Move(path, misnamed);

        using var block = BlockFile.OpenCanonical(misnamed, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.WrongRootDirectory, validation);
    }

    [TestMethod]
    public void OpenCanonical_NameWithAnotherSerialThanTheHeader_IsRejectedBeforeTheName()
    {
        var path = Seed(new VolumeKey('T'));

        using var block = BlockFile.OpenCanonical(path, Serial + 1, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.WrongVolumeSerial, validation);
    }

    [TestMethod]
    public void Open_PreviousFormatVersionBlock_IsRejectedWithoutReadingTheKey()
    {
        var path = Seed(new VolumeKey('T'));
        WriteBytes(path, 4, BitConverter.GetBytes(6u));

        using var block = BlockFile.Open(path, Serial, out var validation);

        Assert.IsNull(block);
        Assert.AreEqual(BlockValidationResult.WrongFormatVersion, validation);
    }
}

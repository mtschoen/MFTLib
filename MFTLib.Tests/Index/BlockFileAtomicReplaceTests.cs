using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953, the watch growth's publication rename. On Windows <c>File.Move(source, canonical,
///     overwrite: true)</c> cannot replace a canonical block file that is still mapped, although every block is opened
///     with <see cref="FileShare.Delete" />: the move is refused and both files stay as they were. The growth therefore
///     renames the canonical file aside first and the replacement into place second, on every platform, and a process
///     killed between the two renames leaves no canonical file, which the next open answers with a cold scan. Renaming
///     a mapped file aside is allowed, which is what the first of the two renames relies on.
/// </summary>
[TestClass]
public sealed class BlockFileAtomicReplaceTests
{
    const ulong OldSerial = 0x0000_1111;
    const ulong NewSerial = 0x0000_2222;

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void MoveWithOverwrite_OverAMappedCanonicalFile_IsRefusedOnWindowsAndLeavesBothFiles()
    {
        using var directory = new TempDirectory("mftlib-atomic-replace");
        var canonical = Path.Combine(directory.Path, "block.mlix");
        var growing = canonical + ".growing-test";
        using var old = TestBlocks.Create(canonical, volumeSerial: OldSerial, deleteOnClose: false);
        using var replacement = TestBlocks.Create(growing, volumeSerial: NewSerial, deleteOnClose: false);

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.Move(growing, canonical, overwrite: true));

        Assert.IsTrue(File.Exists(growing), "a refused move must leave the replacement where it was");
        Assert.AreEqual(OldSerial, ReadSerial(canonical), "a refused move must leave the canonical file untouched");
    }

    [TestMethod]
    public void TwoRenames_ReplaceAMappedCanonicalFile_AndTheOldMappingStaysReadable()
    {
        using var directory = new TempDirectory("mftlib-two-renames");
        var canonical = Path.Combine(directory.Path, "block.mlix");
        var growing = canonical + ".growing-test";
        var retired = canonical + ".retired-test";
        using var old = TestBlocks.Create(canonical, volumeSerial: OldSerial, deleteOnClose: false);
        using (var replacement = TestBlocks.Create(growing, volumeSerial: NewSerial, deleteOnClose: false))
        {
            File.Move(canonical, retired);
            File.Move(growing, canonical);
            replacement.AdoptRenamedPath(canonical);

            Assert.AreEqual(OldSerial, old.Header.VolumeSerial, "the renamed-aside file's mapping must stay readable");
            Assert.AreEqual(NewSerial, replacement.Header.VolumeSerial);
        }

        Assert.AreEqual(NewSerial, ReadSerial(canonical), "the canonical name must now open the replacement");
        CollectionAssert.AreEquivalent(new[] { canonical, retired }, Directory.GetFiles(directory.Path));
    }

    static ulong ReadSerial(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[BlockLayout.HeaderFieldBytes];
        stream.ReadExactly(header);
        var offset = (int)System.Runtime.InteropServices.Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.VolumeSerial));
        return BitConverter.ToUInt64(header, offset);
    }
}

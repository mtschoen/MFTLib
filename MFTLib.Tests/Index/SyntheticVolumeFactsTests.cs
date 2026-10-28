using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The facts a test-controlled volume reports to an open: which volume a root belongs to, where that volume is
///     rooted on the host, and the serial, drive type and format the test chose.
/// </summary>
[TestClass]
public sealed class SyntheticVolumeFactsTests
{
    static readonly VolumeKey VolumeT = new('T');
    static readonly string HostRoot = Path.Combine(Path.GetTempPath(), "synthetic-volume-facts", "t-root");

    static VolumeFacts Read(FileIndexOptions options, RootKey root, string rootDirectory) =>
        options.VolumeFacts!(root, rootDirectory);

    static FileIndexOptions Mapped(uint serial = 7, bool isNtfs = true, bool isLocal = true) =>
        SyntheticVolumeFacts.Apply(new FileIndexOptions(), serial, isNtfs, isLocal, VolumeT, HostRoot);

    [TestMethod]
    public void AVolumeKeyOfTheMappedVolume_ResolvesToTheHostDirectory()
    {
        var facts = Read(Mapped(), VolumeT, HostRoot);

        Assert.AreEqual(VolumeT, facts.Volume);
        Assert.AreEqual(HostRoot, facts.VolumeRootDirectory);
        Assert.AreEqual(7u, facts.VolumeSerial);
        Assert.IsTrue(facts.PlatformHasMft);
        Assert.IsTrue(facts.IsReady);
    }

    [TestMethod]
    [DataRow("", true)]
    [DataRow("child", true)]
    [DataRow("child/deeper", true)]
    [DataRow("-sibling", false)]
    public void ADirectory_BelongsToTheMappedVolumeOnlyWhenItLiesUnderItsHostDirectory(string suffix, bool belongs)
    {
        var path = suffix.Length == 0 ? HostRoot
            : suffix.StartsWith('-') ? HostRoot + suffix.TrimStart('-')
            : Path.Combine(HostRoot, suffix.Replace('/', Path.DirectorySeparatorChar));

        var facts = Read(Mapped(), RootKey.FromDirectory(Path.GetTempPath()), path);

        // A path outside the mapped directory falls through to the drive letter it spells, if any.
        Assert.AreEqual(belongs, facts.Volume == VolumeT);
        Assert.AreEqual(belongs, facts.VolumeRootDirectory == HostRoot);
    }

    [TestMethod]
    public void ADirectoryReachedThroughTheAlternateSeparator_BelongsToTheMappedVolume()
    {
        var path = HostRoot + Path.AltDirectorySeparatorChar + "child";

        var facts = Read(Mapped(), RootKey.FromDirectory(Path.GetTempPath()), path);

        Assert.AreEqual(VolumeT, facts.Volume);
    }

    [TestMethod]
    public void WithoutAMapping_AVolumeKeyIsItsOwnVolumeAtItsDriveRoot()
    {
        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions(), 9);

        var facts = Read(options, new VolumeKey('E'), @"E:\");

        Assert.AreEqual(new VolumeKey('E'), facts.Volume);
        Assert.AreEqual(@"E:\", facts.VolumeRootDirectory);
        Assert.AreEqual(9u, facts.VolumeSerial);
    }

    [TestMethod]
    public void AMappedVolumeDoesNotClaimAnotherVolumesKey()
    {
        var facts = Read(Mapped(), new VolumeKey('E'), @"E:\");

        Assert.AreEqual(new VolumeKey('E'), facts.Volume);
    }

    [TestMethod]
    [DataRow(@"D:\work", 'D')]
    [DataRow(@"d:\work", 'D')]
    public void WithoutAMapping_ADriveLetteredDirectoryBelongsToThatDrive(string path, char letter)
    {
        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions(), 9);

        var facts = Read(options, RootKey.FromDirectory(Path.GetTempPath()), path);

        Assert.AreEqual(new VolumeKey(letter), facts.Volume);
        Assert.AreEqual(char.ToUpperInvariant(letter) + @":\", facts.VolumeRootDirectory);
    }

    [TestMethod]
    [DataRow("/srv/data")]
    [DataRow(@"1:\data")]
    [DataRow("")]
    public void WithoutAMapping_ADirectoryWithNoDriveLetterBelongsToNoVolume(string path)
    {
        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions(), 9);

        var facts = Read(options, RootKey.FromDirectory(Path.GetTempPath()), path);

        Assert.IsNull(facts.Volume);
        Assert.IsNull(facts.VolumeRootDirectory);
    }

    [TestMethod]
    [DataRow(true, true, DriveType.Fixed, "NTFS")]
    [DataRow(false, true, DriveType.Fixed, "exFAT")]
    [DataRow(true, false, DriveType.Network, "NTFS")]
    public void TheChosenFileSystemAndDriveClassAreReported(bool isNtfs, bool isLocal, DriveType driveType, string format)
    {
        var facts = Read(Mapped(isNtfs: isNtfs, isLocal: isLocal), VolumeT, HostRoot);

        Assert.AreEqual(driveType, facts.DriveType);
        Assert.AreEqual(format, facts.Format);
    }
}

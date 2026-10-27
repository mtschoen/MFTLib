using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

[TestClass]
public class TempFileTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FileLifetime_OwnsTheLeafAndItsSiblings(bool create)
    {
        string path;
        string directory;
        using (var file = new TempFile(create: create))
        {
            path = file.Path;
            directory = Path.GetDirectoryName(path)!;
            Assert.IsTrue(Directory.Exists(directory));
            Assert.AreEqual(create, File.Exists(path));
            if (create)
            {
                Assert.AreEqual(0L, new FileInfo(path).Length);
            }

            using (var stream = new FileStream(path, FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None))
            {
                stream.WriteByte(42);
            }

            File.WriteAllText(Path.ChangeExtension(path, ".img"), "sibling");
        }

        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void ConcurrentOwners_HaveDistinctLeafNamesAndIndependentCleanup()
    {
        var first = new TempFile();
        try
        {
            using var second = new TempFile();
            Assert.AreNotEqual(first.Path, second.Path);
            Assert.AreNotEqual(Path.GetFileName(first.Path),
                Path.GetFileName(second.Path));
            first.Dispose();
            Assert.IsFalse(File.Exists(first.Path));
            Assert.IsTrue(File.Exists(second.Path));
            first.Dispose();
        }
        finally
        {
            first.Dispose();
        }
    }
}

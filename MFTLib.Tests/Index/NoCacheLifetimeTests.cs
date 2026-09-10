using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NoCacheLifetimeTests
{
    [TestMethod]
    public void NoCacheBlock_FileIsGoneOnceTheMappingIsDisposed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-nocache-{Guid.NewGuid():N}-probe.mlix");
        var length = BlockLayout.HeaderRegionBytes;
        var (mappedFile, view) = BlockFile.OpenMapping(path, FileMode.Create, length, length,
            FileOptions.DeleteOnClose);
        try
        {
            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            view.Dispose();
            mappedFile.Dispose();
        }

        Assert.IsFalse(File.Exists(path),
            "FileOptions.DeleteOnClose must remove the file once the mapping and its view are both disposed.");
    }

    [TestMethod]
    public void CacheModeBlock_FileSurvivesTheSameDisposalSequence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}-probe.mlix");
        var length = BlockLayout.HeaderRegionBytes;
        var (mappedFile, view) = BlockFile.OpenMapping(path, FileMode.Create, length, length,
            FileOptions.None);
        view.Dispose();
        mappedFile.Dispose();

        try
        {
            Assert.IsTrue(File.Exists(path), "A cache-mode mapping must not be removed on dispose.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

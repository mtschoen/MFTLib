using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.OpenAsync" />'s best-effort sweep of stale ".retired-*" siblings
///     left behind by a killed process, and what happens when that sweep cannot delete one.
/// </summary>
public partial class FileIndexResilienceTests
{
    [TestMethod]
    public async Task OpenAsync_CacheMode_DeletesAPreExistingRetiredSiblingForTheDrive()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staleRetiredPath = Path.Combine(_cacheDirectory,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process");

        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.IsFalse(File.Exists(staleRetiredPath));
    }

    [TestMethod]
    public async Task OpenAsync_CacheMode_LockedRetiredSibling_DoesNotFailTheOpen()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staleRetiredPath = Path.Combine(_cacheDirectory,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process");

        // No FileShare.Delete, so Windows' best-effort delete inside cleanup hits a sharing
        // violation, exercising the IOException branch that leaves the file alone rather than
        // failing the open that triggered the cleanup. Unix has no mandatory share-mode
        // locking, so the delete there succeeds even with the handle still open; either way
        // the open itself must not fail.
        using var lockingHandle =
            new FileStream(staleRetiredPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
    }

    [TestMethod]
    public async Task OpenAsync_CacheMode_ReadOnlyRetiredSibling_DoesNotFailTheOpen()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staleRetiredPath = Path.Combine(_cacheDirectory,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process");
        File.SetAttributes(staleRetiredPath, FileAttributes.ReadOnly);

        try
        {
            // Windows treats the read-only attribute as delete-denying, swallowed by cleanup.
            // Unix unlink ignores a file's own permission bits, so the delete there succeeds.
            // Either way the open itself must not fail.
            await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);

            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        }
        finally
        {
            if (File.Exists(staleRetiredPath))
            {
                File.SetAttributes(staleRetiredPath, FileAttributes.Normal);
            }
        }
    }
}

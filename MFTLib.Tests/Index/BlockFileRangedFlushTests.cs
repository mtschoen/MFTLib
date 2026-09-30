using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockFileRangedFlushTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    static readonly RowColumns RootColumns = new(
        ParentRow: 0,
        Flags: RowFlags.InUse | RowFlags.Directory,
        Attributes: 16,
        Size: 0,
        ModifiedTicks: Moment.Ticks,
        SequenceNumber: 0);

    string _directory = null!;
    string _blockPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-ranged-flush-{Guid.NewGuid():N}");
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

    BlockFile CreateBlock()
    {
        return BlockFile.Create(new BlockFileCreateOptions
        {
            Path = _blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = 64,
            NamePoolCapacity = 512
        });
    }

    [TestMethod]
    public void Flush_ViewLargerThanOneRange_ReportsEachRangeEndInOrder()
    {
        using var block = CreateBlock();
        var rangeBytes = (long)Environment.SystemPageSize;
        Assert.IsTrue(block.Length > 2 * rangeBytes, "The block must span more than two ranges.");
        block._flushRangeBytes = rangeBytes;
        var expected = new List<long>();
        for (var end = rangeBytes; end < block.Length; end += rangeBytes)
        {
            expected.Add(end);
        }

        expected.Add(block.Length);
        var reported = new List<long>();

        block.Flush(reported.Add);

        CollectionAssert.AreEqual(expected, reported);
        Assert.AreEqual(block.Length, reported[^1]);
    }

    [TestMethod]
    public void Flush_ViewSmallerThanOneRange_ReportsOnceWithTheViewLength()
    {
        using var block = CreateBlock();
        var reported = new List<long>();

        block.Flush(reported.Add);

        CollectionAssert.AreEqual(new[] { block.Length }, reported);
    }

    [TestMethod]
    public void Flush_NullCallback_Flushes()
    {
        using (var block = CreateBlock())
        {
            block._flushRangeBytes = Environment.SystemPageSize;
            new BlockWriter(block).TryWriteRow(0, "root", RootColumns);
            block.Flush(null);
        }

        using var reopened = new FileStream(_blockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[reopened.Length];
        reopened.ReadExactly(bytes);
        Assert.AreEqual(BlockLayout.Magic, BitConverter.ToUInt32(bytes, 0));
        Assert.IsTrue(System.Text.Encoding.Unicode.GetString(bytes, (int)BlockLayout.NamePoolOffset(64), 16)
            .StartsWith("root", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Flush_AfterDispose_Throws()
    {
        var block = CreateBlock();
        block.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => block.Flush(null));
    }

    [TestMethod]
    public void Complete_PassesTheCallbackToTheFlush()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "", RootColumns);
        var reported = new List<long>();

        writer.Complete(Moment, reported.Add);

        Assert.AreEqual(block.Length, reported[^1]);
        Assert.IsTrue(block.Header.IsComplete);
    }

    [TestMethod]
    public void Flush_CallbackThrows_PropagatesAndLeavesTheBlockUsable()
    {
        using var block = CreateBlock();
        block._flushRangeBytes = Environment.SystemPageSize;
        var thrown = CaptureException(block, _ => throw new InvalidOperationException("callback"));

        Assert.IsInstanceOfType<InvalidOperationException>(thrown);
        var reported = new List<long>();
        block.Flush(reported.Add);
        Assert.AreEqual(block.Length, reported[^1]);
        new BlockWriter(block).TryWriteRow(0, "", RootColumns);
    }

    [TestMethod]
    public void FlushViewWithRetry_LockViolationThatClears_IsRetriedAndSucceeds()
    {
        using var block = CreateBlock();
        var results = new Queue<int>([33, 33, 0]);
        var attempts = 0;
        var pauses = new List<int>();
        block._flushViewRange = (_, _) =>
        {
            attempts++;
            return results.Dequeue();
        };
        block._pauseMilliseconds = pauses.Add;
        block._spinOnce = static (ref _) => { };

        block.FlushViewWithRetry(0, block.Length);

        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(new[] { 1 }, pauses);
    }

    [TestMethod]
    public void FlushViewWithRetry_DifferentError_ThrowsAtOnce()
    {
        using var block = CreateBlock();
        var attempts = 0;
        var pauses = 0;
        block._flushViewRange = (_, _) =>
        {
            attempts++;
            return 5;
        };
        block._pauseMilliseconds = _ => pauses++;

        var thrown = (Win32Exception)CaptureRetryException(block)!;

        Assert.AreEqual(5, thrown.NativeErrorCode);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, pauses);
    }

    [TestMethod]
    public void FlushViewWithRetry_LockViolationForever_IsBounded()
    {
        using var block = CreateBlock();
        var attempts = 0;
        var pauses = new List<int>();
        block._flushViewRange = (_, _) =>
        {
            attempts++;
            return 33;
        };
        block._pauseMilliseconds = pauses.Add;
        block._spinOnce = static (ref _) => { };

        var thrown = (Win32Exception)CaptureRetryException(block)!;

        Assert.AreEqual(33, thrown.NativeErrorCode);
        Assert.AreEqual(1 + 15 * 20, attempts);
        CollectionAssert.AreEqual(Enumerable.Range(0, 15).Select(wait => 1 << wait).ToArray(), pauses);
    }

    [TestMethod]
    public void FlushViewWithRetry_LockViolationThenDifferentError_ThrowsWithoutSpinningAfterIt()
    {
        using var block = CreateBlock();
        var results = new Queue<int>([33, 33, 5]);
        var spins = 0;
        var pauses = new List<int>();
        block._flushViewRange = (_, _) => results.Dequeue();
        block._pauseMilliseconds = pauses.Add;
        block._spinOnce = (ref _) => spins++;

        var thrown = (Win32Exception)CaptureRetryException(block)!;

        Assert.AreEqual(5, thrown.NativeErrorCode);
        Assert.AreEqual(1, spins, "One spin follows the retry that still failed with 33, none follows the 5.");
        CollectionAssert.AreEqual(new[] { 1 }, pauses);
    }

    [TestMethod]
    public void ClassifyPlatform_OnlyLinuxAndMacOsAreRecognized()
    {
        Assert.AreEqual(OSPlatform.Linux, BlockFile.ClassifyPlatform(isLinux: true, isMacOS: false));
        Assert.AreEqual(OSPlatform.OSX, BlockFile.ClassifyPlatform(isLinux: false, isMacOS: true));
        var other = BlockFile.ClassifyPlatform(isLinux: false, isMacOS: false);
        Assert.AreNotEqual(OSPlatform.Linux, other);
        Assert.AreNotEqual(OSPlatform.OSX, other);
        Assert.ThrowsException<PlatformNotSupportedException>(() => Libc.SelectSynchronousFlag(other));
    }

    [TestMethod]
    public void SelectSynchronousFlag_ChoosesThePlatformValueAndRefusesOthers()
    {
        Assert.AreEqual(4, Libc.SelectSynchronousFlag(OSPlatform.Linux));
        Assert.AreEqual(0x10, Libc.SelectSynchronousFlag(OSPlatform.OSX));
        Assert.ThrowsException<PlatformNotSupportedException>(() => Libc.SelectSynchronousFlag(OSPlatform.Windows));
        Assert.ThrowsException<PlatformNotSupportedException>(() => Libc.SelectSynchronousFlag(OSPlatform.FreeBSD));
    }

    [TestMethod]
    [DataRow("LINUX", 4)]
    [DataRow("OSX", 0x10)]
    public void SynchronizeRange_UnalignedStart_SynchronizesFromItsPageBoundaryWithThePlatformFlag(
        string platform, int expectedFlag)
    {
        using var block = CreateBlock();
        var pageBytes = Environment.SystemPageSize;
        var calls = new List<(long Start, long End, int Flag)>();
        block._synchronizeViewRange = (start, end, flag) =>
        {
            calls.Add((start, end, flag));
            return 0;
        };

        block.SynchronizeRange(pageBytes + 7, pageBytes + 100, OSPlatform.Create(platform));

        CollectionAssert.AreEqual(new[] { ((long)pageBytes, (long)pageBytes + 100, expectedFlag) }, calls);
    }

    [TestMethod]
    public void SynchronizeRange_CallFails_ThrowsWithTheErrno()
    {
        using var block = CreateBlock();
        block._synchronizeViewRange = (_, _, _) => 5;

        IOException? thrown = null;
        try
        {
            block.SynchronizeRange(0, block.Length, OSPlatform.Linux);
        }
        catch (IOException exception)
        {
            thrown = exception;
        }

        Assert.IsNotNull(thrown, "a failed msync throws");
        Assert.AreEqual("msync failed with errno 5.", thrown.Message);
    }

    static Exception? CaptureRetryException(BlockFile block)
    {
        try
        {
            block.FlushViewWithRetry(0, block.Length);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    static Exception? CaptureException(BlockFile block, Action<long> callback)
    {
        try
        {
            block.Flush(callback);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}

using Benchmark;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BenchmarkRunnerTests
{
    /// <summary>
    ///     A block replaced after the benchmark inspected its slot, by an index publishing under
    ///     other row-shaping options, is refused by the cache-only open and its bytes survive.
    ///     The replacement runs from the benchmark's own report line for an earlier slot, which
    ///     it writes after inspecting every slot and before opening the later ones.
    /// </summary>
    [TestMethod]
    public async Task Run_Index_CacheDirectory_BlockReplacedAfterInspection_IsRefusedAndLeftIntact()
    {
        var directory = Path.Combine(_directory, "cache");
        var staging = Path.Combine(_directory, "staging");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(staging);

        // Slot A is declined before any open, so its report line marks the window between
        // inspection and the opens of B (enumeration) and C (MFT, whose root must exist).
        WriteCacheBlock(directory, directory, 'A',
            ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PruneDotDirectories = true }, directory));
        WriteCacheBlock(directory, directory, 'B', SyntheticScanShape.Default(ProducerKind.Enumeration));
        WriteCacheBlock(directory, directory, 'C', SyntheticScanShape.Default(ProducerKind.Mft), ProducerKind.Mft);
        WriteCacheBlock(staging, directory, 'B',
            ScanShape.ForEnumeration(null, new DirectoryWalkOptions { PrunedDirectoryNames = ["build"] }, directory));
        WriteCacheBlock(staging, directory, 'C', ScanShape.ForMft(null, includeFreed: true), ProducerKind.Mft);

        var replacements = Directory.GetFiles(staging, "*.mlix")
            .ToDictionary(path => Path.Combine(directory, Path.GetFileName(path)), File.ReadAllBytes);
        using var output = new LineTriggerWriter("A-00000001.mlix: unsupported scan shape", () =>
        {
            foreach (var (path, bytes) in replacements)
            {
                File.WriteAllBytes(path, bytes);
            }
        });

        Assert.AreEqual(1, await new BenchmarkRunner(output).RunAsync(["index", "--cache-directory", directory]));

        Assert.IsTrue(output.Triggered, output.ToString());
        Assert.Contains("Drive B: Failed", output.ToString());
        // An MFT block names a drive letter, and only Windows has a drive root to open it under;
        // elsewhere the slot is offline and never opened, so its bytes survive for that reason.
        Assert.Contains(OperatingSystem.IsWindows() ? "Drive C: Failed" : "Drive C: Offline",
output.ToString());
        var deleted = replacements.Keys.Where(path => !File.Exists(path)).Select(Path.GetFileName).ToList();
        Assert.IsEmpty(deleted, $"deleted: {string.Join(", ", deleted)}");
        foreach (var (path, bytes) in replacements)
        {
            Assert.AreSequenceEqual(bytes, await File.ReadAllBytesAsync(path), path);
        }
    }

    static void WriteCacheBlock(string directory, string rootDirectory, char drive, ulong scanShape,
        ProducerKind producer = ProducerKind.Enumeration)
    {
        var rootRow = producer == ProducerKind.Mft ? 5u : 0u;
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(directory, $"{drive}-00000001.mlix"),
            VolumeSerial = 1,
            ProducerKind = producer,
            RootRow = rootRow,
            SlotCapacity = 8,
            NamePoolCapacity = 4096,
            CacheTag = new CacheTag("TEST", drive),
            ScanShape = scanShape
        });
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(rootRow, producer == ProducerKind.Mft ? "" : rootDirectory,
            new RowColumns(rootRow, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0)));
        if (producer == ProducerKind.Enumeration)
        {
            Assert.IsTrue(writer.TryWriteRow(1, "file-0000000001", new RowColumns(0, RowFlags.InUse, 0, 1, 0, 0)));
        }
        writer.Complete(DateTime.UtcNow, null);
    }

    /// <summary>Runs an action once, when the first line containing a marker is written.</summary>
    sealed class LineTriggerWriter(string marker, Action action) : StringWriter
    {
        public bool Triggered { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!Triggered && value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                Triggered = true;
                action();
            }
        }
    }
}

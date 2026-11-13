using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The streaming parse a block production consumes: the native sink exports, the slices they deliver, a callback
///     failure, a consumer that stops early, and the native structs the sink shares with managed code.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed unsafe class MftSliceStreamTests : IDisposable
{
    // 70,000 one-KiB records: a first 64 MiB chunk the dump reads as concurrent parts, then a smaller serial chunk.
    const ulong TwoChunkRecordCount = 70_000;

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = new TempDirectory("mftlib-slice-stream");
    }

    public void Dispose()
    {
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        _tempDirectory.Dispose();
    }

    [TestMethod]
    public void Slices_OfATwoChunkDump_AreTheRecordsOfAWholeResultParse()
    {
        var path = WriteSynthetic(TwoChunkRecordCount);
        var expected = DirectParse.ParseFile(path, out _).Select(Describe).ToArray();
        var progress = new List<MftScanProgress>();

        using var input = MftDumpInput.Open(path);
        using var stream = input.OpenSliceStream(new DirectProgress<MftScanProgress>(progress.Add), null,
            TestContext.CancellationToken);
        var streamed = new List<string>();
        foreach (var slice in stream.Slices())
        {
            streamed.AddRange(Decode(slice));
            slice.Return();
        }

        var completion = stream.Finish();

        Assert.AreSequenceEqual(expected, streamed.ToArray());
        Assert.AreEqual(0L, completion.RecordsOmitted);
        Assert.AreEqual((long)TwoChunkRecordCount, progress[^1].RecordsScanned);
        Assert.AreEqual((long)TwoChunkRecordCount, progress[^1].TotalRecords);
        Assert.IsGreaterThan(1, progress.Count, "each chunk reports progress");
    }

    [TestMethod]
    public void DumpExport_WithoutACompleteSink_FailsWithoutParsing()
    {
        var path = WriteSynthetic(20);
        using var input = MFTLibNative.OpenMftDumpInput(MFTLibNative.NullTerminatedUtf8(path), out _);
        var incomplete = new MftParseSink { Slice = IntPtr.Zero, Extensions = new IntPtr(1), Context = IntPtr.Zero };

        foreach (var sink in new[] { IntPtr.Zero, (IntPtr)(&incomplete) })
        {
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                new MftResult(MFTLibNative.ParseMftDumpInputStreaming(input, 64, IntPtr.Zero, default, sink)));
            Assert.AreEqual("Dump input or sink is invalid", failure.Message);
        }
    }

    [TestMethod]
    public void VolumeExport_WithoutACompleteSink_FailsBeforeReadingTheHandle()
    {
        using var handle = File.OpenHandle(WriteSynthetic(20));
        var incomplete = new MftParseSink { Slice = new IntPtr(1), Extensions = IntPtr.Zero, Context = IntPtr.Zero };

        foreach (var sink in new[] { IntPtr.Zero, (IntPtr)(&incomplete) })
        {
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => new MftResult(
                MFTLibNative.Hooks._parseMftRecordsStreaming(handle, false, 64, IntPtr.Zero, default, sink)));
            Assert.AreEqual("Parse sink is invalid", failure.Message);
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "slice callback")]
    [DataRow(false, DisplayName = "extension callback")]
    public void Finish_AfterACallbackFailure_ThrowsThatFailureRatherThanTheCancellationItCaused(bool failSlice)
    {
        var returned = new List<int>();
        using var volume = OpenFakeVolume((_, _, _, _, _, sinkAddress) =>
        {
            var sink = *(MftParseSink*)sinkAddress;
            // A count past what a managed array holds makes the copy throw inside the callback.
            returned.Add(failSlice
                ? ((delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, ushort*, ulong, int>)sink.Slice)(
                    sink.Context, null, ulong.MaxValue, null, 0)
                : ((delegate* unmanaged[Cdecl]<IntPtr, MftExtensionDataSize*, ulong, int>)sink.Extensions)(
                    sink.Context, null, ulong.MaxValue));
            return CancelledResult();
        });
        using var stream = volume.OpenSliceStream(null, null, new MftScanOptions(), TestContext.CancellationToken);

        Assert.IsEmpty(stream.Slices().ToArray());
        Assert.ThrowsExactly<OverflowException>(() => stream.Finish());
        Assert.AreSequenceEqual([1], returned.ToArray(), "the failing callback stops the parse");
    }

    [TestMethod]
    public void Dispose_BeforeTheLastSlice_StopsTheParseAtItsNextSlice()
    {
        const int offeredSlices = 1000;
        var offered = 0;
        var stopped = false;
        var record = new PinnedSlice(MftParsedSlice.Pack([new MftRecord(7, 5, new MftRecordFields(1), "a")]));
        try
        {
            var entries = record.Entries;
            var strings = record.Strings;
            using var volume = OpenFakeVolume((_, _, _, _, _, sinkAddress) =>
            {
                var sink = *(MftParseSink*)sinkAddress;
                var deliver = (delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, ushort*, ulong, int>)sink.Slice;
                while (offered < offeredSlices && !stopped)
                {
                    offered++;
                    stopped = deliver(sink.Context, entries, 1, strings, 1) != 0;
                }

                return CancelledResult();
            });
            var stream = volume.OpenSliceStream(null, null, new MftScanOptions(), TestContext.CancellationToken);

            stream.Slices().First().Return();
            stream.Dispose();
        }
        finally
        {
            // The stream's disposal waited for the parse, so nothing reads the pinned slice any more.
            record.Dispose();
        }

        Assert.IsTrue(stopped, "the parse saw the stop");
        Assert.IsLessThan(offeredSlices, offered, "the parse stopped long before its last slice");
    }

    [TestMethod]
    public void WriteToBlock_WhenTheNameEntriesRunOut_StopsTheStreamedDumpWithoutIndexing()
    {
        var path = WriteSynthetic(TwoChunkRecordCount);
        using var block = TestBlocks.Create(Path.Combine(TemporaryDirectoryPath, "small.mlix"), ProducerKind.Mft,
            slotCapacity: (uint)TwoChunkRecordCount, namePoolCapacity: 4096, nameEntryCapacity: 8, volumeSerial: 0, rootRow: 5,
            deleteOnClose: true);
        var phases = new List<BlockScanPhase>();

        using var input = MftDumpInput.Open(path);
        using (var stream = input.OpenSliceStream(null, null, TestContext.CancellationToken))
        {
            MftBlockScan.WriteToBlock(block, new BlockStamp(default, TimeProvider.System), stream, new MftScanOptions(),
                new BlockWriteReporting(new DirectProgress<BlockWriteProgress>(
                    sample => phases.Add(sample.Phase)), null), TestContext.CancellationToken);
        }

        var header = block.Header;
        Assert.IsTrue(BlockLayout.RetryGrowsCapacity(in header),
            $"exhausted {header.IsCapacityExhausted}, rows {header.RowCount} of {header.SlotCapacity}, pool {header.NamePoolUsed} of {header.NamePoolCapacity}, entries {header.NameEntryCount} of {header.NameEntryCapacity}");
        Assert.IsEmpty(phases, "a block its producer re-runs is never indexed");
    }

    [TestMethod]
    public void NativeSinkStructs_HaveTheNativeLayout()
    {
        Assert.AreEqual(18, sizeof(MftExtensionDataSize));
        Assert.AreEqual(3 * IntPtr.Size, sizeof(MftParseSink));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<MftExtensionDataSize>(nameof(MftExtensionDataSize.Size)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<MftExtensionDataSize>(nameof(MftExtensionDataSize.BaseSequenceNumber)));
    }

    string WriteSynthetic(ulong recordCount)
    {
        var path = Path.Combine(TemporaryDirectoryPath, Guid.NewGuid().ToString("N") + ".mft");
        MftVolume.GenerateSyntheticMFT(path, recordCount);
        return path;
    }

    static MftVolume OpenFakeVolume(Func<SafeHandle, bool, uint, IntPtr, NativeProgressCallback, IntPtr, IntPtr> parse)
    {
        MFTLibNative.Hooks._parseMftRecordsStreaming = parse;
        MFTLibNative.Hooks._freeMftResult = Marshal.FreeHGlobal;
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        return MftVolume.Open(new VolumeKey('C'));
    }

    static IntPtr CancelledResult()
    {
        var result = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(new MftParseResult
        {
            ErrorMessage = "Parse cancelled",
            EntryStride = MFTLibNative.NativeCompactEntrySize,
            Cancelled = 1
        }, result, false);
        return result;
    }

    // The fields a whole-result record and a packed entry share, with the root's empty name read as a record reads it.
    static string Describe(MftRecord record) => string.Join('|', record.RecordNumber, record.ParentRecordNumber,
        record.Flags, record.FileAttributes, record.Size, record.ModifiedFileTime, record.SequenceNumber,
        record.ParentSequenceNumber, record.FileName);

    static List<string> Decode(MftParsedSlice slice)
    {
        var described = new List<string>(slice.EntryCount);
        fixed (byte* table = slice.Entries)
        {
            for (var index = 0; index < slice.EntryCount; index++)
            {
                var entry = new MftPackedEntry(table + (index * (int)MFTLibNative.NativeCompactEntrySize));
                var name = new string(slice.Strings, (int)entry.StringOffset, entry.StringLength);
                described.Add(string.Join('|', entry.RecordNumber, entry.ParentRecordNumber, entry.Flags,
                    (FileAttributes)entry.FileAttributes, entry.Size, entry.ModifiedFileTime, entry.SequenceNumber,
                    entry.ParentSequenceNumber, name.Length == 0 && entry.RecordNumber == 5 ? "." : name));
            }
        }

        return described;
    }

    // One packed slice held at fixed addresses for a fake native parse to hand its sink.
    sealed class PinnedSlice : IDisposable
    {
        readonly MftParsedSlice _slice;
        GCHandle _entries;
        GCHandle _strings;

        internal PinnedSlice(MftParsedSlice slice)
        {
            _slice = slice;
            _entries = GCHandle.Alloc(slice.Entries, GCHandleType.Pinned);
            _strings = GCHandle.Alloc(slice.Strings, GCHandleType.Pinned);
        }

        internal byte* Entries => (byte*)_entries.AddrOfPinnedObject();

        internal ushort* Strings => (ushort*)_strings.AddrOfPinnedObject();

        public void Dispose()
        {
            _entries.Free();
            _strings.Free();
            _slice.Return();
        }
    }
}

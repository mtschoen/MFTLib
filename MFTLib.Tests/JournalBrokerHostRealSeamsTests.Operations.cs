using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostRealSeamsTests
{
    [TestMethod]
    public async Task ServeAsync_ScanAndCatchUp_UseRealMftVolumeSeams()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 0xABCD, NextUsn = 5000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreeNameRecordsResult();
        MFTLibNative._parseMftRecords = (_, _, _, _) => parsePtr;
        MFTLibNative._freeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.Entries);
            }

            if (parsed.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };

        using var writer = CreateSectionWriter();
        var frames = await ServeDefaultScanAsync(writer);

        var cursor = frames.Single(frame => frame.Kind == BrokerFrameKind.Cursor).Cursor;
        Assert.AreEqual(0xABCDUL, cursor.JournalId);
        Assert.AreEqual(5000L, cursor.NextUsn);
        Assert.AreEqual(RowFlags.InUse, writer.Block.Rows[100].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[101].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[102].Flags);
        Assert.AreEqual("file0.txt", NamePool.ReadRowName(writer.Block, 100).ToString());
        var catchUp = frames.Single(frame => frame.Kind == BrokerFrameKind.JournalBatch);
        Assert.AreEqual(0, catchUp.Entries.Length);
        Assert.AreEqual(5100L, catchUp.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_AdaptsNativeProgressWithoutPathResolution()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 7UL, NextUsn = 100 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreeNameRecordsResult();
        MFTLibNative._parseMftRecordsWithProgress = (_, _, flags, _, callback, context) =>
        {
            Assert.AreEqual(MatchFlags.None, flags);
            callback?.Invoke(MftScanPhase.Parsing, 200, 300, 42.0, context);
            callback?.Invoke(MftScanPhase.Parsing, 300, 300, 45.0, context);
            return parsePtr;
        };
        MFTLibNative._freeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.Entries);
            }

            if (parsed.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };

        using var writer = CreateSectionWriter();
        var frames = await ServeDefaultScanAsync(writer);
        var reports = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress)
            .Select(frame => frame.Progress!.Value).ToArray();
        // Intermediate parsing reports may be coalesced before the pump reads them.
        // The final report must retain native totals, independent of the three written rows.
        Assert.AreEqual(300L, reports[^1].RecordsProcessed);
        Assert.AreEqual(300L, reports[^1].TotalRecords);
        Assert.AreEqual(BrokerScanPhase.Transferring, reports[^1].Phase);
        Assert.IsTrue(writer.Block.Header.IsComplete);
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_UsesRealWatchAndDisposeSeam()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var callCount = 0;
        MFTLibNative._watchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            callCount++;
            return callCount == 1
                ? BuildEmptyWatchResult(journalId, startUsn)
                : BuildSingleEntryWatchResult(journalId, startUsn + 100, "watched.txt", 0x100 /* FileCreate */);
        };
        MFTLibNative._cancelUsnJournalWatch = _ => true;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        // A non-zero JournalId means the host uses this cursor directly instead of
        // calling queryCursor (which would need MFTLibNative._queryUsnJournal mocked
        // too) - keeps this test focused on the watch seam.
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual("watched.txt", frame.Entries[0].FileName);

        await cts.CancelAsync();
        await serveTask; // ServeAsync swallows OperationCanceledException internally
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        // Not a `using var`: the token is captured by the WatchUsnJournalBatch mock
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after ServeAsync (which drives the mock) completes.
        var cts = new CancellationTokenSource();
        Action cancel = cts.Cancel;

        MFTLibNative._watchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            // Simulate cancellation racing the kernel wait: cancel, then return an
            // empty batch. MftVolume.WatchUsnJournalWithCursor treats "empty batch +
            // already cancelled" as a clean `yield break`, distinct from a cancelled
            // Task.Run throwing OperationCanceledException.
            cancel();
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative._cancelUsnJournalWatch = _ => true;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        // ServeAsync's own token is the same source: once the watch ends cleanly,
        // the outer serve loop unwinds too (its blocked read gets cancelled).
        await host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);
        cts.Dispose();
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_IncludesRootDirectoryRow()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 0x1234, NextUsn = 1000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride * 2);
        unsafe
        {
            new Span<byte>((void*)entryBuf, (int)stride * 2).Clear();

            var childName = "Users";
            var stringBuf = Marshal.AllocHGlobal(childName.Length * sizeof(char));
            childName.AsSpan().CopyTo(new Span<char>((void*)stringBuf, childName.Length));

            // Record 5: root directory (empty native name falls back to dot)
            var rootEntry = (byte*)entryBuf;
            Unsafe.WriteUnaligned(rootEntry, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 8, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 16, 0UL);
            Unsafe.WriteUnaligned(rootEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(rootEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(rootEntry + 30, (ushort)0); // zero-length name

            // Record 100: child folder
            var childEntry = (byte*)entryBuf + stride;
            Unsafe.WriteUnaligned(childEntry, 100UL);
            Unsafe.WriteUnaligned(childEntry + 8, 5UL);
            Unsafe.WriteUnaligned(childEntry + 16, 0UL);
            Unsafe.WriteUnaligned(childEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(childEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(childEntry + 30, (ushort)childName.Length);

            var parseResult = new MftParseResult
            {
                TotalRecords = 2,
                UsedRecords = 2,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = (ulong)childName.Length,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(parseResult, parsePtr, false);

            MFTLibNative._parseMftRecords = (_, _, _, _) => parsePtr;
            MFTLibNative._freeMftResult = ptr =>
            {
                var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
                if (parsed.Entries != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parsed.Entries);
                }

                if (parsed.EntryStrings != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parsed.EntryStrings);
                }

                Marshal.FreeHGlobal(ptr);
            };
        }

        using var writer = CreateSectionWriter();
        await ServeDefaultScanAsync(writer);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[5].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[5].ParentRow);
        Assert.AreEqual(".", NamePool.ReadRowName(writer.Block, 5).ToString());
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[100].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[100].ParentRow);
        Assert.AreEqual("Users", NamePool.ReadRowName(writer.Block, 100).ToString());
    }
}

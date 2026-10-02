using System.Buffers;
using MFTLib.Index;

namespace MFTLib;

// Frame write methods for BrokerProtocol, in BrokerFrameKind order. See BrokerProtocol.cs for
// the entry codec and ReadFrame dispatch, and BrokerProtocol.Payload.cs for the primitives.
internal static partial class BrokerProtocol
{
    // Control pipe

    // payload: [requestId u32][drive string][pipeName string]
    public static void WriteOpenChannel(IBufferWriter<byte> writer, uint requestId, string drive, string pipeName)
    {
        new PayloadWriter().UInt32(requestId).String(drive).String(pipeName)
            .WriteTo(writer, BrokerFrameKind.OpenChannel);
    }

    // payload: [requestId u32]
    public static void WriteChannelOpened(IBufferWriter<byte> writer, uint requestId)
    {
        new PayloadWriter().UInt32(requestId).WriteTo(writer, BrokerFrameKind.ChannelOpened);
    }

    // payload: [requestId u32][drive string]
    public static void WriteQueryVolume(IBufferWriter<byte> writer, uint requestId, string drive)
    {
        new PayloadWriter().UInt32(requestId).String(drive).WriteTo(writer, BrokerFrameKind.QueryVolume);
    }

    // payload: [requestId u32][mftRecordCount i64][bytesPerFileRecordSegment u32][mftValidDataLength i64]
    public static void WriteVolumeInfo(IBufferWriter<byte> writer, uint requestId, long mftRecordCount,
        uint bytesPerFileRecordSegment, long mftValidDataLength)
    {
        new PayloadWriter().UInt32(requestId).Int64(mftRecordCount).UInt32(bytesPerFileRecordSegment)
            .Int64(mftValidDataLength).WriteTo(writer, BrokerFrameKind.VolumeInfo);
    }

    // payload: [requestId u32][drive string][maximumSize i64][allocationDelta i64]
    public static void WriteGrowUsnJournal(IBufferWriter<byte> writer, uint requestId, string drive,
        long maximumSize, long allocationDelta)
    {
        new PayloadWriter().UInt32(requestId).String(drive).Int64(maximumSize).Int64(allocationDelta)
            .WriteTo(writer, BrokerFrameKind.GrowUsnJournal);
    }

    // payload: [requestId u32][maximumSize i64][allocationDelta i64]
    public static void WriteUsnJournalSettings(IBufferWriter<byte> writer, uint requestId,
        long maximumSize, long allocationDelta)
    {
        new PayloadWriter().UInt32(requestId).Int64(maximumSize).Int64(allocationDelta)
            .WriteTo(writer, BrokerFrameKind.UsnJournalSettings);
    }

    // Any pipe

    // payload: [requestId u32 (0 on a drive pipe)][message string]
    public static void WriteError(IBufferWriter<byte> writer, uint requestId, string message)
    {
        new PayloadWriter().UInt32(requestId).String(message).WriteTo(writer, BrokerFrameKind.Error);
    }

    public static void WriteHeartbeat(IBufferWriter<byte> writer)
    {
        new PayloadWriter().WriteTo(writer, BrokerFrameKind.Heartbeat);
    }

    // payload: [message string]
    public static void WriteStalled(IBufferWriter<byte> writer, string message)
    {
        new PayloadWriter().String(message).WriteTo(writer, BrokerFrameKind.Stalled);
    }

    // Drive pipe

    // payload: [sectionName string][profile i32][nameCount i32][per name: name string]
    public static void WriteArmAndScan(IBufferWriter<byte> writer, string sectionName, BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null)
    {
        var payload = new PayloadWriter().String(sectionName).Int32((int)profile);
        var names = keepFileNames ?? Array.Empty<string>();
        payload.Int32(names.Count);
        foreach (var name in names)
        {
            payload.String(name);
        }

        payload.WriteTo(writer, BrokerFrameKind.ArmAndScan);
    }

    // payload: [journalId u64][nextUsn i64]
    public static void WriteCursor(IBufferWriter<byte> writer, UsnJournalCursor cursor)
    {
        new PayloadWriter().Cursor(cursor).WriteTo(writer, BrokerFrameKind.Cursor);
    }

    // payload: [phase i32][recordsProcessed i64][bytesProcessed i64][totalRecordsOrMinusOne i64]
    //          [totalBytesOrMinusOne i64][elapsedTicks i64]
    // The drive is the channel's, so the progress sample's drive letter is not written.
    public static void WriteScanProgress(IBufferWriter<byte> writer, BrokerScanProgress progress)
    {
        new PayloadWriter().Int32((int)progress.Phase).Int64(progress.RecordsProcessed)
            .Int64(progress.BytesProcessed).Int64(progress.TotalRecords ?? -1L).Int64(progress.TotalBytes ?? -1L)
            .Int64(progress.Elapsed.Ticks).WriteTo(writer, BrokerFrameKind.ScanProgress);
    }

    // payload: [cause i32][checkpointUsn i64][firstUsn i64][nextUsn i64][allocationDelta i64]
    //          [maximumSize i64][bytesBehind nullable i64][sizeThatWouldHaveRetained nullable i64][message string]
    internal static void WriteCatchUpLost(IBufferWriter<byte> writer, JournalCheckpointLoss loss, string message)
    {
        new PayloadWriter().Int32((int)loss.Cause).Int64(loss.CheckpointUsn).Int64(loss.FirstUsn)
            .Int64(loss.NextUsn).Int64(loss.AllocationDelta).Int64(loss.MaximumSize)
            .NullableInt64(loss.BytesBehind).NullableInt64(loss.SizeThatWouldHaveRetained).String(message)
            .WriteTo(writer, BrokerFrameKind.CatchUpLost);
    }

    // payload: [rowCount i64][namePoolUsedBytes i64][skippedRecordCount i64]
    public static void WriteScanReady(IBufferWriter<byte> writer, long rowCount, long namePoolUsedBytes,
        long skippedRecordCount)
    {
        new PayloadWriter().Int64(rowCount).Int64(namePoolUsedBytes).Int64(skippedRecordCount)
            .WriteTo(writer, BrokerFrameKind.ScanReady);
    }

    // payload: [journalId u64][nextUsn i64][entryCount i32][entries]
    public static void WriteJournalBatch(IBufferWriter<byte> writer, UsnJournalCursor cursor,
        UsnJournalEntry[] entries)
    {
        var payload = new PayloadWriter().Cursor(cursor).Int32(entries.Length);
        foreach (var entry in entries)
        {
            payload.Entry(entry);
        }

        payload.WriteTo(writer, BrokerFrameKind.JournalBatch);
    }

    // payload: [journalId u64][nextUsn i64]
    public static void WriteStartWatch(IBufferWriter<byte> writer, UsnJournalCursor since)
    {
        new PayloadWriter().Cursor(since).WriteTo(writer, BrokerFrameKind.StartWatch);
    }

    public static void WriteCaughtUp(IBufferWriter<byte> writer)
    {
        new PayloadWriter().WriteTo(writer, BrokerFrameKind.CaughtUp);
    }
}

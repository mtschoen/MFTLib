using System.Buffers;

namespace MFTLib;

// Frame write methods for BrokerProtocol, in BrokerFrameKind order. See BrokerProtocol.cs for
// the entry codec and ReadFrame dispatch, and BrokerProtocol.Payload.cs for the primitives.
static partial class BrokerProtocol
{
    // Control pipe

    // payload: [requestId u32][volume UTF-16 character][pipeName string]
    public static void WriteOpenChannel(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).Volume(frame.RequireVolume()).String(frame.PipeName)
            .WriteTo(writer, BrokerFrameKind.OpenChannel);
    }

    // payload: [requestId u32]
    public static void WriteChannelOpened(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).WriteTo(writer, BrokerFrameKind.ChannelOpened);
    }

    // payload: [requestId u32][volume UTF-16 character]
    public static void WriteQueryVolume(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).Volume(frame.RequireVolume()).WriteTo(writer, BrokerFrameKind.QueryVolume);
    }

    // payload: [requestId u32][bytesPerFileRecordSegment u32][mftValidDataLength i64]
    public static void WriteVolumeInfo(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).UInt32(frame.BytesPerFileRecordSegment)
            .Int64(frame.MftValidDataLength).WriteTo(writer, BrokerFrameKind.VolumeInfo);
    }

    // payload: [requestId u32][volume UTF-16 character][maximumSize i64][allocationDelta i64]
    public static void WriteGrowUsnJournal(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).Volume(frame.RequireVolume()).Int64(frame.JournalMaximumSize)
            .Int64(frame.JournalAllocationDelta).WriteTo(writer, BrokerFrameKind.GrowUsnJournal);
    }

    // payload: [requestId u32][maximumSize i64][allocationDelta i64]
    public static void WriteUsnJournalSettings(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).Int64(frame.JournalMaximumSize)
            .Int64(frame.JournalAllocationDelta).WriteTo(writer, BrokerFrameKind.UsnJournalSettings);
    }

    // Any pipe

    // The longest message either text frame carries, in UTF-16 units; a longer one is cut and
    // marked, so an exception text never makes a frame the reader refuses.
    internal const int MaximumMessageUnits = 32768;
    internal const string TruncationMarker = "... (truncated)";

    static string FitMessage(string message)
    {
        return message.Length <= MaximumMessageUnits
            ? message
            : string.Concat(message.AsSpan(0, MaximumMessageUnits - TruncationMarker.Length), TruncationMarker);
    }

    // payload: [requestId u32 (0 on a volume pipe)][message string]
    public static void WriteError(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().UInt32(frame.RequestId).String(FitMessage(frame.RequireMessage()))
            .WriteTo(writer, BrokerFrameKind.Error);
    }

    public static void WriteHeartbeat(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        _ = frame;
        new PayloadWriter().WriteTo(writer, BrokerFrameKind.Heartbeat);
    }

    // payload: [message string]
    public static void WriteStalled(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().String(FitMessage(frame.RequireMessage())).WriteTo(writer, BrokerFrameKind.Stalled);
    }

    // Volume pipe

    // The ArmAndScan frame's length (kind byte plus payload), so a caller can refuse a keep list
    // the frame limit cannot carry before anything is sent.
    public static long ArmAndScanFrameLength(string sectionName, IReadOnlyCollection<string>? retainedFileNames)
    {
        var length = 1L + 4 + (2L * sectionName.Length) + 1 + 5;
        foreach (var name in retainedFileNames ?? [])
        {
            length += 4 + (2L * name.Length);
        }

        return length;
    }

    // payload: [sectionName string][includeFreed byte, 0 or 1][nameCount nullable i32][per name: name string]
    public static void WriteArmAndScan(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        var payload = new PayloadWriter().String(frame.SectionName).Boolean(frame.IncludeFreed)
            .NullableInt32(frame.RetainedFileNames?.Count);
        foreach (var name in frame.RetainedFileNames ?? [])
        {
            payload.String(name);
        }

        payload.WriteTo(writer, BrokerFrameKind.ArmAndScan);
    }

    // payload: [journalIdentifier u64][nextUsn i64]
    public static void WriteCursor(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().Cursor(frame.Cursor).WriteTo(writer, BrokerFrameKind.Cursor);
    }

    // payload: [phase i32][recordsProcessed i64][bytesProcessed i64][totalRecords nullable i64]
    //          [totalBytes nullable i64]
    // The volume is the channel's, so the progress sample's drive letter is not written.
    public static void WriteScanProgress(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        var progress = frame.Progress.GetValueOrDefault();
        new PayloadWriter().Int32((int)progress.Phase).Int64(progress.RecordsProcessed)
            .Int64(progress.BytesProcessed).NullableInt64(progress.TotalRecords)
            .NullableInt64(progress.TotalBytes).WriteTo(writer, BrokerFrameKind.ScanProgress);
    }

    // payload: [cause i32][checkpointUsn i64][firstUsn i64][nextUsn i64][allocationDelta i64]
    //          [maximumSize i64][bytesBehind nullable i64][sizeThatWouldHaveRetained nullable i64]
    internal static void WriteCatchUpLost(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        var loss = frame.CatchUpLoss.GetValueOrDefault();
        new PayloadWriter().Int32((int)loss.Cause).Int64(loss.CheckpointUsn).Int64(loss.FirstUsn)
            .Int64(loss.NextUsn).Int64(loss.AllocationDelta).Int64(loss.MaximumSize)
            .NullableInt64(loss.BytesBehind).NullableInt64(loss.SizeThatWouldHaveRetained)
            .WriteTo(writer, BrokerFrameKind.CatchUpLost);
    }

    // payload: [skippedRecordCount i64]
    public static void WriteScanReady(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().Int64(frame.SkippedRecordCount).WriteTo(writer, BrokerFrameKind.ScanReady);
    }

    // payload: [journalIdentifier u64][nextUsn i64][entryCount i32][entries]
    public static void WriteJournalBatch(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        var payload = new PayloadWriter().Cursor(frame.Cursor).Int32(frame.Entries.Length);
        foreach (var entry in frame.Entries)
        {
            payload.Entry(entry);
        }

        payload.WriteTo(writer, BrokerFrameKind.JournalBatch);
    }

    // payload: [journalIdentifier u64][nextUsn i64]
    public static void WriteStartWatch(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        new PayloadWriter().Cursor(frame.Cursor).WriteTo(writer, BrokerFrameKind.StartWatch);
    }

    public static void WriteCaughtUp(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        _ = frame;
        new PayloadWriter().WriteTo(writer, BrokerFrameKind.CaughtUp);
    }

    // No payload: successful completion of scan catch-up.
    public static void WriteScanCompleted(IBufferWriter<byte> writer, BrokerFrame frame)
    {
        _ = frame;
        new PayloadWriter().WriteTo(writer, BrokerFrameKind.ScanCompleted);
    }
}

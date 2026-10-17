using System.Text;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class UsnJournalSyntheticTests
{
    const int UsnRecordHeaderSize = 60;

    /// <summary>
    ///     A non-zero <c>RecordLength</c> smaller than <c>offsetof(USN_RECORD_V2, FileName)</c> (60 bytes)
    ///     stops the buffer walk cleanly in both <see cref="UsnJournalVolume.ReadUsnJournalBounded" /> and
    ///     <see cref="UsnJournalVolume.WatchUsnJournal" />: well-formed records before the malformed record are
    ///     returned, and the malformed record and any records after it in the buffer are dropped.
    /// </summary>
    [DataTestMethod]
    [DataRow(false, 8u)]
    [DataRow(false, 59u)]
    [DataRow(true, 8u)]
    [DataRow(true, 59u)]
    public async Task ReadAndWatchUsnJournal_RecordLengthBelowHeaderSize_DropsMalformedTailAndReturnsPrecedingEntries(
        bool watch, uint malformedRecordLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2000L);
        UsnReadBuffers.WriteRecord(writer, 100, 5, 1000, 0x00000100u, "before.txt");
        WriteRawRecord(writer, recordLengthField: malformedRecordLength, wireByteCount: 64, recordNumber: 200,
            parentRecordNumber: 5, usn: 1200, reason: 0x00000100u, fileNameOffset: UsnRecordHeaderSize,
            fileNameByteLength: 4, payloadBytes: Encoding.Unicode.GetBytes("ab"));
        UsnReadBuffers.WriteRecord(writer, 300, 5, 1500, 0x00000200u, "after.txt");

        var (entries, cursor) = await ReadOrWatchBatchAsync(stream.ToArray(), watch);

        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual(100UL, entries[0].RecordNumber);
        Assert.AreEqual("before.txt", entries[0].FileName);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    /// <summary>
    ///     A <c>RecordLength</c> larger than the remaining bytes in the buffer stops the walk cleanly in
    ///     both <see cref="UsnJournalVolume.ReadUsnJournalBounded" /> and <see cref="UsnJournalVolume.WatchUsnJournal" />:
    ///     well-formed records before the truncated record are returned, and the truncated record is dropped.
    /// </summary>
    [DataTestMethod]
    [DataRow(false, 68, 72u)]
    [DataRow(false, 64, 72u)]
    [DataRow(false, 60, 64u)]
    [DataRow(true, 68, 72u)]
    [DataRow(true, 64, 72u)]
    [DataRow(true, 60, 64u)]
    public async Task ReadAndWatchUsnJournal_RecordLengthBeyondBufferEnd_DropsTruncatedRecordAndReturnsPrecedingEntries(
        bool watch, int remainingBytes, uint claimedRecordLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2000L);
        UsnReadBuffers.WriteRecord(writer, 100, 5, 1000, 0x00000100u, "before.txt");
        WriteRawRecord(writer, recordLengthField: claimedRecordLength, wireByteCount: remainingBytes,
            recordNumber: 200, parentRecordNumber: 5, usn: 1200, reason: 0x00000100u,
            fileNameOffset: UsnRecordHeaderSize, fileNameByteLength: 0, payloadBytes: []);

        var (entries, cursor) = await ReadOrWatchBatchAsync(stream.ToArray(), watch);

        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual(100UL, entries[0].RecordNumber);
        Assert.AreEqual("before.txt", entries[0].FileName);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    /// <summary>
    ///     When <c>FileNameOffset</c> lies outside <c>[offsetof(USN_RECORD_V2, FileName), RecordLength)</c>
    ///     (either inside the fixed header or at/past <c>RecordLength</c>), the record is retained with an
    ///     empty <see cref="UsnJournalEntry.FileName" /> and the buffer walk continues to subsequent records.
    /// </summary>
    [DataTestMethod]
    [DataRow(false, (ushort)8)]
    [DataRow(false, (ushort)59)]
    [DataRow(false, (ushort)64)]
    [DataRow(false, (ushort)72)]
    [DataRow(true, (ushort)8)]
    [DataRow(true, (ushort)59)]
    [DataRow(true, (ushort)64)]
    [DataRow(true, (ushort)72)]
    public async Task ReadAndWatchUsnJournal_FileNameOffsetOutOfRange_ReturnsEmptyNameAndContinuesWalk(
        bool watch, ushort fileNameOffset)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2000L);
        UsnReadBuffers.WriteRecord(writer, 100, 5, 1000, 0x00000100u, "before.txt");
        WriteRawRecord(writer, recordLengthField: 64, wireByteCount: 64, recordNumber: 200,
            parentRecordNumber: 5, usn: 1200, reason: 0x00000100u, fileNameOffset: fileNameOffset,
            fileNameByteLength: 8, payloadBytes: Encoding.Unicode.GetBytes("ab"));
        UsnReadBuffers.WriteRecord(writer, 300, 5, 1500, 0x00000200u, "after.txt");

        var (entries, cursor) = await ReadOrWatchBatchAsync(stream.ToArray(), watch);

        Assert.AreEqual(3, entries.Length);
        Assert.AreEqual("before.txt", entries[0].FileName);
        Assert.AreEqual(200UL, entries[1].RecordNumber);
        Assert.AreEqual(1200L, entries[1].Usn);
        Assert.AreEqual(string.Empty, entries[1].FileName);
        Assert.AreEqual(300UL, entries[2].RecordNumber);
        Assert.AreEqual("after.txt", entries[2].FileName);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    /// <summary>
    ///     When <c>FileNameOffset + FileNameLength</c> extends past <c>RecordLength</c>, the copied name is
    ///     clamped to the whole <c>WCHAR</c> units within <c>[FileNameOffset, RecordLength)</c> and the buffer
    ///     walk continues cleanly to subsequent records.
    /// </summary>
    [DataTestMethod]
    [DataRow(false, (ushort)60, (ushort)16, "ab")]
    [DataRow(false, (ushort)63, (ushort)8, "")]
    [DataRow(true, (ushort)60, (ushort)16, "ab")]
    [DataRow(true, (ushort)63, (ushort)8, "")]
    public async Task ReadAndWatchUsnJournal_FileNameLengthPastRecordEnd_ClampsNameToRecordLengthAndContinuesWalk(
        bool watch, ushort fileNameOffset, ushort claimedFileNameByteLength, string expectedClampedName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2000L);
        UsnReadBuffers.WriteRecord(writer, 100, 5, 1000, 0x00000100u, "before.txt");
        WriteRawRecord(writer, recordLengthField: 64, wireByteCount: 64, recordNumber: 200,
            parentRecordNumber: 5, usn: 1200, reason: 0x00000100u, fileNameOffset: fileNameOffset,
            fileNameByteLength: claimedFileNameByteLength, payloadBytes: Encoding.Unicode.GetBytes("ab"));
        UsnReadBuffers.WriteRecord(writer, 300, 5, 1500, 0x00000200u, "after.txt");

        var (entries, cursor) = await ReadOrWatchBatchAsync(stream.ToArray(), watch);

        Assert.AreEqual(3, entries.Length);
        Assert.AreEqual("before.txt", entries[0].FileName);
        Assert.AreEqual(200UL, entries[1].RecordNumber);
        Assert.AreEqual(expectedClampedName, entries[1].FileName);
        Assert.AreEqual(300UL, entries[2].RecordNumber);
        Assert.AreEqual("after.txt", entries[2].FileName);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    static async Task<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> ReadOrWatchBatchAsync(byte[] buffer, bool watch)
    {
        UseFakeHandle();
        FileUtilities._getWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        QueueSuccess(buffer);
        using var volume = MftVolume.Open("C");
        if (!watch)
        {
            QueueSuccess(UsnReadBuffers.Build(2000));
            return volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        }

        await using var enumerator = volume.Journal.WatchUsnJournal(Cursor).GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    static void WriteRawRecord(BinaryWriter writer, uint recordLengthField, int wireByteCount, ulong recordNumber,
        ulong parentRecordNumber, long usn, uint reason, ushort fileNameOffset, ushort fileNameByteLength,
        byte[] payloadBytes)
    {
        writer.Write(recordLengthField);
        writer.Write((ushort)2);
        writer.Write((ushort)0);
        writer.Write(recordNumber);
        writer.Write(parentRecordNumber);
        writer.Write(usn);
        writer.Write(0L);
        writer.Write(reason);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(fileNameByteLength);
        writer.Write(fileNameOffset);
        var remainingWireBytes = wireByteCount - UsnRecordHeaderSize;
        var copiedPayloadBytes = Math.Min(payloadBytes.Length, Math.Max(0, remainingWireBytes));
        if (copiedPayloadBytes > 0)
        {
            writer.Write(payloadBytes, 0, copiedPayloadBytes);
        }

        for (var i = copiedPayloadBytes; i < remainingWireBytes; i++)
        {
            writer.Write((byte)0);
        }
    }
}

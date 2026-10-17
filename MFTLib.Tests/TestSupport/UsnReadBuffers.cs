using System.Text;

namespace MFTLib.Tests.TestSupport;

/// <summary>Builds FSCTL_READ_USN_JOURNAL output buffers for the native USN test hooks.</summary>
internal static class UsnReadBuffers
{
    // A FSCTL_READ_USN_JOURNAL output buffer: leading int64 nextUsn, then packed
    // USN_RECORD_V2 records.
    internal static byte[] Build(long nextUsn,
        params (ulong rec, ulong parent, long usn, uint reason, string name)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(nextUsn);
        foreach (var e in entries)
        {
            WriteRecord(w, e.rec, e.parent, e.usn, e.reason, e.name);
        }

        return ms.ToArray();
    }

    internal static void WriteRecord(BinaryWriter w, ulong rec, ulong parent, long usn, uint reason, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        const int fixedLen = 60; // USN_RECORD_V2 header up to FileName[]
        var recordLen = (fixedLen + nameBytes.Length + 7) & ~7; // 8-byte align
        w.Write((uint)recordLen); // RecordLength
        w.Write((ushort)2); // MajorVersion
        w.Write((ushort)0); // MinorVersion
        w.Write(rec); // FileReferenceNumber
        w.Write(parent); // ParentFileReferenceNumber
        w.Write(usn); // Usn
        w.Write(0L); // TimeStamp
        w.Write(reason); // Reason
        w.Write(0u); // SourceInfo
        w.Write(0u); // SecurityId
        w.Write(0u); // FileAttributes
        w.Write((ushort)nameBytes.Length); // FileNameLength (bytes)
        w.Write((ushort)fixedLen); // FileNameOffset
        w.Write(nameBytes);
        for (var i = 0; i < recordLen - (fixedLen + nameBytes.Length); i++)
        {
            w.Write((byte)0);
        }
    }
}

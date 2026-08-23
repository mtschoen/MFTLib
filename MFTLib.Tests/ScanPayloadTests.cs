using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ScanPayloadTests
{
    [TestMethod]
    public void ScanPayload_V2_RoundTrips_RecordsAndStrings()
    {
        var records = new[]
        {
            new ScanRecord(5, 5, 0,
                0, 0x10, true,
                "C:", "C:\\"),
            new ScanRecord(100, 5, 2048,
                638_000_000_000_000_000, 0x20, false,
                "nöte.txt", "C:\\nöte.txt")
        };

        using var stream = new MemoryStream();
        var result = ScanPayload.Write(stream, [records], CancellationToken.None);

        Assert.AreEqual(2L, result.RecordCount);
        Assert.AreEqual(stream.Length, result.ByteLength);

        stream.Position = 0;
        var batches = ScanPayload.ReadBatches(stream, result.ByteLength, 4096, CancellationToken.None).ToList();
        Assert.AreEqual(1, batches.Count);
        CollectionAssert.AreEqual(records, batches[0]);

        var count = ScanPayload.ReadCount(stream.ToArray());
        Assert.AreEqual(2L, count);

        var readAll = ScanPayload.ReadAll(stream.ToArray()).ToArray();
        CollectionAssert.AreEqual(records, readAll);
    }

    [TestMethod]
    public void ScanPayload_V2_Empty_RoundTrips()
    {
        var records = Array.Empty<ScanRecord>();
        using var stream = new MemoryStream();
        var result = ScanPayload.Write(stream, [records], CancellationToken.None);

        Assert.AreEqual(0L, result.RecordCount);
        Assert.AreEqual(24L, result.ByteLength);

        stream.Position = 0;
        var batches = ScanPayload.ReadBatches(stream, result.ByteLength, 4096, CancellationToken.None).ToList();
        Assert.AreEqual(0, batches.Count);

        Assert.AreEqual(0L, ScanPayload.ReadCount(stream.ToArray()));
        CollectionAssert.AreEqual(Array.Empty<ScanRecord>(), ScanPayload.ReadAll(stream.ToArray()).ToArray());
    }

    [TestMethod]
    public void ScanPayload_V2_MultiBatch_Streaming_RoundTrips()
    {
        var batch1 = new[]
        {
            new ScanRecord(1, 0, 100, 1000, 0x20, false, "f1.txt", "C:\\f1.txt"),
            new ScanRecord(2, 0, 200, 2000, 0x20, false, "f2.txt", "C:\\f2.txt")
        };
        var batch2 = new[]
        {
            new ScanRecord(3, 0, 300, 3000, 0x20, false, "f3.txt", "C:\\f3.txt"),
            new ScanRecord(4, 0, 400, 4000, 0x20, false, "f4.txt", "C:\\f4.txt")
        };
        var batch3 = new[]
        {
            new ScanRecord(5, 0, 500, 5000, 0x20, false, "f5.txt", "C:\\f5.txt")
        };

        var allRecords = batch1.Concat(batch2).Concat(batch3).ToArray();

        using var stream = new MemoryStream();
        var result = ScanPayload.Write(stream, [batch1, batch2, batch3], CancellationToken.None);

        Assert.AreEqual(5L, result.RecordCount);

        stream.Position = 0;
        // Read in batches of size 2 -> should produce 3 batches (2, 2, 1)
        var readBatches = ScanPayload.ReadBatches(stream, result.ByteLength, 2, CancellationToken.None).ToList();
        Assert.AreEqual(3, readBatches.Count);
        Assert.AreEqual(2, readBatches[0].Length);
        Assert.AreEqual(2, readBatches[1].Length);
        Assert.AreEqual(1, readBatches[2].Length);

        var concatenated = readBatches.SelectMany(b => b).ToArray();
        CollectionAssert.AreEqual(allRecords, concatenated);
    }

    [TestMethod]
    public void ScanPayload_V2_NonAscii_PreservesUnicode()
    {
        var records = new[]
        {
            new ScanRecord(10, 0, 1024, 12345, 0x20, false, "日本語ファイル.txt", "C:\\フォルダ\\日本語ファイル.txt"),
            new ScanRecord(11, 0, 2048, 67890, 0x20, false, "🚀_rocket_test.log", "C:\\emoji\\🚀_rocket_test.log"),
            new ScanRecord(12, 0, 512, 11111, 0x10, true, "dir_éàü", "C:\\dir_éàü")
        };

        using var stream = new MemoryStream();
        var result = ScanPayload.Write(stream, [records], CancellationToken.None);

        stream.Position = 0;
        var read = ScanPayload.ReadBatches(stream, result.ByteLength, 4096, CancellationToken.None).Single();
        CollectionAssert.AreEqual(records, read);
    }

    [TestMethod]
    public void ScanPayload_V2_ComputeSize_MatchesWrittenBytes()
    {
        var records = new[]
        {
            new ScanRecord(1, 0, 512, 100, 0x20, false, "ab", "X:\\ab"),
            new ScanRecord(2, 1, 1024, 200, 0x20, false, "cd", "X:\\ab\\cd")
        };

        var computedSize = ScanPayload.ComputeSize(records);
        var bytes = new byte[computedSize];
        ScanPayload.Write(bytes, records);

        var read = ScanPayload.ReadAll(bytes).ToArray();
        CollectionAssert.AreEqual(records, read);
        Assert.AreEqual(computedSize, bytes.Length);
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_WrongMagic_Throws()
    {
        var bytes = new byte[32];
        bytes[0] = 0xDE;
        bytes[1] = 0xAD;
        bytes[2] = 0xBE;
        bytes[3] = 0xEF;

        Assert.ThrowsException<InvalidDataException>(() => ScanPayload.ReadCount(bytes));

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(bytes);
            _ = ScanPayload.ReadBatches(stream, 32, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_WrongVersion_Throws()
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4D4C_5343); // Magic
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1); // Version 1 (expected 2)
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 24);

        Assert.ThrowsException<InvalidDataException>(() => ScanPayload.ReadCount(bytes));

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(bytes);
            _ = ScanPayload.ReadBatches(stream, 32, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_NegativeRecordCount_Throws()
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4D4C_5343);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), -1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 24);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(bytes);
            _ = ScanPayload.ReadBatches(stream, 24, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_ByteLengthSmallerThanHeader_Throws()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(new byte[16]);
            _ = ScanPayload.ReadBatches(stream, 16, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_NegativeNameLength_Throws()
    {
        var payload = CreateValidPayloadBytes();
        // Record 0 starts at offset 24. Fixed size 48 bytes. nameByteLength is at 24 + 40 = 64
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(64), -2);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(payload);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_OddNameLength_Throws()
    {
        var payload = CreateValidPayloadBytes();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(64), 3);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(payload);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_NegativePathLength_Throws()
    {
        var payload = CreateValidPayloadBytes();
        // pathByteLength is at 24 + 44 = 68
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(68), -4);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(payload);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_OddPathLength_Throws()
    {
        var payload = CreateValidPayloadBytes();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(68), 5);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(payload);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_LengthExceedsPayload_Throws()
    {
        var payload = CreateValidPayloadBytes();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(64), 10000);

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(payload);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Malformed_TruncatedPayload_Throws()
    {
        var payload = CreateValidPayloadBytes();
        var truncated = payload.Take(payload.Length - 5).ToArray();

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            using var stream = new MemoryStream(truncated);
            _ = ScanPayload.ReadBatches(stream, payload.Length, 4096, CancellationToken.None).ToList();
        });
    }

    [TestMethod]
    public void ScanPayload_V2_Cancellation_BetweenBatches_Throws()
    {
        var batch1 = new[] { new ScanRecord(1, 0, 100, 1000, 0x20, false, "a", "C:\\a") };
        var batch2 = new[] { new ScanRecord(2, 0, 200, 2000, 0x20, false, "b", "C:\\b") };

        using var stream = new MemoryStream();
        var result = ScanPayload.Write(stream, [batch1, batch2], CancellationToken.None);

        stream.Position = 0;
        using var cts = new CancellationTokenSource();
        using var enumerator = ScanPayload.ReadBatches(stream, result.ByteLength, 1, cts.Token).GetEnumerator();

        Assert.IsTrue(enumerator.MoveNext());
        Assert.AreEqual(1, enumerator.Current.Length);

        cts.Cancel();
        try
        {
            enumerator.MoveNext();
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [TestMethod]
    public void ScanPayload_V2_InvalidBatchSize_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
        {
            using var stream = new MemoryStream();
            _ = ScanPayload.ReadBatches(stream, 24, 0, CancellationToken.None).ToList();
        });
    }

    static byte[] CreateValidPayloadBytes()
    {
        var records = new[]
        {
            new ScanRecord(1, 0, 512, 100, 0x20, false, "test.txt", "C:\\test.txt")
        };
        using var stream = new MemoryStream();
        ScanPayload.Write(stream, [records], CancellationToken.None);
        return stream.ToArray();
    }
}

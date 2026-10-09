using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

// Payload primitives shared by every frame's write and read. A payload is built in its own
// buffer so its length is known before the length prefix is written.
internal static partial class BrokerProtocol
{
    sealed class PayloadWriter
    {
        readonly ArrayBufferWriter<byte> _payload = new();

        public PayloadWriter UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_payload.GetSpan(4), value);
            _payload.Advance(4);
            return this;
        }

        public PayloadWriter Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_payload.GetSpan(4), value);
            _payload.Advance(4);
            return this;
        }

        public PayloadWriter UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_payload.GetSpan(8), value);
            _payload.Advance(8);
            return this;
        }

        public PayloadWriter Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_payload.GetSpan(8), value);
            _payload.Advance(8);
            return this;
        }

        // [present byte][value int64], so every long value, negative ones included, round-trips.
        public PayloadWriter NullableInt64(long? value)
        {
            _payload.GetSpan(1)[0] = value.HasValue ? (byte)1 : (byte)0;
            _payload.Advance(1);
            return Int64(value ?? 0);
        }

        // [byteLength int32][UTF-16 bytes]
        public PayloadWriter String(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);
            Int32(bytes.Length);
            _payload.Write(bytes);
            return this;
        }

        public PayloadWriter Cursor(UsnJournalCursor cursor)
        {
            return UInt64(cursor.JournalIdentifier).Int64(cursor.NextUsn);
        }

        public void Entry(UsnJournalEntry entry)
        {
            WriteEntry(_payload, entry);
        }

        public void WriteTo(IBufferWriter<byte> writer, BrokerFrameKind kind)
        {
            var totalLength = 1 + _payload.WrittenCount;
            if (totalLength > BrokerFrameStream.MaximumFrameLength)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Broker {kind} frame is {totalLength} bytes, over the {BrokerFrameStream.MaximumFrameLength}-byte frame limit."));
            }

            var span = writer.GetSpan(4 + totalLength);
            BinaryPrimitives.WriteInt32LittleEndian(span, totalLength);
            span[4] = (byte)kind;
            _payload.WrittenSpan.CopyTo(span[5..]);
            writer.Advance(4 + totalLength);
        }
    }

    // Every read checks the bytes it needs against what the frame holds, so a short or garbled
    // payload fails as InvalidDataException, the one failure a frame reader reports.
    ref struct PayloadReader(ReadOnlySpan<byte> payload)
    {
        // RecordNumber, ParentRecordNumber, SequenceNumber, Usn, Timestamp, Reason, FileAttributes
        // and the name length: the fixed part of one journal entry.
        const int EntryFixedBytes = 8 + 8 + 2 + 8 + 8 + 4 + 4 + 4;

        readonly ReadOnlySpan<byte> _payload = payload;
        int _offset;

        public uint UInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        }

        public int Int32()
        {
            return BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        }

        public ulong UInt64()
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        }

        public long Int64()
        {
            return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        }

        public long? NullableInt64()
        {
            var present = Take(1)[0] != 0;
            var value = Int64();
            return present ? value : null;
        }

        public string String()
        {
            return Encoding.Unicode.GetString(Take(Length()));
        }

        // A count of items that each take at least minimumItemBytes; a count the remaining bytes
        // cannot hold is malformed, so a garbled count never sizes an allocation.
        public int Count(int minimumItemBytes)
        {
            var count = Int32();
            if (count < 0 || count > Remaining / minimumItemBytes)
            {
                throw new InvalidDataException($"Broker frame declares {count} items in {Remaining} bytes");
            }

            return count;
        }

        public int EntryCount() => Count(EntryFixedBytes);

        public UsnJournalEntry Entry()
        {
            if (Remaining < EntryFixedBytes)
            {
                throw Truncated(EntryFixedBytes);
            }

            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(_payload[(_offset + EntryFixedBytes - 4)..]);
            if (nameLength < 0 || nameLength > Remaining - EntryFixedBytes)
            {
                throw new InvalidDataException($"Broker journal entry declares a {nameLength}-byte name");
            }

            var entry = ReadEntry(_payload[_offset..], out var consumed);
            _offset += consumed;
            return entry;
        }

        readonly int Remaining => _payload.Length - _offset;

        int Length()
        {
            var length = Int32();
            if (length < 0 || length > Remaining)
            {
                throw new InvalidDataException($"Broker frame declares a {length}-byte field in {Remaining} bytes");
            }

            return length;
        }

        ReadOnlySpan<byte> Take(int bytes)
        {
            if (bytes > Remaining)
            {
                throw Truncated(bytes);
            }

            var taken = _payload.Slice(_offset, bytes);
            _offset += bytes;
            return taken;
        }

        readonly InvalidDataException Truncated(int bytes)
        {
            return new InvalidDataException($"Broker frame payload is truncated: {bytes} bytes needed, {Remaining} left");
        }
    }
}

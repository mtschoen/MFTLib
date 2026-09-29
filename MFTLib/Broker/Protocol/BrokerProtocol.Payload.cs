using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

// Payload primitives shared by every frame's write and read. A payload is built in its own
// buffer so its length is known before the length prefix is written.
public static partial class BrokerProtocol
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
            return UInt64(cursor.JournalId).Int64(cursor.NextUsn);
        }

        public void Entry(UsnJournalEntry entry)
        {
            WriteEntry(_payload, entry);
        }

        public void WriteTo(IBufferWriter<byte> writer, BrokerFrameKind kind)
        {
            var totalLength = 1 + _payload.WrittenCount;
            var span = writer.GetSpan(4 + totalLength);
            BinaryPrimitives.WriteInt32LittleEndian(span, totalLength);
            span[4] = (byte)kind;
            _payload.WrittenSpan.CopyTo(span[5..]);
            writer.Advance(4 + totalLength);
        }
    }

    ref struct PayloadReader(ReadOnlySpan<byte> payload)
    {
        readonly ReadOnlySpan<byte> _payload = payload;
        int _offset;

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_offset..]);
            _offset += 4;
            return value;
        }

        public int Int32()
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(_payload[_offset..]);
            _offset += 4;
            return value;
        }

        public ulong UInt64()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_payload[_offset..]);
            _offset += 8;
            return value;
        }

        public long Int64()
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(_payload[_offset..]);
            _offset += 8;
            return value;
        }

        public long? NullableInt64()
        {
            var present = _payload[_offset] != 0;
            _offset += 1;
            var value = Int64();
            return present ? value : null;
        }

        public string String()
        {
            var length = Int32();
            var value = Encoding.Unicode.GetString(_payload.Slice(_offset, length));
            _offset += length;
            return value;
        }

        public UsnJournalEntry Entry()
        {
            var entry = ReadEntry(_payload[_offset..], out var consumed);
            _offset += consumed;
            return entry;
        }
    }
}

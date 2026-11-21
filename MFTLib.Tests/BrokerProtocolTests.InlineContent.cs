using System.Buffers;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The inline content frames of a volume pipe: the client's one ReadInlineContent request and the host's
// InlineContentBatch and InlineContentCompleted replies.
public partial class BrokerProtocolTests
{
    [TestMethod]
    public void ReadInlineContent_RoundTripsEveryRequestField()
    {
        InlineContentRequest[] requests =
        [
            new(0, 0, 0), new(7, 5, 3), new(int.MaxValue, long.MaxValue, ushort.MaxValue)
        ];

        var frame = RoundTrip(BrokerFrame.ReadInlineContent(requests, 0x1111111122222222), BrokerProtocol.WriteReadInlineContent);

        Assert.AreEqual(BrokerFrameKind.ReadInlineContent, frame.Kind);
        Assert.AreEqual(0x1111111122222222UL, frame.ExpectedVolumeSerial);
        Assert.AreSequenceEqual(requests, frame.InlineContentRequests.ToArray());
    }

    [TestMethod]
    public void ReadInlineContent_EqualRecordNumbersAreAscending()
    {
        InlineContentRequest[] requests = [new(1, 9, 2), new(2, 9, 2)];

        var frame = RoundTrip(BrokerFrame.ReadInlineContent(requests, 0xA1B2C3D4), BrokerProtocol.WriteReadInlineContent);

        Assert.AreEqual(2, frame.InlineContentRequests.Length);
    }

    [TestMethod]
    public void ReadInlineContent_GoldenBytesPinTheLayout()
    {
        AssertWireBytes(
            writer => BrokerProtocol.WriteReadInlineContent(writer,
                BrokerFrame.ReadInlineContent(new[] { new InlineContentRequest(3, 0x0102, 0x0A0B) }, 0x11223344A1B2C3D4)),
            [
                27, 0, 0, 0, // length: kind, volume serial, count and one 14-byte request
                19, // ReadInlineContent
                0xD4, 0xC3, 0xB2, 0xA1, 0x44, 0x33, 0x22, 0x11, // expected volume serial
                1, 0, 0, 0, // count
                3, 0, 0, 0, // position
                0x02, 0x01, 0, 0, 0, 0, 0, 0, // record number
                0x0B, 0x0A // sequence number
            ]);
    }

    [TestMethod]
    public void ReadInlineContent_TheMostRequestsPerFrameFitTheLimitExactlyAndOneMoreDoesNot()
    {
        var requests = new InlineContentRequest[BrokerProtocol.MaximumInlineContentRequestsPerFrame];
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteReadInlineContent(buffer, BrokerFrame.ReadInlineContent(requests, 0xA1B2C3D4));

        Assert.IsLessThanOrEqualTo(BrokerFrameStream.MaximumFrameLength + 4, buffer.WrittenCount);
        Assert.IsGreaterThan(BrokerFrameStream.MaximumFrameLength + 4 - BrokerProtocol.InlineContentRequestBytes,
            buffer.WrittenCount, "one more request would not fit");
        Assert.ThrowsExactly<InvalidOperationException>(() => BrokerProtocol.WriteReadInlineContent(
            new ArrayBufferWriter<byte>(),
            BrokerFrame.ReadInlineContent(new InlineContentRequest[requests.Length + 1], 0xA1B2C3D4)));
    }

    [TestMethod]
    public void ReadFrame_ReadInlineContent_RecordsOutOfOrderOrNegative_ThrowInvalidDataException()
    {
        foreach (var requests in new[]
                 {
                     new InlineContentRequest[] { new(0, 9, 1), new(1, 8, 1) },
                     [new(0, -1, 1)]
                 })
        {
            var buffer = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteReadInlineContent(buffer, BrokerFrame.ReadInlineContent(requests, 0xA1B2C3D4));

            var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));

            Assert.Contains("out of ascending order", exception.Message);
        }
    }

    [TestMethod]
    public void ReadFrame_ReadInlineContent_CountBeyondThePayload_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteReadInlineContent(buffer, BrokerFrame.ReadInlineContent(new[] { new InlineContentRequest(0, 1, 1) }, 0xA1B2C3D4));
        var bytes = buffer.WrittenSpan.ToArray();

        bytes[13] = 2;

        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }

    [TestMethod]
    public void InlineContentBatch_RoundTripsEveryOutcomeAndItsContent()
    {
        byte[] content = [1, 2, 3, 4, 5];
        BrokerInlineContentAnswer[] answers =
        [
            new(4, InlineContentOutcome.Returned, 0, 3),
            new(9, InlineContentOutcome.NotInline, 3, 0),
            new(2, InlineContentOutcome.Returned, 3, 2),
            new(8, InlineContentOutcome.Returned, 5, 0),
            new(1, InlineContentOutcome.Superseded, 5, 0),
            new(6, InlineContentOutcome.Unreadable, 5, 0)
        ];

        var frame = RoundTrip(BrokerFrame.InlineContentBatch(answers, content), BrokerProtocol.WriteInlineContentBatch);

        Assert.AreEqual(BrokerFrameKind.InlineContentBatch, frame.Kind);
        Assert.AreSequenceEqual(answers, frame.InlineContentAnswers);
        Assert.AreSequenceEqual(content, frame.InlineContent.ToArray());
    }

    [TestMethod]
    public void InlineContentBatch_GoldenBytesPinTheLayout()
    {
        AssertWireBytes(
            writer => BrokerProtocol.WriteInlineContentBatch(writer, BrokerFrame.InlineContentBatch(
                [new BrokerInlineContentAnswer(3, InlineContentOutcome.Returned, 0, 2)], new byte[] { 0xAA, 0xBB })),
            [
                16, 0, 0, 0, // length: kind, count and one answer of 9 fixed bytes and 2 content bytes
                20, // InlineContentBatch
                1, 0, 0, 0, // count
                3, 0, 0, 0, // position
                0, // Returned
                2, 0, 0, 0, // content length
                0xAA, 0xBB
            ]);
    }

    [TestMethod]
    public void ReadFrame_InlineContentBatch_UnknownOutcome_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteInlineContentBatch(buffer, BrokerFrame.InlineContentBatch(
            [new BrokerInlineContentAnswer(0, InlineContentOutcome.NotInline, 0, 0)], ReadOnlyMemory<byte>.Empty));
        var bytes = buffer.WrittenSpan.ToArray();

        // length prefix 4 + kind 1 + count 4 + position 4 = the outcome's offset.
        bytes[13] = 9;
        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));

        Assert.Contains("9", exception.Message);
    }

    [TestMethod]
    public void ReadFrame_InlineContentBatch_ContentOnAnAnswerThatIsNotReturned_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteInlineContentBatch(buffer, BrokerFrame.InlineContentBatch(
            [new BrokerInlineContentAnswer(0, InlineContentOutcome.NotInline, 0, 2)], new byte[] { 1, 2 }));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));

        Assert.Contains("NotInline", exception.Message);
    }

    [TestMethod]
    public void ReadFrame_InlineContentBatch_ContentLengthBeyondThePayload_ThrowsInvalidDataException()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteInlineContentBatch(buffer, BrokerFrame.InlineContentBatch(
            [new BrokerInlineContentAnswer(0, InlineContentOutcome.Returned, 0, 2)], new byte[] { 1, 2 }));
        var bytes = buffer.WrittenSpan.ToArray();

        // length prefix 4 + kind 1 + count 4 + position 4 + outcome 1 = the content length's offset.
        bytes[14] = 200;

        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }

    [TestMethod]
    public void InlineContentCompleted_RoundTripsWithoutAPayload()
    {
        var frame = RoundTrip(BrokerFrame.InlineContentCompleted(), BrokerProtocol.WriteInlineContentCompleted);

        Assert.AreEqual(BrokerFrameKind.InlineContentCompleted, frame.Kind);
        AssertWireBytes(writer => BrokerProtocol.WriteInlineContentCompleted(writer, BrokerFrame.InlineContentCompleted()),
            [1, 0, 0, 0, 21]);
    }

    [TestMethod]
    public void ReadFrame_InlineContentCompleted_TrailingByte_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame([2, 0, 0, 0, 21, 0], out _));
    }
}

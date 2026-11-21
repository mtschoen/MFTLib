using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>One answer a read produced: what was found and the bytes copied out of the lent span.</summary>
readonly record struct InlineAnswer(InlineContentOutcome Outcome, byte[] Content);

/// <summary>Reads wanted records of a dump file through the native inline-content reader, as a test would assert on them.</summary>
static class InlineContentDump
{
    /// <summary>Reads the wanted (record, sequence) pairs; answer <c>i</c> belongs to <paramref name="wanted" /> <c>i</c>.</summary>
    internal static InlineAnswer[] Read(string dumpPath, params (long Record, ushort Sequence)[] wanted) =>
        Read(dumpPath, CancellationToken.None, wanted).Answers;

    internal static (InlineAnswer[] Answers, long ReadCalls, long BytesRead) Read(string dumpPath,
        CancellationToken cancellationToken, params (long Record, ushort Sequence)[] wanted)
    {
        var requests = wanted.Select((pair, position) => new InlineContentRequest(position, pair.Record, pair.Sequence))
            .OrderBy(request => request.RecordNumber).ThenBy(request => request.Position).ToArray();
        var answers = new InlineAnswer[wanted.Length];
        var delivered = new bool[wanted.Length];
        using var input = MftDumpInput.Open(dumpPath);
        using var reader = input.OpenInlineContentReader();
        reader.Read(requests, (position, outcome, content) =>
        {
            Assert.IsFalse(delivered[position], $"position {position} was answered twice");
            delivered[position] = true;
            answers[position] = new InlineAnswer(outcome, content.ToArray());
        }, cancellationToken);
        Assert.IsTrue(delivered.All(flag => flag), "every position is answered exactly once");
        return (answers, reader.NativeReadCalls, reader.NativeBytesRead);
    }

    /// <summary>The bytes of a resident value that exactly fills the record, given the name of the file.</summary>
    internal static int LargestContent(int recordSize, string name)
    {
        var fileNameLength = (0x18 + 66 + (name.Length * 2) + 7) & ~7;
        var firstAttribute = (MftDumpFixture.UpdateSequenceArrayOffset + (((recordSize / 512) + 1) * 2) + 7) & ~7;
        var dataStart = firstAttribute + 0x18 + 48 + fileNameLength;
        return (recordSize - dataStart - 0x18 - 8) & ~7;
    }

    /// <summary>Content whose every byte depends on <paramref name="seed" /> and its position, so a misplaced copy shows.</summary>
    internal static byte[] Pattern(int seed, int length)
    {
        var content = new byte[length];
        for (var index = 0; index < length; index++)
        {
            content[index] = (byte)((seed * 31) + (index * 7) + (index >> 8));
        }

        return content;
    }
}

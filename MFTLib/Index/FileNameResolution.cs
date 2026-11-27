namespace MFTLib.Index;

/// <summary>What the broker's re-read of one file's MFT record found when a journal batch reported a hard-link change.</summary>
enum FileNameLookupOutcome : byte
{
    /// <summary>The record is the file's incarnation; the name and parent are the ones a scan gives it.</summary>
    Resolved = 0,

    /// <summary>The record is free, past the end of the MFT, or carries another sequence number: the file is gone.</summary>
    Superseded = 1,

    /// <summary>The record or its lookup could not be read or judged, so the file's surviving name is unknown.</summary>
    Unreadable = 2
}

/// <summary>
///     The name and parent a fresh scan gives one file, read again from its MFT record after the journal reported a
///     hard-link change for it, because the journal record alone does not say which of the file's links survives
///     (issue 1008). The broker looks each such file up once per batch and ships the answers with the batch;
///     <see cref="JournalMutator" /> applies a resolved answer as a rename and detaches the row when the answer is
///     unreadable.
/// </summary>
/// <param name="RecordNumber">The file's MFT record number.</param>
/// <param name="SequenceNumber">The file's sequence number as the journal record carries it.</param>
/// <param name="Outcome">What the lookup found.</param>
/// <param name="ParentRecordNumber">The parent's record number when <paramref name="Outcome" /> is Resolved; otherwise zero.</param>
/// <param name="Name">The name when <paramref name="Outcome" /> is Resolved; otherwise empty.</param>
readonly record struct FileNameResolution(
    ulong RecordNumber,
    ushort SequenceNumber,
    FileNameLookupOutcome Outcome,
    ulong ParentRecordNumber,
    string Name)
{
    // The parent file reference the native answer leads with: 48 bits of record number, then 16 of sequence number.
    const int ParentReferenceBytes = 8;
    const ulong RecordNumberMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>An answer that says the file's surviving name is unknown.</summary>
    public static FileNameResolution Unreadable(ulong recordNumber, ushort sequenceNumber) =>
        new(recordNumber, sequenceNumber, FileNameLookupOutcome.Unreadable, 0, "");

    /// <summary>
    ///     Decodes one native file name answer: a returned answer is the 8-byte parent file reference followed by the
    ///     name's UTF-16 units. A returned answer too short for the reference, with an odd byte count or an empty name
    ///     is unreadable, as is every outcome other than returned and superseded.
    /// </summary>
    public static FileNameResolution FromLookup(ulong recordNumber, ushort sequenceNumber, InlineContentOutcome outcome,
        ReadOnlySpan<byte> content)
    {
        if (outcome == InlineContentOutcome.Superseded)
        {
            return new FileNameResolution(recordNumber, sequenceNumber, FileNameLookupOutcome.Superseded, 0, "");
        }

        if (outcome != InlineContentOutcome.Returned || content.Length <= ParentReferenceBytes ||
            (content.Length - ParentReferenceBytes) % sizeof(char) != 0)
        {
            return Unreadable(recordNumber, sequenceNumber);
        }

        var parent = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content) & RecordNumberMask;
        var name = System.Text.Encoding.Unicode.GetString(content[ParentReferenceBytes..]);
        return new FileNameResolution(recordNumber, sequenceNumber, FileNameLookupOutcome.Resolved, parent, name);
    }
}

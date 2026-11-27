namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A directory whose name is in an extension record that refers back to the base. When present, the base's
///     <c>$ATTRIBUTE_LIST</c> has a placeholder payload with no list entries. The tree is the standard one plus
///     record <see cref="BaseRecordNumber" /> <c>Spilled</c> under the root and <see cref="ChildRecordNumber" />
///     <c>inside.txt</c> under it.
/// </summary>
static class ExtensionNameFixture
{
    internal const int BaseRecordNumber = 12;
    internal const int ChildRecordNumber = 13;
    internal const string DirectoryName = "Spilled";
    internal const ushort BaseSequenceNumber = 3;

    internal sealed record Options
    {
        /// <summary>Where the extension record that carries the name sits.</summary>
        internal int ExtensionRecordNumber { get; init; } = 24;

        /// <summary>The base keeps only the DOS 8.3 name, as a base keeps the short one of a pair it split.</summary>
        internal bool BaseKeepsDosName { get; init; }

        internal bool BaseHasAttributeList { get; init; } = true;

        /// <summary>The base sequence the extension's base reference carries.</summary>
        internal ushort ReferencedSequenceNumber { get; init; } = BaseSequenceNumber;

        internal bool ExtensionInUse { get; init; } = true;

        /// <summary>The namespace of the extension's name; 2 is DOS only.</summary>
        internal byte ExtensionNamespace { get; init; } = 1;

        /// <summary>A second extension, at this record, carries the name <c>Second</c>.</summary>
        internal int? SecondExtensionRecordNumber { get; init; }

        /// <summary>The base is a file with no data attribute of its own instead of a directory.</summary>
        internal bool File { get; init; }

        /// <summary>
        ///     The base's <c>$ATTRIBUTE_LIST</c> holds real entries, as NTFS writes them: a <c>$FILE_NAME</c> entry for
        ///     the base itself and one for each extension record, instead of the placeholder payload.
        /// </summary>
        internal bool ListsExtensions { get; init; }
    }

    /// <summary>The sequence number every extension record of the fixture carries.</summary>
    internal const ushort ExtensionSequenceNumber = 9;

    internal static byte[] Build(Options? options = null)
    {
        options ??= new Options();
        var records = MftDumpFixture.StandardRecords();
        records[BaseRecordNumber] = new DumpRecord(options.BaseKeepsDosName ? "SPILLE~1" : DirectoryName, 5)
        {
            IsDirectory = !options.File,
            Size = null,
            SequenceNumber = BaseSequenceNumber,
            AttributeList = options.BaseHasAttributeList,
            AttributeListEntries = options.ListsExtensions ? ListEntries(options) : null,
            OmitFileName = !options.BaseKeepsDosName,
            FileNameNamespace = 2
        };
        records[ChildRecordNumber] = new DumpRecord("inside.txt", BaseRecordNumber) { Size = 5 };
        records[options.ExtensionRecordNumber] = Extension(DirectoryName, options, options.ExtensionNamespace);
        if (options.SecondExtensionRecordNumber is { } second)
        {
            records[second] = Extension("Second", options, 1);
        }

        return MftDumpFixture.Build(1024, 32, records);
    }

    static List<(uint TypeCode, ulong SegmentReference)> ListEntries(Options options)
    {
        const uint fileNameType = 0x30;
        static ulong Reference(int record, ushort sequence) => ((ulong)sequence << 48) | (uint)record;
        List<(uint TypeCode, ulong SegmentReference)> entries =
        [
            (0x10, Reference(BaseRecordNumber, BaseSequenceNumber)),
            (fileNameType, Reference(BaseRecordNumber, BaseSequenceNumber)),
            (fileNameType, Reference(options.ExtensionRecordNumber, ExtensionSequenceNumber))
        ];
        if (options.SecondExtensionRecordNumber is { } second)
        {
            entries.Add((fileNameType, Reference(second, ExtensionSequenceNumber)));
        }

        return entries;
    }

    static DumpRecord Extension(string name, Options options, byte nameSpace) =>
        new(name, 5)
        {
            InUse = options.ExtensionInUse,
            Size = null,
            SequenceNumber = ExtensionSequenceNumber,
            OmitStandardInformation = true,
            FileNameNamespace = nameSpace,
            BaseReference = ((ulong)options.ReferencedSequenceNumber << 48) | BaseRecordNumber
        };
}

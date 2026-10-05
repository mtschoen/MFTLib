using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>Builds materialized <see cref="MftRecord" /> values for tests, without reading a volume.</summary>
public static class SyntheticMftRecord
{
    const ushort InUseFlag = 1;
    const ushort DirectoryFlag = 2;
    const ushort SizeUnknownFlag = 0x8000;

    /// <summary>Creates one materialized MFT record.</summary>
    /// <param name="options">The columns of the record.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public static MftRecord Create(SyntheticMftRecordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var flags = (ushort)((options.InUse ? InUseFlag : 0)
                             | (options.IsDirectory ? DirectoryFlag : 0)
                             | (options.SizeKnown ? 0 : SizeUnknownFlag));
        return MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = options.RecordNumber,
            ParentRecordNumber = options.ParentRecordNumber,
            Flags = flags,
            FileName = options.FileName,
            FullPath = options.FullPath,
            FileAttributes = options.FileAttributes
                             ?? (options.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal),
            Size = options.Size,
            ModifiedFileTime = (options.ModifiedUtc ?? DateTime.UnixEpoch).ToFileTimeUtc(),
            SequenceNumber = options.SequenceNumber
        });
    }
}

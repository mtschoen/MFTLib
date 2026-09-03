namespace MFTLib.Index;

/// <summary>
///     Which producer builds a drive's block. <see cref="Auto" /> picks the MFT producer for an
///     NTFS volume when a broker is available and the enumeration producer otherwise, so no
///     drive is ever unindexed because of its substrate.
/// </summary>
public enum ProducerPolicy
{
    Auto,
    MftOnly,
    EnumerationOnly
}

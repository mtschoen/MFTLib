using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Per-row state packed into the 16-bit row flags field.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Flags suffix is conventional here; renaming breaks consumers.")]
public enum RowFlags : ushort
{
    None = 0,

    /// <summary>The slot holds a live record. Slots that were never filled read as zero.</summary>
    InUse = 1,

    /// <summary>The record is a directory. Directories carry a size of zero.</summary>
    Directory = 2,

    /// <summary>The record was deleted. The name is retained so a change feed can still name it.</summary>
    Tombstone = 4,

    /// <summary>The producer could not determine the size; the size column holds zero.</summary>
    SizeUnknown = 8,

    /// <summary>Enumeration hit an access denial under this directory and skipped its subtree.</summary>
    SubtreeSkipped = 16
}

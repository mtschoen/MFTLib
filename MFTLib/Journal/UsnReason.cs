namespace MFTLib;

/// <summary>Flags reported by Windows in a <c>USN_REASON_*</c> change-journal record.</summary>
[Flags]
public enum UsnReason : uint
{
    /// <summary>No change reason is reported.</summary>
    None = 0,
    /// <summary>Unnamed data was overwritten.</summary>
    DataOverwrite = 0x00000001,
    /// <summary>Unnamed data was extended.</summary>
    DataExtend = 0x00000002,
    /// <summary>Unnamed data was truncated.</summary>
    DataTruncation = 0x00000004,
    /// <summary>Records an in-place write to an existing named alternate stream without increasing its length.</summary>
    NamedDataOverwrite = 0x00000010,
    /// <summary>Records an append or other length-increasing write to a named alternate stream.</summary>
    NamedDataExtend = 0x00000020,
    /// <summary>Records removal of bytes from a named alternate stream by reducing its length.</summary>
    NamedDataTruncation = 0x00000040,
    /// <summary>Records creation of the initial file or directory object.</summary>
    FileCreate = 0x00000100,
    /// <summary>Records deletion of a file or directory from the namespace.</summary>
    FileDelete = 0x00000200,
    /// <summary>Records a native extended-attribute update, which ordinary Win32 applications usually cannot access.</summary>
    EaChange = 0x00000400,
    /// <summary>Security descriptor data changed.</summary>
    SecurityChange = 0x00000800,
    /// <summary>Identifies the rename record whose filename is the pre-rename name for pairing with the new-name record.</summary>
    RenameOldName = 0x00001000,
    /// <summary>Identifies the rename record whose filename is the post-rename name for pairing with the old-name record.</summary>
    RenameNewName = 0x00002000,
    /// <summary>A change affected the item's indexable state.</summary>
    IndexableChange = 0x00004000,
    /// <summary>Basic file information, such as attributes or timestamps, changed.</summary>
    BasicInfoChange = 0x00008000,
    /// <summary>A hard-link relationship changed.</summary>
    HardLinkChange = 0x00010000,
    /// <summary>NTFS compression state changed.</summary>
    CompressionChange = 0x00020000,
    /// <summary>NTFS encryption state changed.</summary>
    EncryptionChange = 0x00040000,
    /// <summary>Records assignment, replacement, or removal of the NTFS object identifier.</summary>
    ObjectIdChange = 0x00080000,
    /// <summary>Records addition, removal, or target change of a reparse point.</summary>
    ReparsePointChange = 0x00100000,
    /// <summary>A named data stream was added, removed, or changed.</summary>
    StreamChange = 0x00200000,
    /// <summary>Records a change made within a Transactional NTFS transaction.</summary>
    TransactedChange = 0x00400000,
    /// <summary>File integrity state changed.</summary>
    IntegrityChange = 0x00800000,
    /// <summary>The file-handle operation closed.</summary>
    Close = 0x80000000
}

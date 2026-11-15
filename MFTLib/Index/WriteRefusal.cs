namespace MFTLib.Index;

/// <summary>
///     Why a <see cref="BlockWriter" /> member declined a write. A refusal writes nothing: no row, name entry, pool
///     byte or posting changes. The writer never sets the capacity-exhausted flag itself; the caller decides whether a
///     refusal means flagging the block, growing it, or skipping the record. <see cref="Slots" /> is a row index at or
///     past the slot capacity; <see cref="NameEntries" /> a new name with every entry used (or a name too long to
///     store); <see cref="NamePool" /> a new name whose bytes do not fit the pool; <see cref="Delta" /> a posting
///     delta with no room for the association the write would append.
/// </summary>
enum WriteRefusal
{
    None,
    Slots,
    NameEntries,
    NamePool,
    Delta
}

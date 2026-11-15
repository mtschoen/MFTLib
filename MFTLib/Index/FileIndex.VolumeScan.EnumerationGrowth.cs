namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Owns the enumeration block while a walk fills it. <see cref="Grow" /> replaces an exhausted
        ///     block with one whose exhausted regions have grown, holding the same rows; disposal releases
        ///     whatever block is current unless <see cref="Release" /> handed it to the caller.
        /// </summary>
        sealed class EnumerationBlockGrowth(VolumeScan runtime, string blockPath, bool deleteOnClose,
            BlockCapacities capacities) : IDisposable
        {
            BlockCapacities _capacities = capacities;
            BlockFile? _block = runtime.CreateEnumerationBlock(blockPath, deleteOnClose, capacities);

            public BlockFile Block => _block ?? throw new ObjectDisposedException(nameof(EnumerationBlockGrowth));

            /// <summary>
            ///     Copies the exhausted block's regions straight into a replacement created at a sibling path, with
            ///     only the exhausted regions doubled, disposes and deletes the old block, and renames the replacement
            ///     into place. The walk is unfinished, so no posting base is built. The copy is verbatim, never
            ///     compacting: a walk interns each name for the row it writes and never renames one, so every stored
            ///     name is referenced. Null when every exhausted region is at its clamp.
            /// </summary>
            public BlockWriter? Grow(BlockWriter exhausted)
            {
                var next = _capacities.AtLeast(BlockLayout.GrowthMinimum(in exhausted.Block.Header));
                if (next == _capacities)
                {
                    return null;
                }

                var replacementPath = $"{blockPath}.growing-{Guid.NewGuid():N}";
                var replacement = runtime.CreateEnumerationBlock(replacementPath, deleteOnClose, next);
                var rowCount = exhausted.Block.RowCount;
                try
                {
                    // The refusal that triggered this growth is remedied by it, so the replacement starts unflagged.
                    using (exhausted.Block.TakeAccess())
                    {
                        exhausted.Block.Header.Flags &= ~BlockFlags.CapacityExhausted;
                    }

                    BlockCopier.Copy(exhausted.Block, replacement, buildPostings: false, complete: false);
                    // Dispose first: private blocks delete on close; canonical blocks need an
                    // explicit deletion while the caller still holds their cache owner lock.
                    Dispose();
                    if (!deleteOnClose)
                    {
                        File.Delete(blockPath);
                    }

                    File.Move(replacementPath, blockPath);
                    replacement.AdoptRenamedPath(blockPath);
                }
                catch
                {
                    replacement.Dispose();
                    if (!deleteOnClose)
                    {
                        File.Delete(replacementPath);
                    }

                    throw;
                }

                _capacities = next;
                _block = replacement;
                DiagnosticsReport.Send(runtime.Diagnostics,
                    $"Grew block file '{blockPath}': enumeration capacity exhausted at {rowCount} rows; " +
                    $"the walk continues with {_capacities.SlotCapacity} slots, {_capacities.NameEntryCapacity} name entries " +
                    $"and {_capacities.NamePoolCapacity} name-pool bytes.");
                return new BlockWriter(replacement);
            }

            /// <summary>Hands the current block to the caller, who owns it from here.</summary>
            public BlockFile Release()
            {
                var released = Block;
                _block = null;
                return released;
            }

            public void Dispose()
            {
                _block?.Dispose();
                _block = null;
            }
        }
    }
}

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
            ///     Copies the exhausted block's rows out, disposes and deletes it, and restores the rows into a
            ///     block with only the exhausted regions doubled. Null when every exhausted region is at its clamp.
            /// </summary>
            public BlockWriter? Grow(BlockWriter exhausted)
            {
                var next = _capacities.AtLeast(BlockLayout.GrowthMinimum(in exhausted.Block.Header));
                if (next == _capacities)
                {
                    return null;
                }

                var snapshot = BlockRowSnapshot.Capture(exhausted.Block);
                // Dispose first: private blocks delete on close; canonical blocks need an
                // explicit deletion while the caller still holds their cache owner lock.
                Dispose();
                if (!deleteOnClose)
                {
                    File.Delete(blockPath);
                }

                _capacities = next;
                _block = runtime.CreateEnumerationBlock(blockPath, deleteOnClose, _capacities);
                snapshot.RestoreInto(_block);
                DiagnosticsReport.Send(runtime.Options.Diagnostics,
                    $"Grew block file '{blockPath}': enumeration capacity exhausted at {snapshot.RowCount} rows; " +
                    $"the walk continues with {_capacities.SlotCapacity} slots, {_capacities.NameEntryCapacity} name entries " +
                    $"and {_capacities.NamePoolCapacity} name-pool bytes.");
                return new BlockWriter(_block);
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

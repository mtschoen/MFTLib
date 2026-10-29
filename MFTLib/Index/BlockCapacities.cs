namespace MFTLib.Index;

readonly record struct BlockCapacities(uint SlotCapacity, uint NameEntryCapacity, uint NamePoolCapacity,
    uint DeltaCapacity)
{
    public BlockCapacities AtLeast(BlockCapacities minimum)
    {
        return new BlockCapacities(
            Math.Max(SlotCapacity, minimum.SlotCapacity),
            Math.Max(NameEntryCapacity, minimum.NameEntryCapacity),
            Math.Max(NamePoolCapacity, minimum.NamePoolCapacity),
            Math.Max(DeltaCapacity, minimum.DeltaCapacity));
    }
}

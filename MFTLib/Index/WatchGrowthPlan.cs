namespace MFTLib.Index;

/// <summary>
///     The capacities of the replacement a watch growth copies a block into, per growable refusal. No region
///     ever shrinks. <see cref="WriteRefusal.Slots" /> re-plans every region from the refused record;
///     <see cref="WriteRefusal.NameEntries" /> and <see cref="WriteRefusal.NamePool" /> double their region;
///     <see cref="WriteRefusal.Delta" /> doubles the delta until it reaches the slot count, and the posting base the
///     copy rebuilds empties it either way.
/// </summary>
static class WatchGrowthPlan
{
    /// <summary>The capacities of <paramref name="block" /> as built.</summary>
    internal static BlockCapacities CapacitiesOf(BlockFile block) =>
        new(block.SlotCapacity, block.NameEntryCapacity, block.NamePoolCapacity, block.DeltaCapacity);

    /// <summary>Plans the replacement for a refusal of <paramref name="reason" /> by the record <paramref name="recordNumber" />.</summary>
    /// <param name="current">The capacities of the block that refused.</param>
    /// <param name="rowCount">The block's row count.</param>
    /// <param name="reason">The growable region that refused.</param>
    /// <param name="recordNumber">The refused record, which a slot growth must make room for.</param>
    /// <returns>The replacement's capacities, each at least the current one.</returns>
    /// <exception cref="InvalidOperationException">The refused region is at its clamp, so no growth can make room.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason" /> is not a growable region.</exception>
    internal static BlockCapacities Plan(BlockCapacities current, uint rowCount, WriteRefusal reason, uint recordNumber)
    {
        return reason switch
        {
            WriteRefusal.Slots => PlanSlots(current, rowCount, recordNumber),
            WriteRefusal.NameEntries => current with
            {
                NameEntryCapacity = DoubleNameRegionWithoutCompaction(current.NameEntryCapacity,
                    BlockLayout.DoubleNameEntryCapacity, "name entry", BlockLayout.MaximumNameEntryCapacity)
            },
            WriteRefusal.NamePool => current with
            {
                NamePoolCapacity = DoubleNameRegionWithoutCompaction(current.NamePoolCapacity,
                    BlockLayout.DoubleNamePoolCapacity, "name pool", BlockLayout.MaximumNamePoolCapacity)
            },
            WriteRefusal.Delta => current with { DeltaCapacity = PlanDelta(current) },
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only a growable region is planned.")
        };
    }

    static BlockCapacities PlanSlots(BlockCapacities current, uint rowCount, uint recordNumber)
    {
        var required = Math.Max((ulong)recordNumber + 1, rowCount);
        if (required > BlockLayout.MaximumEstimatedRowCount)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Record {recordNumber} lies past the largest slot region a block can plan ({BlockLayout.MaximumEstimatedRowCount} rows)."));
        }

        return BlockLayout.PlanCapacities((uint)required, current);
    }

    /// <summary>
    ///     Doubles a name region. Lane 7 of MFTLib issue 953 compacts the names first and doubles only a region that is
    ///     still more than half full after compaction; until then every name growth doubles.
    /// </summary>
    static uint DoubleNameRegionWithoutCompaction(uint capacity, Func<uint, uint> doubler, string region, uint clamp)
    {
        var doubled = doubler(capacity);
        if (doubled <= capacity)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The {region} region is at its clamp of {clamp}."));
        }

        return doubled;
    }

    static uint PlanDelta(BlockCapacities current) =>
        current.DeltaCapacity >= current.SlotCapacity
            ? current.DeltaCapacity
            : Math.Min(BlockLayout.DoubleCapacity(current.DeltaCapacity), current.SlotCapacity);
}

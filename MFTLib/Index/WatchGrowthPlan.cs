namespace MFTLib.Index;

/// <summary>
///     The capacities of the replacement a watch growth copies a block into, per growable refusal, and whether the copy
///     compacts names. No region ever shrinks. <see cref="WriteRefusal.Slots" /> re-plans every region from the refused
///     record; <see cref="WriteRefusal.NameEntries" /> and <see cref="WriteRefusal.NamePool" /> always compact, then
///     double their region only if it is still more than half full; <see cref="WriteRefusal.Delta" /> doubles the delta
///     until it reaches the slot count, and the posting base the copy rebuilds empties it either way.
/// </summary>
static class WatchGrowthPlan
{
    /// <summary>
    ///     A growth for any reason compacts once unused names are at least one in this many stored names, so a delta or
    ///     slot growth also sheds them. A default; lane 8 of MFTLib issue 953 measures it.
    /// </summary>
    internal const uint UnusedNameShareDenominator = 8;

    /// <summary>
    ///     A refused name region doubles only when what it holds after compaction exceeds its capacity divided by this,
    ///     so every growth leaves at least that share free and cannot recur at once. A default, measured with the above.
    /// </summary>
    internal const uint FullNameRegionShareDenominator = 2;

    /// <summary>The capacities of <paramref name="block" /> as built.</summary>
    internal static BlockCapacities CapacitiesOf(BlockFile block) =>
        new(block.SlotCapacity, block.NameEntryCapacity, block.NamePoolCapacity, block.DeltaCapacity);

    /// <summary>
    ///     True when a growth for <paramref name="reason" /> compacts names: always for a name region, otherwise once
    ///     unused names reach <see cref="UnusedNameShareDenominator" />'s share of the stored ones.
    /// </summary>
    internal static bool CompactsNames(WriteRefusal reason, NameUsage names) =>
        reason is WriteRefusal.NameEntries or WriteRefusal.NamePool ||
        (names.UnusedEntries != 0 && (ulong)names.UnusedEntries * UnusedNameShareDenominator >= names.StoredEntries);

    /// <summary>Plans the replacement for a refusal of <paramref name="reason" /> by the record <paramref name="recordNumber" />.</summary>
    /// <param name="current">The capacities of the block that refused.</param>
    /// <param name="rowCount">The block's row count.</param>
    /// <param name="reason">The growable region that refused.</param>
    /// <param name="recordNumber">The refused record, which a slot growth must make room for.</param>
    /// <param name="names">The block's stored and referenced names, from <see cref="NameReferences.Count" />.</param>
    /// <returns>The replacement's capacities, each at least the current one, and whether the copy compacts names.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The refused region is at its clamp and, for a name region, its referenced names alone leave no room, so no
    ///     growth can make room.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason" /> is not a growable region.</exception>
    internal static PlannedGrowth Plan(BlockCapacities current, uint rowCount, WriteRefusal reason, uint recordNumber,
        NameUsage names)
    {
        var capacities = reason switch
        {
            WriteRefusal.Slots => PlanSlots(current, rowCount, recordNumber),
            WriteRefusal.NameEntries => current with
            {
                NameEntryCapacity = PlanNameRegion(current.NameEntryCapacity, names.ReferencedEntries, 1,
                    BlockLayout.DoubleNameEntryCapacity, "name entry", BlockLayout.MaximumNameEntryCapacity)
            },
            WriteRefusal.NamePool => current with
            {
                NamePoolCapacity = PlanNameRegion(current.NamePoolCapacity, names.CompactedPoolBytes,
                    BlockLayout.NamePoolExhaustionMarginBytes + 1, BlockLayout.DoubleNamePoolCapacity, "name pool",
                    BlockLayout.MaximumNamePoolCapacity)
            },
            WriteRefusal.Delta => current with { DeltaCapacity = PlanDelta(current) },
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only a growable region is planned.")
        };
        return new PlannedGrowth(capacities, CompactsNames(reason, names));
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
    ///     A refused name region after compaction: kept when at most <see cref="FullNameRegionShareDenominator" />'s share
    ///     of it is used, doubled otherwise. At the clamp it is kept while the compacted use leaves room for another name.
    /// </summary>
    /// <param name="capacity">The region's current capacity.</param>
    /// <param name="compacted">What the referenced names use of the region once compacted.</param>
    /// <param name="roomForAnotherName">The free share of the region that still stores one more name: an entry, or the pool bytes of any NTFS-length name.</param>
    /// <param name="doubler">Doubles the capacity, clamped.</param>
    /// <param name="region">The region's name, for the clamp failure.</param>
    /// <param name="clamp">The region's clamp, for the clamp failure.</param>
    static uint PlanNameRegion(uint capacity, ulong compacted, ulong roomForAnotherName, Func<uint, uint> doubler,
        string region, uint clamp)
    {
        if (compacted * FullNameRegionShareDenominator <= capacity)
        {
            return capacity;
        }

        var doubled = doubler(capacity);
        if (doubled > capacity)
        {
            return doubled;
        }

        if (compacted + roomForAnotherName <= capacity)
        {
            return capacity;
        }

        throw new InvalidOperationException(FormattableString.Invariant(
            $"The {region} region is at its clamp of {clamp}, and its referenced names alone leave no room."));
    }

    static uint PlanDelta(BlockCapacities current) =>
        current.DeltaCapacity >= current.SlotCapacity
            ? current.DeltaCapacity
            : Math.Min(BlockLayout.DoubleCapacity(current.DeltaCapacity), current.SlotCapacity);
}

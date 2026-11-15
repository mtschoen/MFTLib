namespace MFTLib.Index;

/// <summary>What <see cref="WatchGrowthPlan.Plan" /> decided for one watch growth.</summary>
/// <param name="Capacities">The replacement's capacities, each at least the current one.</param>
/// <param name="CompactsNames">True when the copy stores only referenced names (<see cref="BlockCopier.CopyCompacting" />).</param>
readonly record struct PlannedGrowth(BlockCapacities Capacities, bool CompactsNames);

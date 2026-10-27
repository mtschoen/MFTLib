using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One watch start the index asked for: the drive and the cursor it resumed from.</summary>
/// <param name="Drive">The normalized drive key the index started a watch on.</param>
/// <param name="Cursor">The journal position the watch resumed from.</param>
public readonly record struct ScriptedWatchStart(VolumeKey Drive, SyntheticJournalCursor Cursor)
{
    /// <summary>Pairs the drive identity with the persisted cursor; default keys are rejected.</summary>
    public VolumeKey Drive { get; init => field = value.Validated(); } = Drive.Validated();
}

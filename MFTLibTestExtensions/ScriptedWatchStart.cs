using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>One watch start the index asked for: the volume and the cursor it resumed from.</summary>
/// <param name="Volume">The normalized volume key the index started a watch on.</param>
/// <param name="Cursor">The journal position the watch resumed from.</param>
public readonly record struct ScriptedWatchStart(VolumeKey Volume, SyntheticJournalCursor Cursor)
{
    /// <summary>Pairs the volume identity with the persisted cursor; default keys are rejected.</summary>
    public VolumeKey Volume { get; init => field = value.Validated(); } = Volume.Validated();
}

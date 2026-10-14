namespace MFTLib.Index;

/// <summary>Why a checkpoint-loss report cannot supply a journal growth target.</summary>
public enum JournalGrowthTargetRefusalReason
{
    /// <summary>The loss was not trimming; a larger journal would not have prevented it.</summary>
    CheckpointNotTrimmed,

    /// <summary>No retention size was recorded, including when the computed size overflowed a long.</summary>
    RetentionSizeUnavailable,

    /// <summary>The recorded retention size is zero or negative, as a synthetic report can supply.</summary>
    NonPositiveRetentionSize,

    /// <summary>The recorded allocation delta is zero or negative, as a synthetic report can supply.</summary>
    NonPositiveAllocationDelta,

    /// <summary>The journal was already configured at least this large when the checkpoint was lost; projection cannot supply a growth request.</summary>
    RetentionSizeNotGreaterThanMaximum
}

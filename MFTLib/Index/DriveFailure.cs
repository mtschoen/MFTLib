namespace MFTLib.Index;

/// <summary>
///     Why one drive failed: the kind a consumer branches on and the message it shows. Carried by
///     <see cref="DriveStatus.Failure" /> while a drive is <see cref="DriveState.Failed" />, and while a
///     <see cref="DriveState.Ready" /> drive retains its previous block after a failed
///     <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> or a failed catch-up retry scan during
///     <see cref="FileIndex.OpenAsync" />.
/// </summary>
public sealed record DriveFailure
{
    internal DriveFailure(DriveFailureKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>Which failure this is; <see cref="DriveFailureKind.CacheDeclined" />, <see cref="DriveFailureKind.CacheTagMismatch" /> and <see cref="DriveFailureKind.InUse" /> are cured by a rescan's scan, <see cref="DriveFailureKind.ProducerFailed" /> is the scan itself failing.</summary>
    public DriveFailureKind Kind { get; init; }

    /// <summary>The detail behind <see cref="Kind" />: the MFT producer's error, or the library's sentence for a cache-only refusal. Never null.</summary>
    public string Message { get; init; }
}

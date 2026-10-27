namespace MFTLib.Index;

/// <summary>
///     Why one root failed: the kind a consumer branches on and the message it shows. Carried by
///     <see cref="RootStatus.Failure" /> while a root is <see cref="RootState.Failed" />, and while a
///     <see cref="RootState.Ready" /> root retains its previous block after a failed
///     <see cref="FileIndex.RescanAsync" /> or a failed catch-up retry scan during
///     <see cref="FileIndex.OpenAsync" />.
/// </summary>
public sealed record RootFailure
{
    internal RootFailure(RootFailureKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>Which failure this is; <see cref="RootFailureKind.CacheDeclined" />, <see cref="RootFailureKind.CacheIdentityMismatch" /> and <see cref="RootFailureKind.InUse" /> are cured by a rescan's scan, <see cref="RootFailureKind.ProducerFailed" /> is the scan itself failing, and <see cref="RootFailureKind.MftSourceMissing" /> cannot be cured while the source stays null.</summary>
    public RootFailureKind Kind { get; init; }

    /// <summary>The detail behind <see cref="Kind" />: the producer's error, or the library-owned sentence for a cache-only refusal or missing MFT source. Never null.</summary>
    public string Message { get; init; }
}

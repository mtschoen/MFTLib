namespace MFTLib.Index;

/// <summary>
///     One resource owner shared by every struct copy of a row enumerator: the active name pass, its exact-name candidate rows, the subtree membership cache and the reusable
///     ASCII capture buffer. Advancing any copy bumps the version and invalidates older copies, which is what keeps a
///     row and its captured name from mismatching.
/// </summary>
sealed class RowEnumerationResources : IDisposable
{
    NameQueryMatches? _names;
    PostingCandidates? _candidates;
    SubtreeMembership? _membership;
    char[] _nameCharacters = [];
    int _version;

    internal NameQueryMatches Names => _names ??
        throw new InvalidOperationException("Name matching requires an active root.");

    /// <summary>The active root's posting-driven candidate rows when its query is an exact name; null for a row scan.</summary>
    internal PostingCandidates? Candidates => _candidates;

    /// <summary>The active root's subtree membership test, or null when the root's scan excludes no row.</summary>
    internal SubtreeMembership? Membership => _membership;

    internal void ValidateVersion(int version)
    {
        if (version != _version)
        {
            throw new InvalidOperationException("Another copy of this row enumerator has advanced.");
        }
    }

    internal int Advance(int version)
    {
        ValidateVersion(version);
        return ++_version;
    }

    internal void BeginRoot(RootBlock rootBlock, SearchQuery query, ResolvedSubtree? explicitAnchor,
        uint? admittedViewRow, CancellationToken cancellationToken)
    {
        EndRoot();
        _names = new NameQueryMatches(rootBlock.Block, query, cancellationToken);
        if (_names.IsExactCandidates)
        {
            _candidates = PostingCandidates.Gather(rootBlock.Block, PostingCandidates.IdentifiersOf(_names),
                cancellationToken);
        }

        _membership = SubtreeAnchor.CreateMembership(rootBlock, explicitAnchor, admittedViewRow);
    }

    internal void EndRoot()
    {
        _candidates?.Dispose();
        _candidates = null;
        _names?.Dispose();
        _names = null;
        _membership?.Dispose();
        _membership = null;
    }

    /// <summary>Exposes a name as characters: mapped UTF-16 text directly, ASCII through the shared buffer.</summary>
    internal ReadOnlySpan<char> Capture(NameView name)
    {
        if (name.IsUtf16)
        {
            return name.Utf16Characters;
        }

        if (_nameCharacters.Length < name.Length)
        {
            _nameCharacters = new char[name.Length];
        }

        name.CopyTo(_nameCharacters);
        return _nameCharacters.AsSpan(0, name.Length);
    }

    public void Dispose() => EndRoot();
}

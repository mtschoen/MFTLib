namespace MFTLib.Index;

/// <summary>
///     One resource owner shared by every struct copy of a row enumerator: the active name pass and the reusable
///     ASCII capture buffer. Advancing any copy bumps the version and invalidates older copies, which is what keeps a
///     row and its captured name from mismatching.
/// </summary>
sealed class RowEnumerationResources : IDisposable
{
    NameQueryMatches? _names;
    char[] _nameCharacters = [];
    int _version;

    internal NameQueryMatches Names => _names ??
        throw new InvalidOperationException("Name matching requires an active root.");

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

    internal void BeginRoot(BlockFile block, SearchQuery query, CancellationToken cancellationToken)
    {
        EndRoot();
        _names = new NameQueryMatches(block, query, cancellationToken);
    }

    internal void EndRoot()
    {
        _names?.Dispose();
        _names = null;
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

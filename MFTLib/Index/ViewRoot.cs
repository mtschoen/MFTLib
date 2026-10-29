namespace MFTLib.Index;

/// <summary>
///     Binds an index root to a row of the block it reads. A volume root, a walk and a dump are rooted at the
///     block's own root row and are always present. A directory root on an NTFS volume is a view of the whole
///     volume's block, rooted at the row its path resolves to; that row can move, vanish and return as the journal
///     renames the directory away and back, so the binding is rechecked at publication, after each applied journal
///     batch and at query admission.
/// </summary>
/// <remarks>
///     <para>
///         The binding (row, sequence number, presence) is one <see cref="long" /> published with a volatile write and
///         read with a volatile read, so a reader never pairs one refresh's row with another's presence.
///         <see cref="RefreshAfterBatch" /> recomputes the binding from the block's rows under a gate, so a slow refresh that
///         raced a journal batch can never overwrite a newer verdict with an older one: whichever refresh takes the
///         gate last reads the rows as they are then.
///     </para>
///     <para>
///         A query that took its block borrow before a rename keeps reading under the binding it saw at admission
///         only through <see cref="Row" /> reads it makes itself; the engines read the binding once per query.
///     </para>
/// </remarks>
sealed class ViewRoot
{
    const int RowShift = 0;
    const int SequenceShift = 32;
    const long PresentBit = 1L << 48;

    /// <summary>
    ///     What a directory view follows: the directory's host path, the path rules its block uses, and the block's rendering
    ///     root in matchable form, which splits the path into the part below the volume root.
    /// </summary>
    sealed record Target(string Path, RootKind Kind, string MatchableVolumePrefix);

    /// <summary>
    ///     How the view's path last failed to resolve: each level the walk tried (the row searched, the name looked for
    ///     and the child it preferred, none for the missing one).
    /// </summary>
    sealed record Absence(LookupEngine.DescentLevel[] Levels);

    readonly Lock _gate = new();
    readonly Target? _target;
    Absence? _absence;
    long _state;
    long _resolutionDescents;

    ViewRoot(uint rootRow) => _state = PresentBit | rootRow;

    ViewRoot(string path, RootKind kind, string volumeRootDirectory)
    {
        _target = new Target(path, kind, kind.ToMatchablePrefix(kind.NormalizeRoot(volumeRootDirectory)) ?? string.Empty);
        MatchablePath = kind.ToMatchablePrefix(kind.NormalizeRoot(path));
    }

    /// <summary>A root at the block's own root row; always present.</summary>
    internal static ViewRoot ForBlockRoot(uint rootRow) => new(rootRow);

    /// <summary>
    ///     A directory view of a volume block, absent until the first <see cref="Resolve" /> resolves it.
    /// </summary>
    /// <param name="path">The normalized host path of the directory.</param>
    /// <param name="kind">The block's path rules.</param>
    /// <param name="volumeRootDirectory">The host path the block renders its rows under.</param>
    internal static ViewRoot ForDirectory(string path, RootKind kind, string volumeRootDirectory) =>
        new(path, kind, volumeRootDirectory);

    /// <summary>True for a directory view, whose rows are a subtree of the block rather than the whole block.</summary>
    internal bool IsDirectoryView => _target is not null;

    /// <summary>
    ///     The view's path in the form <c>LookupEngine.Find</c> matches against, or null for a root that derives
    ///     it from its block's rendering root.
    /// </summary>
    internal string? MatchablePath { get; }

    /// <summary>Whether the view's directory is currently in the block.</summary>
    internal bool IsPresent => (Volatile.Read(ref _state) & PresentBit) != 0;

    /// <summary>The view's current row; meaningful only while <see cref="IsPresent" />.</summary>
    internal uint Row => (uint)(Volatile.Read(ref _state) >> RowShift);

    /// <summary>The sequence number the view's row had when the binding was made.</summary>
    internal ushort Sequence => (ushort)(Volatile.Read(ref _state) >> SequenceShift);

    /// <summary>Reads the row and presence from one snapshot of the binding.</summary>
    internal bool TryGetRow(out uint row)
    {
        var state = Volatile.Read(ref _state);
        row = (uint)(state >> RowShift);
        return (state & PresentBit) != 0;
    }

    /// <summary>
    ///     Descents of the block's rows that looked the view's path up: one per resolution, however many components it has.
    ///     Counted so tests can prove an unrelated journal batch or a query on an absent view costs none.
    /// </summary>
    internal long ResolutionDescents => Volatile.Read(ref _resolutionDescents);

    /// <summary>
    ///     Resolves the view from scratch, as publication does for a block no batch has touched. Costs one descent.
    /// </summary>
    /// <param name="rootBlock">The block the view is bound to; the caller keeps it mapped.</param>
    /// <param name="cancellationToken">Stops the descent.</param>
    /// <returns>Whether the view's directory is present.</returns>
    internal bool Resolve(RootBlock rootBlock, CancellationToken cancellationToken)
    {
        if (_target is not { } target)
        {
            return true;
        }

        lock (_gate)
        {
            return ResolveLocked(target, rootBlock, cancellationToken, out _);
        }
    }

    /// <summary>
    ///     Follows the view's directory through one applied journal batch, under the write gate. A bound view is kept
    ///     while its row is in use, not deleted, has the same sequence number and still renders the view's path (cost: the
    ///     path's depth). A view that stopped matching, or is absent, is looked up again only when the batch could change
    ///     the answer: an absent path can only come into existence through an entry that names one of its components, so
    ///     an unrelated batch costs nothing however large the block has grown.
    /// </summary>
    /// <param name="rootBlock">The block the view is bound to.</param>
    /// <param name="entries">The batch that was just applied.</param>
    internal void RefreshAfterBatch(RootBlock rootBlock, IReadOnlyList<UsnJournalEntry> entries)
    {
        if (_target is not { } target)
        {
            return;
        }

        lock (_gate)
        {
            var current = Volatile.Read(ref _state);
            if ((current & PresentBit) != 0)
            {
                if (StillBound(target, rootBlock, (uint)(current >> RowShift), (ushort)(current >> SequenceShift)))
                {
                    return;
                }
            }
            else if (_absence is not { } absence || !CanChangeTheOutcome(target, absence, entries))
            {
                return;
            }

            _ = ResolveLocked(target, rootBlock, CancellationToken.None, out _);
        }
    }

    /// <summary>
    ///     Admits one query: returns the row the view's directory is bound to, read in the same step that checked the
    ///     binding, so the query restricts itself to that row for its whole length whatever a later batch does. A bound
    ///     view whose row stopped matching (a batch applied since the last follow-up) is looked up again; an absent view
    ///     is answered as absent without a descent, because only a batch's follow-up can make it present.
    /// </summary>
    /// <param name="rootBlock">The block the view is bound to; the caller has borrowed it.</param>
    /// <param name="row">The admitted row when present.</param>
    /// <returns>Whether the view's directory is present.</returns>
    internal bool TryAdmit(RootBlock rootBlock, out uint row)
    {
        if (_target is not { } target)
        {
            row = rootBlock.Block.Header.RootRow;
            return true;
        }

        lock (_gate)
        {
            var current = Volatile.Read(ref _state);
            row = (uint)(current >> RowShift);
            if ((current & PresentBit) == 0)
            {
                return false;
            }

            return StillBound(target, rootBlock, row, (ushort)(current >> SequenceShift)) ||
                   ResolveLocked(target, rootBlock, CancellationToken.None, out row);
        }
    }

    bool ResolveLocked(Target target, RootBlock rootBlock, CancellationToken cancellationToken, out uint row)
    {
        Interlocked.Increment(ref _resolutionDescents);
        var resolved = TryResolve(target, rootBlock, cancellationToken, out row, out var absence);
        _absence = resolved ? null : absence;
        Volatile.Write(ref _state, resolved
            ? PresentBit | ((long)rootBlock.Block.SequenceNumbers[(int)row] << SequenceShift) | row
            : 0);
        return resolved;
    }

    // What can change where an absent path leads is limited to rows that sit under a row the walk searched and carry the
    // name it looked for at that level: a create, a rename to that name, or a modification record that hydrates a row
    // never written (the mutator installs its name and parent). An entry for the child the walk already preferred at
    // that level changes nothing by itself. A structural change to a row the walk stood on (create, delete, rename)
    // also counts. Everything else, including the same names under other parents and records for other names, cannot
    // change the answer. The cost is the batch's entries times the path's depth.
    static bool CanChangeTheOutcome(Target target, Absence absence, IReadOnlyList<UsnJournalEntry> entries)
    {
        const UsnReason restructures = UsnReason.FileCreate | UsnReason.RenameNewName | UsnReason.FileDelete |
                                       UsnReason.RenameOldName;
        foreach (var entry in entries)
        {
            foreach (var level in absence.Levels)
            {
                if (entry.ParentRecordNumber == level.ParentRow &&
                    (level.FoundRow is not { } preferred || entry.RecordNumber != preferred) &&
                    string.Equals(entry.FileName, level.Name, target.Kind.Comparison))
                {
                    return true;
                }

                if ((entry.Reason & restructures) != 0 && level.FoundRow is { } found && entry.RecordNumber == found)
                {
                    return true;
                }
            }
        }

        return false;
    }

    bool StillBound(Target target, RootBlock rootBlock, uint row, ushort sequence)
    {
        var block = rootBlock.Block;
        if (row >= block.Header.RowCount)
        {
            return false;
        }

        ref readonly var candidate = ref block.Rows[(int)row];
        if (!candidate.IsInUse || candidate.IsDeleted || block.SequenceNumbers[(int)row] != sequence)
        {
            return false;
        }

        return RendersPath(target, rootBlock, row);
    }

    bool RendersPath(Target target, RootBlock rootBlock, uint row)
    {
        try
        {
            var rendered = target.Kind.ToMatchablePrefix(IndexNavigation.BuildPath(rootBlock, row));
            return string.Equals(rendered, MatchablePath, target.Kind.Comparison);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    static bool TryResolve(Target target, RootBlock rootBlock, CancellationToken cancellationToken, out uint row,
        out Absence? absence)
    {
        string? folded = null;
        row = 0;
        absence = null;
        if (!LookupEngine.TryMatchRootPrefix(target.Kind, target.MatchableVolumePrefix, target.Path, ref folded,
                out var prefixLength))
        {
            return false;
        }

        var trace = new LookupEngine.DescentTrace();
        if (LookupEngine.TryDescend(rootBlock, rootBlock.Block.Header.RootRow, target.Path.AsSpan(prefixLength),
                trace, cancellationToken, out row))
        {
            return true;
        }

        absence = trace.Levels.Count == 0 ? null : new Absence([.. trace.Levels]);
        return false;
    }
}

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed record VolumeScanCreation(ResolvedRoot Resolved, FileIndexOptions Options,
        string CacheDirectoryPath, IndexHooks Hooks);

    internal sealed partial class IndexView(FileIndex index, ResolvedRoot resolved)
    {
        FileIndex Index { get; } = index;
        internal ResolvedRoot Resolved { get; set; } = resolved;
        // Assigned during OpenAsync, before the index can escape to a consumer. Publish/acquire
        // access also makes the binding explicit to callbacks from the creating scan.
        VolumeScan? _scan;
        internal VolumeScan Scan
        {
            get => Volatile.Read(ref _scan) ?? throw new InvalidOperationException("The view has not attached to a scan.");
            set => Volatile.Write(ref _scan, value);
        }
        internal RootBlock? Block { get; set; }
        internal ViewBorrows Borrows { get; } = new();
        internal FileIndexOptions Options => Index._options;
        internal VolumeScanRegistry Registry => Options.ScanRegistry ?? VolumeScanRegistry.Process;
        internal (RootFailure Failure, PendingRootResult Result)? OpenFailure { get; set; }
        // StateLock protects publication to the consumer as well as block publication. A failed
        // open can later read a sibling's settle, so its retry mappings must retain handle lifetime.
        internal bool OpenCompleted { get; private set; }
        internal void CompleteOpenLocked() => OpenCompleted = true;
        internal bool IsDisposed { get; private set; }
        internal void MarkDisposedLocked() => IsDisposed = true;
        internal CancellationToken DisposalToken => Index.DisposalToken;
        Lock StateLock => Scan.StateLock;
        internal RootKey Root => Resolved.Root;
        internal RootKind Kind => RootKind.For(Resolved.Producer, Resolved.IsDump);
        internal string RootDirectory => Resolved.RootDirectory;
        internal string? MatchableRootDirectoryPath => Kind.ToMatchablePrefix(Kind.NormalizeRoot(RootDirectory));

        internal RootBlock BindBlock(MappedBlock mapped)
        {
            var view = IsDirectoryViewOfVolume()
                ? ViewRoot.ForDirectory(Resolved.RootDirectory, Kind, Resolved.RenderRootDirectory)
                : null;
            var block = new RootBlock(Root, mapped, Borrows, Resolved.RenderRootDirectory,
                Index._options.MftSource, view);
            view?.Resolve(block, Scan.ClosingToken);
            return block;
        }

        bool IsDirectoryViewOfVolume()
        {
            if (Resolved.Producer != ProducerKind.Mft || Resolved.IsDump || Root.IsVolume ||
                Resolved.VolumeRootDirectory is not { } volumeRoot)
            {
                return false;
            }

            return !string.Equals(Kind.ToMatchablePrefix(RootDirectory), Kind.ToMatchablePrefix(volumeRoot),
                Kind.Comparison);
        }
    }
}

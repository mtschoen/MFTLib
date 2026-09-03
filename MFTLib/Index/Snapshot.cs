using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The set of drive blocks current at one moment. A <see cref="FileEntry" /> holds a
///     reference to its snapshot, so a handle keeps its block mapped for as long as the handle
///     is reachable. The finalizer is the release path for handles the caller simply drops;
///     <see cref="ReleaseNow" /> is the deterministic path the index uses on teardown.
/// </summary>
public sealed class Snapshot
{
    readonly DriveBlock[] _driveBlocks;
    int _releaseState;

    Snapshot(DriveBlock[] driveBlocks)
    {
        _driveBlocks = driveBlocks;
    }

    /// <summary>
    ///     Releases the snapshot's blocks for a caller who simply dropped every handle. The catch
    ///     is deliberately unconditional and local to this path: releasing unmaps a view and can
    ///     delete a retired block file, so an unbalanced count or a file the operating system will
    ///     not unlink surfaces as an exception, and an exception leaving a finalizer terminates the
    ///     process with a stack no consumer can act on. The worst case swallowed here is a leaked
    ///     mapping or a leftover file, which is what the caller already has. <see cref="ReleaseNow" />
    ///     keeps propagating, because a deterministic teardown failure is worth surfacing.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1075",
        Justification = "A finalizer is the one place where catching everything and doing nothing is the " +
                        "correct behaviour: an exception that escapes it terminates the process. There is " +
                        "nothing to log to and nothing to retry, and the swallowed worst case is a leaked " +
                        "mapping, which is strictly better than the alternative. Scoped to this method only.")]
    ~Snapshot()
    {
        try
        {
            ReleaseCore();
        }
        catch (Exception)
        {
            // See this finalizer's summary.
        }
    }

    public IReadOnlyList<DriveBlock> DriveBlocks => _driveBlocks;

    public int DriveCount => _driveBlocks.Length;

    /// <summary>
    ///     Takes one reference on every block. Throws if any block has already been fully
    ///     released, because a snapshot over an unmapped block would hand out handles that read
    ///     freed memory.
    /// </summary>
    public static Snapshot Create(IReadOnlyList<DriveBlock> driveBlocks)
    {
        ArgumentNullException.ThrowIfNull(driveBlocks);
        var taken = new List<DriveBlock>(driveBlocks.Count);
        foreach (var driveBlock in driveBlocks)
        {
            if (driveBlock.TryAddReference())
            {
                taken.Add(driveBlock);
                continue;
            }

            foreach (var alreadyTaken in taken)
            {
                alreadyTaken.Release();
            }

            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} was already released and cannot join a snapshot.");
        }

        return new Snapshot([.. taken]);
    }

    public DriveBlock GetDriveBlock(ushort driveOrdinal)
    {
        return _driveBlocks[driveOrdinal];
    }

    /// <summary>Null when no current drive block has this letter.</summary>
    public DriveBlock? FindDriveBlock(char driveLetter)
    {
        foreach (var candidate in _driveBlocks)
        {
            if (char.ToUpperInvariant(candidate.DriveLetter) == char.ToUpperInvariant(driveLetter))
            {
                return candidate;
            }
        }

        return null;
    }

    [SuppressMessage("Design", "CA1816",
        Justification = "ReleaseNow is the deterministic teardown path FileIndex calls; it is internal rather " +
                         "than a public Dispose because ordinary consumers release a snapshot only by dropping " +
                         "their FileEntry handles and letting the finalizer run.")]
    internal void ReleaseNow()
    {
        if (ReleaseCore())
        {
            GC.SuppressFinalize(this);
        }
    }

    bool ReleaseCore()
    {
        if (Interlocked.Exchange(ref _releaseState, 1) != 0)
        {
            return false;
        }

        foreach (var driveBlock in _driveBlocks)
        {
            driveBlock.Release();
        }

        return true;
    }
}

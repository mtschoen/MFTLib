namespace MFTLib.Index;

/// <summary>Resolves admitted options into a <see cref="ResolvedRoot" />: reads the volume facts once and picks the producer.</summary>
static class RootResolution
{
    /// <summary>
    ///     Reads the facts of the volume under the root once and applies <see cref="FileIndexOptions.Producer" />.
    ///     A missing <see cref="FileIndexOptions.MftSource" /> is not a fact: it is judged last, and only by an open
    ///     that has to produce a block, so a cached MFT block still opens without a source.
    /// </summary>
    /// <param name="prepared">Options already admitted by <see cref="RootKind.PrepareOpenOptions" />.</param>
    internal static ResolvedRoot Resolve(FileIndexOptions prepared)
    {
        if (prepared.MftSource?.DumpIdentity is { } identity)
        {
            return new ResolvedRoot
            {
                Root = identity.Volume,
                RootDirectory = identity.Root,
                Volume = identity.Volume,
                VolumeRootDirectory = identity.Root,
                Producer = ProducerKind.Mft,
                IsDump = true
            };
        }

        var root = prepared.Root ?? throw new InvalidOperationException("Prepared options must have a Root.");
        var hostPath = root.TryGetVolume(out var volumeKey) ? $"{volumeKey.Letter}:\\" : root.ToString();
        if (!root.IsVolume)
        {
            hostPath = ResolveDirectory(prepared, root, hostPath);
        }

        var readFacts = prepared.VolumeFacts ?? PlatformVolumeFacts.Read;
        var facts = readFacts(root, hostPath);
        var unavailable = MftEligibility.Evaluate(in facts, hasMftSource: true);
        var resolved = new ResolvedRoot
        {
            Root = root,
            RootDirectory = root.IsVolume ? facts.VolumeRootDirectory ?? hostPath : hostPath,
            Volume = facts.Volume,
            VolumeRootDirectory = facts.VolumeRootDirectory,
            VolumeSerial = facts.VolumeSerial,
            Producer = ProducerKind.Mft
        };
        return prepared.Producer switch
        {
            ProducerSelection.WalkOnly => resolved with { Producer = ProducerKind.Enumeration },
            ProducerSelection.MftOnly => resolved with { MftUnavailable = unavailable },
            _ => unavailable is { } reason ? resolved.AsWalk(reason) : resolved
        };
    }

    /// <summary>
    ///     The host path a directory root is read, indexed and matched under: its final path with every junction,
    ///     symbolic link and mount point followed, so two keys for one folder resolve alike and the volume facts are
    ///     those of the volume that really holds the folder. A directory missing at open keeps the path as named (the root
    ///     settles Offline, is never rescanned and gets no resolved path; a reopen resolves it); a failed resolution keeps it too and reports the reason once to
    ///     <see cref="FileIndexOptions.Diagnostics" />. Never throws.
    /// </summary>
    static string ResolveDirectory(FileIndexOptions prepared, RootKey root, string named)
    {
        if (!Directory.Exists(named))
        {
            return named;
        }

        var resolution = (prepared.FinalPath ?? PlatformFinalPath.Resolve)(named);
        if (resolution.Path is { } finalPath)
        {
            try
            {
                _ = RootKey.FromDirectory(finalPath, out var normalized);
                return normalized;
            }
            catch (ArgumentException exception)
            {
                resolution = new FinalPathResolution(null, exception.Message);
            }
        }

        DiagnosticsReport.Send(prepared.Diagnostics,
            $"Root {root}: its final path could not be resolved ({resolution.Failure}); the path as named is used.");
        return named;
    }
}

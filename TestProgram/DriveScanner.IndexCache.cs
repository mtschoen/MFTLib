using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The cache and elevation verbs, which open no index.
partial class DriveScanner
{
    void InspectOrClearCache(IndexVerbArguments verb)
    {
        var clear = verb.Has("--clear");
        var named = verb.Text("--cache-directory");
        if (clear && named is null)
        {
            throw new ArgumentException("--clear needs an explicit --cache-directory.");
        }

        var path = named ?? CacheDirectory.ResolveDefaultPath();
        if (verb.Has("--ensure-created"))
        {
            _writeLine($"Cache directory {CacheDirectory.EnsureCreated(path).FullName}");
        }

        var drives = verb.Drives.Count > 0 ? verb.Drives.ToHashSet() : null;
        if (clear)
        {
            foreach (var result in CacheDirectory.DeleteCached(path, drives, line => _writeLine($"  {line}")))
            {
                _writeLine($"  {result.File.Path}: {result.Outcome} {result.FailureReason}");
            }

            return;
        }

        var rejected = new List<CachedBlockRejection>();
        var expected = verb.Text("--cache-tag") is { } tag ? ParseCacheTag(tag) : (CacheTag?)null;
        _writeLine($"{CacheDirectory.InspectCached(path, drives).Count} cached blocks in {path}");
        foreach (var status in CacheDirectory.InspectCached(path, drives, rejected.Add))
        {
            var file = status.File;
            _writeLine($"  {file.Path}: drive {file.DriveLetter} serial {file.VolumeSerial} {file.SizeBytes} bytes " +
                       $"{file.LastWriteTimeUtc:u}; {status.Availability} {status.Validation} {status.ProducerKind} root " +
                       $"{status.RootDirectory} tag {status.CacheTag}" +
                       (expected is { } wanted && status.CacheTag is { } actual ? $" (equals {wanted}: {actual == wanted})" : string.Empty));
        }

        rejected.ForEach(rejection => _writeLine($"  rejected {rejection.Path}: {rejection.Reason}"));
    }

    void ShowElevationStatus()
    {
        IElevationProvider provider = ElevationUtilities.DefaultProvider;
        _writeLine($"Elevated {ElevationUtilities.IsElevated()} (provider {provider.IsElevated()}); can relaunch " +
                   $"elevated {ElevationUtilities.CanSelfElevate()} (provider {provider.CanSelfElevate()}); an elevated " +
                   $"relaunch is waited for up to {ElevationUtilities.DefaultElevatedTimeout}. Broker verbs raise one UAC " +
                   "prompt; the enumeration source needs none.");
    }
}

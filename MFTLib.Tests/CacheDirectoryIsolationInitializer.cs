using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace MFTLib.Tests;

internal static class CacheDirectoryIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
    }
}

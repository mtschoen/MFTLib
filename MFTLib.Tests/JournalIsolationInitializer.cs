using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace MFTLib.Tests;

internal static class JournalIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
    }
}

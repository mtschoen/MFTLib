using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace MFTLib.Tests;

static class JournalIsolationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
        SyntheticVolumeFacts.AllowOverrides();
    }
}

using MFTLib.Tests.Index;

namespace MFTLib.Tests.TestSupport;

internal static class CompletedBlocks
{
    public static SyntheticBlockBuilder Create(DateTime scanMoment, char driveLetter = 'T')
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        try
        {
            builder.AddRoot();
            builder.Complete(scanMoment);
            return builder;
        }
        catch
        {
            builder.Dispose();
            throw;
        }
    }
}

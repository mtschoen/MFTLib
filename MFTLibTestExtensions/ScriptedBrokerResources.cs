namespace MFTLibTestExtensions;

/// <summary>
///     What a scripted in-process broker owns beside its process: the client's block sections, the host's
///     section writer, and the log of scans the host began.
/// </summary>
internal sealed class ScriptedBrokerResources : IDisposable
{
    readonly List<InProcessBrokerScan> _scans = [];

    public ScriptedBrokerResources()
    {
        Writer = new RecordingBlockSectionWriter(Sections.Resolve);
    }

    public TestBlockSections Sections { get; } = new();

    public RecordingBlockSectionWriter Writer { get; }

    /// <summary>Every scan the host began, in order.</summary>
    public IReadOnlyList<InProcessBrokerScan> Scans()
    {
        lock (_scans)
        {
            return [.. _scans];
        }
    }

    public void Dispose()
    {
        Writer.Dispose();
        Sections.Dispose();
    }

    public void RecordScan(string drive, IReadOnlyCollection<string>? directoryScanFileNames)
    {
        var scan = new InProcessBrokerScan(drive, directoryScanFileNames);
        lock (_scans)
        {
            _scans.Add(scan);
        }
    }
}

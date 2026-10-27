using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     What a scripted in-process broker owns beside its process: the client's block sections, the host's
///     section writer, and the log of scans the host began.
/// </summary>
sealed class ScriptedBrokerResources : IDisposable
{
    readonly List<InProcessBrokerScan> _scans = [];

    // ReSharper disable once ConvertToPrimaryConstructor
    // aislop-ignore-next-line jb/ConvertToPrimaryConstructor -- the explicit constructor body is the ownership transfer IDisposableAnalyzers follows; a primary-constructor initializer hides it and raises IDISP001 in Create
    public ScriptedBrokerResources(TestBlockSections sections,
        RecordingBlockSectionWriter writer, BrokerBlockSectionFactory createBlockSection)
    {
        Sections = sections;
        Writer = writer;
        CreateBlockSection = createBlockSection;
    }

    public static ScriptedBrokerResources Create()
    {
        var sections = new TestBlockSections();
        var writer = new RecordingBlockSectionWriter(sections.Resolve);
        return new ScriptedBrokerResources(sections, writer, sections.Create);
    }

    public TestBlockSections Sections { get; }
    public RecordingBlockSectionWriter Writer { get; }
    public BrokerBlockSectionFactory CreateBlockSection { get; }

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

    public void RecordScan(VolumeKey volume, IReadOnlyCollection<string>? retainedFileNames)
    {
        var scan = new InProcessBrokerScan(volume, retainedFileNames);
        lock (_scans)
        {
            _scans.Add(scan);
        }
    }
}

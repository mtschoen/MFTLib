using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests.SampleHostContract;

// The shared contract against SampleProgram.Direct's copy of SampleHost, plus the Direct-only elevation rules: a local
// scan self-elevates, which the Watch sample's broker launch does not.
[TestClass]
[DoNotParallelize]
public sealed class DirectSharedSampleHostTests : SharedSampleHostTests
{
    string? _directoryPath;

    // The base class's setup already builds a subject, before this class's own setup could run.
    string WorkingDirectory => _directoryPath ??= Directory.CreateTempSubdirectory("notice-tests-").FullName;

    [TestCleanup]
    public void DeleteDirectory()
    {
        if (_directoryPath is not null)
        {
            Directory.Delete(_directoryPath, true);
        }
    }

    internal override SampleHostSubject CreateSubject(TextWriter output, TextReader input, FakeTimeProvider clock, Action elevated) =>
        new DirectSampleHostSubject(output, input, WorkingDirectory, clock, elevated);

    SampleHost DirectHost(List<string> lines) =>
        new(new LineCollectingWriter(lines), TextReader.Null, WorkingDirectory, new FakeTimeProvider())
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _messageBeep = _ => true,
            _messageBox = (_, _, _, _) => throw new AssertFailedException("No dialog is expected."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("No elevation is expected.")
        };

    [TestMethod]
    public void Run_NotElevated_CannotSelfElevate_ShowsNoDialog()
    {
        var host = DirectHost([]);
        host._canSelfElevate = () => false;

        var result = host.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () => 0);

        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void Run_Elevated_IgnoresTheUnattendedSwitch()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var lines = new List<string>();
        var host = DirectHost(lines);
        host._isElevated = () => true;

        var result = host.RunWithElevation(["scan", "T"], ElevationNeed.SelfElevate, () =>
            throw new IOException("the scan was attempted"));

        Assert.AreEqual(1, result, "The scan was attempted and failed; the switch changed nothing.");
        Assert.DoesNotContain(line => line.Contains("Running unattended"), lines);
    }

    [TestMethod]
    public void Run_LocalScan_UnattendedButAlreadyElevated_ScansAsUsual()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var lines = new List<string>();
        var host = DirectHost(lines);
        host._isElevated = () => true;

        var result = host.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () =>
            throw new IOException("the scan was attempted"));

        Assert.AreEqual(1, result);
        var report = File.ReadAllText(host.OutputLogPath);
        Assert.Contains("Error: IOException: the scan was attempted", report);
        Assert.DoesNotContain(line => line.Contains("Running unattended"), lines);
    }
}

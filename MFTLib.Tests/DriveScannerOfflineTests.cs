using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The parse-file mode runs the real offline parse over the deterministic fixture image: it needs
// no volume, no elevation and no seam, so nothing here is faked.
[TestClass]
[DoNotParallelize]
public class DriveScannerOfflineTests
{
    string _fixturePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _fixturePath = Path.Combine(Path.GetTempPath(), $"driveScannerOffline-{Guid.NewGuid():N}.mft");
        if (OperatingSystem.IsWindows())
        {
            MftVolume.GenerateFixtureMFT(_fixturePath);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        File.Delete(_fixturePath);
    }

    [TestMethod]
    public void Run_ParseFile_Defaults_ParsesEveryInUseRecordWithPathsAndTimings()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        var result = Scanner(lines).Run(["parse-file", _fixturePath]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 8 records in ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("  Native: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.Contains("[record 6 sequence ") && line.Contains("size 37")));
        Assert.IsTrue(lines.Any(line => line.Contains(" directory in use ")));
        Assert.IsTrue(lines.Contains($"=== File {_fixturePath}: done ==="));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Completed at ", StringComparison.Ordinal)));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow(4u)]
    public void Run_ParseFile_NoPathsWithoutAName_UsesTheUnfilteredOverloadAndLeavesPathsUnresolved(uint? bufferSize)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();
        uint parsedWith = 0;
        var route = MFTLibNative._parseMftFromFile;
        MFTLibNative._parseMftFromFile = (filePath, filter, matchFlags, bufferSizeRecords) =>
        {
            parsedWith = bufferSizeRecords;
            return route(filePath, filter, matchFlags, bufferSizeRecords);
        };

        var arguments = new List<string> { "parse-file", _fixturePath, "--no-paths" };
        if (bufferSize is { } size)
        {
            arguments.Add("--buffer-size");
            arguments.Add(size.ToString(CultureInfo.InvariantCulture));
        }

        Scanner(lines).Run([.. arguments]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 8 records in ", StringComparison.Ordinal)));
        Assert.IsFalse(lines.Any(line => line.Contains(" name ")), "No record carries a resolved path.");
        Assert.AreEqual(bufferSize ?? 262144u, parsedWith);
    }

    [TestMethod]
    public void Run_ParseFile_NameOnly_KeepsTheExactMatch()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        Scanner(lines).Run(["parse-file", _fixturePath, "--name", "resident.txt"]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 1 records in ", StringComparison.Ordinal)));
        Assert.AreEqual(1, lines.Count(line => line.Contains("[record 6 ")));
    }

    [TestMethod]
    public void Run_ParseFile_ContainsWithFreedAndABufferSize_KeepsTheFreedMatches()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        Scanner(lines).Run(["parse-file", _fixturePath, "--name", "deleted", "--contains", "--include-freed",
            "--buffer-size", "4"]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 8 records in ", StringComparison.Ordinal)));
        Assert.AreEqual(8, lines.Count(line => line.Contains(" freed ")));
    }

    [TestMethod]
    public void Run_ParseFile_Stream_ReadsTheNativeResultEveryWay()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        Scanner(lines).Run(["parse-file", _fixturePath, "--stream", "--batch-size", "3"]);

        Assert.IsTrue(lines.Any(line => line.StartsWith("Parsed 24 records, 8 kept, ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Contains("Materialized 8 records in 3 batches"));
        Assert.IsTrue(lines.Contains("ToArray holds 8 records"));
        Assert.IsTrue(lines.Contains("Retained after the result was disposed:"));
    }

    [TestMethod]
    public void Run_ParseFile_StreamWithABufferSize_ParsesTheSameRecords()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var lines = new List<string>();

        Scanner(lines).Run(["parse-file", _fixturePath, "--stream", "--buffer-size", "4", "--no-paths"]);

        Assert.IsTrue(lines.Contains("Materialized 8 records in 1 batches"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_ParseFile_MissingFile_PrintsTheErrorAndCarriesOn(bool stream)
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.mft");
        var lines = new List<string>();
        string[] arguments = stream ? ["parse-file", missing, "--stream"] : ["parse-file", missing];

        var result = Scanner(lines).Run(arguments);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith($"Error on file {missing}: ", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Completed at ", StringComparison.Ordinal)));
    }

    // Any contact with elevation fails the test: the offline parse touches no volume.
    static DriveScanner Scanner(List<string> lines)
    {
        return new DriveScanner
        {
            _isElevated = () => throw new AssertFailedException("parse-file must not check elevation."),
            _canSelfElevate = () => throw new AssertFailedException("parse-file must not self-elevate."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("parse-file must not self-elevate."),
            _writeLine = lines.Add
        };
    }
}

using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndexOptions.RetainedFileNames" /> is validated at <see cref="FileIndex.OpenAsync" />
///     admission, before any cache directory exists or any scan starts.
/// </summary>
[TestClass]
public sealed class FileIndexRetainedFileNamesAdmissionTests : IDisposable
{
    IndexTreeFixture _fixture = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture();

    public void Dispose() => _fixture.Dispose();

    static string[] KeepList(int count) => [.. Enumerable.Repeat(new string('n', 255), count)];

    static MftIndexSource BrokerSource() =>
        new BrokerMftBlockProducer(_ => throw new InvalidOperationException("no broker is reachable in this test"))
            .CreateIndexSource();

    [TestMethod]
    public async Task OpenAsync_SetTooLongForOneBrokerFrame_ThrowsBeforeTheCacheDirectoryIsCreated()
    {
        var options = _fixture.Options(producer: ProducerKind.Mft) with
        {
            MftSource = BrokerSource(),
            RetainedFileNames = KeepList(32_700)
        };

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FileIndex.OpenAsync(options, TestContext.CancellationToken));

        Assert.Contains("frame limit", exception.Message);
        Assert.IsFalse(Directory.Exists(_fixture.CacheDirectory));
    }

    [TestMethod]
    public async Task OpenAsync_SetTooLongForOneBrokerFrame_IsAcceptedWhenNoDriveUsesTheMftProducer()
    {
        var options = _fixture.Options(producer: ProducerKind.Enumeration) with
        {
            MftSource = BrokerSource(),
            RetainedFileNames = KeepList(32_700)
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [TestMethod]
    public async Task OpenAsync_NullElementInTheSet_ThrowsBeforeTheCacheDirectoryIsCreated()
    {
        var options = _fixture.Options() with { RetainedFileNames = ["a", null!] };

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FileIndex.OpenAsync(options, TestContext.CancellationToken));

        Assert.Contains("RetainedFileNames", exception.Message);
        Assert.IsFalse(Directory.Exists(_fixture.CacheDirectory));
    }

    [TestMethod]
    [DataRow(0xD800, true)]
    [DataRow(0xD801, true)]
    [DataRow(0xDC00, false)]
    public async Task OpenAsync_UnpairedSurrogateInTheSet_ThrowsBeforeTheCacheDirectoryIsCreated(int codeUnit, bool trailing)
    {
        var options = _fixture.Options() with { RetainedFileNames = ["a", MalformedText.Make(codeUnit, trailing)] };

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FileIndex.OpenAsync(options, TestContext.CancellationToken));

        Assert.Contains("unpaired surrogate", exception.Message);
        Assert.IsFalse(Directory.Exists(_fixture.CacheDirectory));
    }

    [TestMethod]
    public async Task OpenAsync_ReplacementCharacterAndValidPairInTheSet_AreAdmitted()
    {
        var options = _fixture.Options() with { RetainedFileNames = ["x\uFFFD", "x\uD83D\uDE00"] };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [TestMethod]
    public async Task OpenAsync_MaterializesTheSetOnceSoLaterEditsDoNotChangeTheWalk()
    {
        await File.WriteAllTextAsync(Path.Combine(_fixture.TreeRoot, "keep.txt"), "x", TestContext.CancellationToken);
        var retained = new List<string> { "keep.txt" };
        var options = _fixture.Options() with { RetainedFileNames = retained };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        retained.Clear();

        Assert.IsNotNull(index.Find(Path.Combine(_fixture.TreeRoot, "keep.txt"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(Path.Combine(_fixture.TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));
    }
}

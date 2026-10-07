using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.MetadataSurfaceReader;

namespace MFTLib.Tests;

/// <summary>
///     Decision 10: every public authored member of MFTLib and MFTLib.Index has a real caller outside the
///     library tests, either a sample use or a consumer production caller. The sample half is proved here by
///     reading the compiled IL of SampleProgram.Direct and SampleProgram.Watch; the consumer half is the short
///     reasoned exempt list, one entry per member, each naming the consumer file that calls it.
///     A literal <c>const</c> is inlined by the compiler and leaves no IL reference, so the constants are a
///     separate reasoned list that must equal the set the library metadata declares, in both directions.
/// </summary>
[TestClass]
public class PublicMemberCallerTests
{
    const string DirectSample = "SampleProgram.Direct.dll";
    const string WatchSample = "SampleProgram.Watch.dll";

    /// <summary>
    ///     Members with no sample caller that a consumer production caller uses, by display text, one reason each.
    ///     The reason names the consumer repository and file, never a line number, which rots.
    ///     Adding an entry is a review decision.
    /// </summary>
    static readonly Dictionary<string, string> ExemptMembers = new(StringComparer.Ordinal)
    {
        ["type MFTLib.BrokerDiagnostics"] =
            "file-wizard file-wizard/ProgramEntry.cs and FileWizardMaui/App.xaml.cs enable broker diagnostics at startup",
        ["method MFTLib.BrokerDiagnostics.Enable(System.String) : System.Void"] =
            "file-wizard file-wizard/ProgramEntry.cs and FileWizardMaui/App.xaml.cs enable broker diagnostics at startup",
        ["property MFTLib.BrokerDiagnostics.LogDirectory : System.String"] =
            "file-wizard file-wizard/ProgramEntry.cs and FileWizardMaui/App.xaml.cs point the diagnostics log at the local files path",
        ["type MFTLib.BrokerLauncher"] =
            "file-wizard file-wizard/BrokerSmoke.cs and git-wizard GitWizard/Discovery/RepositoryDiscoveryDependencies.cs launch the broker",
        ["method MFTLib.BrokerLauncher.Launch(System.String) : System.Boolean"] =
            "file-wizard file-wizard/BrokerSmoke.cs and git-wizard GitWizard/Discovery/RepositoryDiscoveryDependencies.cs launch the broker",
        ["method MFTLib.BrokerSession..ctor(System.Func`2<System.String,System.Boolean>, System.Nullable`1<System.TimeSpan>) : System.Void"] =
            "git-wizard GitWizard/MftIndexSession.Windows.cs builds the session with its own launcher and connect timeout",
        ["method MFTLib.BrokerSession.DisposeAsync() : System.Threading.Tasks.ValueTask"] =
            "reached through IAsyncDisposable, so no IL names it; file-wizard file-wizard/BrokerSmoke.cs disposes the session with await using",
        ["method MFTLib.ElevationUtilities.CanSelfElevate() : System.Boolean"] =
            "file-wizard file-wizard/CliServices.cs decides whether to self-elevate",
        ["method MFTLib.ElevationUtilities.IsElevated() : System.Boolean"] =
            "file-wizard file-wizard/CliServices.cs checks the elevation before a scan",
        ["method MFTLib.ElevationUtilities.TryRunElevated(System.Collections.Generic.IReadOnlyList`1<System.String>, System.TimeSpan) : System.Boolean"] =
            "file-wizard file-wizard/CliServices.cs and file-wizard/ConfigureDefenderCommand.cs rerun elevated",
        ["method MFTLib.Index.CacheDirectory.EnsureCreated(System.String) : System.IO.DirectoryInfo"] =
            "file-wizard FileWizard/ContentHashSidecar.cs creates the cache directory before it writes beside it",
        ["method MFTLib.Index.CacheDirectory.InspectCached(System.String, System.Collections.Generic.IReadOnlySet`1<System.Char>) : System.Collections.Generic.IReadOnlyList`1<MFTLib.Index.CachedBlockStatus>"] =
            "git-wizard GitWizard/MftIndexCacheInspection.cs inspects every cached block of a directory",
        ["method MFTLib.Index.FileIndex.RescanAsync(System.Collections.Generic.IReadOnlyList`1<System.Char>, System.Threading.CancellationToken) : System.Threading.Tasks.Task`1<System.Collections.Generic.IReadOnlyList`1<MFTLib.Index.DriveOperationResult>>"] =
            "file-wizard FileWizard/FileIndexHost.cs and git-wizard GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs rescan a chosen set of drives",
        ["method MFTLib.Index.FileIndex.StartWatchingAsync(System.Char, System.Threading.CancellationToken) : System.Threading.Tasks.Task"] =
            "file-wizard FileWizardMaui/MainPage.DriveRescan.cs restarts the watch of one rescanned drive",
        ["method MFTLib.Index.FileIndex.WaitForCatchUpAsync(System.Char, System.Threading.CancellationToken) : System.Threading.Tasks.Task"] =
            "git-wizard GitWizard/Watch/IndexVolumeChangeSource.Startup.cs waits for each drive in turn",
        ["method MFTLib.Index.MftIndexSource.Unavailable(System.String) : MFTLib.Index.MftIndexSource"] =
            "file-wizard FileWizard/FileIndexHost.cs supplies a source for a host that may not scan",
        ["property MFTLib.Index.FileIndexOptions.InitialOpenCacheOnly : System.Boolean"] =
            "file-wizard FileWizard/FileIndexHost.cs opens cache-only first",
        ["property MFTLib.Index.FileIndexOptions.OpenProgress : System.IProgress`1<MFTLib.Index.IndexDriveOpened>"] =
            "file-wizard FileWizard/FileIndexHost.cs forwards the open progress sink",
        ["type MFTLib.Index.IndexDriveOpened"] =
            "file-wizard FileWizardMaui.Logic/OpenProgressPresenter.cs shows each settled drive",
        ["property MFTLib.Index.IndexDriveOpened.DriveLetter : System.Char"] =
            "file-wizard FileWizardMaui.Logic/OpenProgressPresenter.cs shows each settled drive",
        ["property MFTLib.Index.IndexDriveOpened.SettledDriveCount : System.Int32"] =
            "file-wizard FileWizardMaui.Logic/OpenProgressPresenter.cs shows each settled drive",
        ["property MFTLib.Index.IndexDriveOpened.TotalDriveCount : System.Int32"] =
            "file-wizard FileWizardMaui.Logic/OpenProgressPresenter.cs shows each settled drive"
    };

    /// <summary>
    ///     Every public literal constant, by declaring type and field. The compiler inlines each value into the
    ///     caller, so no IL names it; the reason says why the constant is public.
    /// </summary>
    static readonly Dictionary<string, string> InlinedConstants = new(StringComparer.Ordinal)
    {
        ["MFTLib.Index.FileIndex.LostCatchUpRecoveryLimit"] =
            "file-wizard FileWizardMaui.Logic/JournalHintLogic.cs reads the limit to word the lost catch-up hint"
    };

    static string LibraryPath => typeof(FileIndex).Assembly.Location;

    static string SamplePath(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    /// <summary>The surface members that none of the referenced identities reach, in a stable order.</summary>
    static List<SurfaceMember> FindUnreferenced(Surface surface, IReadOnlySet<string> references,
        Dictionary<string, string> exempt) =>
        surface.Members
            .Where(member => !exempt.ContainsKey(member.Display) && !member.Keys.Any(references.Contains))
            .OrderBy(member => member.Display, StringComparer.Ordinal)
            .ToList();

    static string Describe(IEnumerable<SurfaceMember> members) =>
        string.Join(Environment.NewLine, members.Select(member => "  " + member.Display));

    [TestMethod]
    public void EveryPublicMember_HasASampleOrReasonedConsumerCaller()
    {
        if (!File.Exists(SamplePath(WatchSample)))
        {
            Assert.Inconclusive($"{WatchSample} is built on Windows only; the caller gate is enforced there.");
        }

        var surface = ReadSurface(LibraryPath);
        var references = CollectReferences([SamplePath(DirectSample), SamplePath(WatchSample)]);

        var unreferenced = FindUnreferenced(surface, references, ExemptMembers);
        Assert.AreEqual(0, unreferenced.Count,
            "These public members have no caller in SampleProgram.Direct or SampleProgram.Watch and no reasoned " +
            "exemption. Give each a real use, internalize it, or delete it:" + Environment.NewLine +
            Describe(unreferenced));

        var staleExemptions = ExemptMembers.Keys.Where(key => surface.Members.All(member => member.Display != key)).ToList();
        Assert.AreEqual(0, staleExemptions.Count,
            "These exempt entries name no public member: " + string.Join("; ", staleExemptions));

        var redundant = surface.Members
            .Where(member => ExemptMembers.ContainsKey(member.Display) && member.Keys.Any(references.Contains))
            .Select(member => member.Display).ToList();
        Assert.AreEqual(0, redundant.Count,
            "These exempt members now have a sample caller; drop the exemption: " + string.Join("; ", redundant));
    }

    [TestMethod]
    public void TheConstantList_EqualsTheLiteralFieldsTheLibraryDeclares()
    {
        var declared = ReadSurface(LibraryPath).LiteralFields.ToHashSet(StringComparer.Ordinal);

        var unlisted = declared.Except(InlinedConstants.Keys).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var lingering = InlinedConstants.Keys.Except(declared).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.AreEqual(0, unlisted.Count, "Public constants with no reason: " + string.Join("; ", unlisted));
        Assert.AreEqual(0, lingering.Count, "Listed constants the library no longer declares: " + string.Join("; ", lingering));
    }

    /// <summary>
    ///     Negative control, part one. The real sample reference set with the references of one known member
    ///     removed must report exactly that member as newly unreferenced, and name it in the failure text.
    ///     The member is asserted by identity, so an unrelated gap cannot satisfy the control.
    /// </summary>
    [TestMethod]
    public void TheGate_ReportsAMemberWhoseReferenceWasRemoved()
    {
        var surface = ReadSurface(LibraryPath);
        var references = CollectReferences([SamplePath(DirectSample)]);
        var openAsync = surface.Members.Single(member => member.Display.StartsWith(
            "method MFTLib.Index.FileIndex.OpenAsync(", StringComparison.Ordinal));
        Assert.IsTrue(openAsync.Keys.Any(references.Contains), "the control needs a member the sample really calls");

        var before = FindUnreferenced(surface, references, new Dictionary<string, string>());
        var withoutOpenAsync = references.Except(openAsync.Keys).ToHashSet(StringComparer.Ordinal);
        var after = FindUnreferenced(surface, withoutOpenAsync, new Dictionary<string, string>());

        var added = after.Except(before).ToList();
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(openAsync.Display, added[0].Display);
        StringAssert.Contains(Describe(after), "FileIndex.OpenAsync(");
    }

    /// <summary>
    ///     Negative control, part two. A fixture inside the test assembly calls one overload of a library overload
    ///     set and nothing else. The collector, run over that fixture type's IL (the rest of the test assembly
    ///     calls every overload somewhere), must find that overload and not its sibling, which proves overloads
    ///     are told apart and that a reference absent from the IL is reported.
    /// </summary>
    [TestMethod]
    public void TheCollector_TellsOverloadsApart()
    {
        // Built, not invoked: the method-group conversion is what leaves the member reference in the IL.
        Assert.IsNotNull(CallerGateFixtureCaller.ReferenceThreeParameterInspectCached());

        var surface = ReadSurface(LibraryPath);
        var references = CollectReferencesFromType(typeof(PublicMemberCallerTests).Assembly.Location,
            "MFTLib.Tests.CallerGateFixtureCaller");
        var overloads = surface.Members.Where(member => member.Display.StartsWith(
            "method MFTLib.Index.CacheDirectory.InspectCached(", StringComparison.Ordinal)).ToList();
        var called = overloads.Single(member => member.Display.Contains("Action`1<MFTLib.Index.CachedBlockRejection>"));
        var sibling = overloads.Single(member => member != called);

        Assert.IsTrue(called.Keys.Any(references.Contains), "the overload the fixture calls must be found");
        Assert.IsFalse(sibling.Keys.Any(references.Contains), "the sibling overload no IL names must not be found");
    }

    /// <summary>
    ///     A positional record that also declares its own Deconstruct overload and property: the generated
    ///     positional properties stay off the surface, the authored property stays on it.
    /// </summary>
    [TestMethod]
    public void TheReader_KeepsAuthoredMembersOfAPositionalRecordWithASecondDeconstruct()
    {
        var surface = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location,
            ["MFTLib.Tests.CallerGateFixtures"]);
        var displays = surface.Members.Select(member => member.Display).ToList();

        Assert.IsTrue(displays.Any(display => display.StartsWith("property MFTLib.Tests.CallerGateFixtures.CallerGatePositionalRecord.Extra", StringComparison.Ordinal)),
            string.Join("; ", displays));
        Assert.IsFalse(displays.Any(display => display.Contains("CallerGatePositionalRecord.First", StringComparison.Ordinal)));
        Assert.IsFalse(displays.Any(display => display.Contains("CallerGatePositionalRecord.Second", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     The exempt key names one member: overloads that differ by generic arity, indexers that differ by
    ///     parameter, and real FileIndex overloads each get their own text, so one entry never covers a sibling.
    /// </summary>
    [TestMethod]
    public void AnExemptEntry_CoversExactlyOneMember()
    {
        var fixtures = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location, ["MFTLib.Tests.CallerGateFixtures"]);
        var fixtureDisplays = fixtures.Members.Select(member => member.Display).ToList();
        Assert.AreEqual(fixtureDisplays.Count, fixtureDisplays.Distinct().Count(), string.Join("; ", fixtureDisplays));

        var surface = ReadSurface(LibraryPath);
        var exempt = surface.Members.Single(member => member.Display.StartsWith(
            "method MFTLib.Index.FileIndex.RescanAsync(System.Collections.Generic.IReadOnlyList`1<System.Char>", StringComparison.Ordinal));
        var unreferenced = FindUnreferenced(surface, new HashSet<string>(),
            new Dictionary<string, string> { [exempt.Display] = "control" });

        Assert.IsFalse(unreferenced.Contains(exempt));
        Assert.IsTrue(unreferenced.Any(member => member.Display.StartsWith(
            "method MFTLib.Index.FileIndex.RescanAsync(System.Char,", StringComparison.Ordinal)));
    }
}

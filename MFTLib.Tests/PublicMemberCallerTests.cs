using System.Reflection;
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
    ///     The reason names the consumer repository and file; only an interpolated ToString, which no IL check can
    ///     find, also cites the line.
    ///     Adding an entry is a review decision.
    /// </summary>
    static readonly Dictionary<string, string> ExemptMembers = new(StringComparer.Ordinal)
    {
        ["property MFTLib.Index.WatchFault.IsRecovering : System.Boolean"] =
            "file-wizard FileWizard/JournalWatcher.cs and git-wizard GitWizard/Watch/IndexVolumeChangeSource.cs classify automatic fault recovery",
        ["method MFTLib.Index.DriveStatus.ToWatchState() : MFTLib.Index.DriveWatchState"] =
            "file-wizard FileWizardMaui/MainPage.WatchStates.cs and git-wizard GitWizard/Watch/IndexVolumeChangeSource.WatchStates.cs seed watch state from a captured status",
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
        ["method MFTLib.Index.CacheDirectory.EnsureCreated(System.String) : System.IO.DirectoryInfo"] =
            "file-wizard FileWizard/ContentHashSidecar.cs creates the cache directory before it writes beside it",
        ["method MFTLib.Index.CacheDirectory.InspectCached(System.String, System.Collections.Generic.IReadOnlySet`1<System.Char>) : System.Collections.Generic.IReadOnlyList`1<MFTLib.Index.CachedBlockStatus>"] =
            "git-wizard GitWizard/MftIndexCacheInspection.cs inspects every cached block of a directory",
        ["method MFTLib.Index.CacheTag.ToString() : System.String"] =
            "reached through string interpolation, which emits no member reference; file-wizard file-wizard/BlockCacheCatalog.cs:98 and git-wizard GitWizard/MftIndexCacheStatusFormatter.cs:50 print the stored and requested tags",
        ["method MFTLib.Index.FileIndex.RescanAsync(System.Collections.Generic.IReadOnlyList`1<System.Char>, System.Threading.CancellationToken) : System.Threading.Tasks.Task`1<System.Collections.Generic.IReadOnlyList`1<MFTLib.Index.DriveOperationResult>>"] =
            "file-wizard FileWizard/FileIndexHost.cs and git-wizard GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs rescan a chosen set of drives",
        ["method MFTLib.Index.FileIndex.StartWatchingAsync(System.Char, System.Threading.CancellationToken) : System.Threading.Tasks.Task"] =
            "file-wizard FileWizardMaui/MainPage.DriveRescan.cs restarts the watch of one rescanned drive",
        ["method MFTLib.Index.FileIndex.WaitForCatchUpAsync(System.Char, System.Threading.CancellationToken) : System.Threading.Tasks.Task"] =
            "git-wizard GitWizard/Watch/IndexVolumeChangeSource.Startup.cs waits for each drive in turn",
        ["method MFTLib.Index.FileEntry.Children(System.Threading.CancellationToken) : System.Collections.Generic.IReadOnlyList`1<MFTLib.Index.FileEntry>"] =
            "git-wizard GitWizard/GitWizardApi.IndexDiscovery.cs lists the children of a directory entry",
        ["method MFTLib.Index.MftIndexSource.Unavailable(System.String) : MFTLib.Index.MftIndexSource"] =
            "file-wizard FileWizard/FileIndexHost.cs supplies a source for a host that may not scan",
        ["method MFTLib.Index.JournalCheckpointLoss.TryGetGrowthTarget(MFTLib.UsnJournalSettings&) : System.Boolean"] =
            "file-wizard file-wizard/JournalCommand.cs and FileWizardMaui.Logic/JournalSettingsPresenter.cs, and git-wizard GitWizardUI/ViewModels/MainViewModel.Journals.cs use the recorded target for explicit journal growth",
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
    static readonly IReadOnlyDictionary<string, string> InlinedConstants =
        System.Collections.Frozen.FrozenDictionary<string, string>.Empty;

    // Coverlet rewrites the copy of MFTLib.dll next to the tests during a coverage run, which changes the IL the gate
    // reads, so the gate reads the library project's own build output, whose path the build recorded. There is no
    // fallback to the test-directory copy: a missing file fails on every platform.
    static string LibraryPath => ResolveLibraryPath(
        typeof(PublicMemberCallerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MFTLib.UninstrumentedOutput")?.Value);

    static string ResolveLibraryPath(string? recorded)
    {
        if (recorded is not null && File.Exists(recorded))
        {
            return recorded;
        }

        Assert.Fail($"The caller gate reads the library project's own build output, and {recorded ?? "its recorded path"} is missing.");
        return recorded!;
    }

    static string SamplePath(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    /// <summary>The surface members that none of the referenced identities reach, in a stable order.</summary>
    static List<SurfaceMember> FindUnreferenced(Surface surface, IReadOnlySet<string> references,
        Dictionary<string, string> exempt) =>
        surface.Members
            .Where(member => !exempt.ContainsKey(member.Display) && !member.IsReferencedBy(references))
            .OrderBy(member => member.Display, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     The sample assemblies the full gate reads. Off Windows the Watch sample is not built, so the gate is
    ///     Inconclusive there; on Windows a missing Watch assembly is a failure, never a skip.
    /// </summary>
    static string[] GateSamplePaths(bool isWindows, string baseDirectory)
    {
        if (!isWindows)
        {
            Assert.Inconclusive($"{WatchSample} is built on Windows only; the caller gate is enforced there.");
        }

        var watchPath = Path.Combine(baseDirectory, WatchSample);
        Assert.IsTrue(File.Exists(watchPath), $"The caller gate needs {watchPath} on Windows, and it is missing.");
        return [Path.Combine(baseDirectory, DirectSample), watchPath];
    }

    /// <summary>Fails naming every exposed type outside the surface namespaces, which the gate would never see.</summary>
    static void AssertNoOutOfScopeTypes(Surface surface) =>
        Assert.AreEqual(0, surface.OutOfScopeTypes.Count,
            "These public types sit outside the MFTLib and MFTLib.Index namespaces, so the caller gate would not see " +
            "them. Move them into a gated namespace or extend the gate: " + string.Join("; ", surface.OutOfScopeTypes));

    static string Describe(IEnumerable<SurfaceMember> members) =>
        string.Join(Environment.NewLine, members.Select(member => "  " + member.Display));

    [TestMethod]
    public void EveryPublicMember_HasASampleOrReasonedConsumerCaller()
    {
        var samplePaths = GateSamplePaths(OperatingSystem.IsWindows(), AppContext.BaseDirectory);
        var surface = ReadSurface(LibraryPath);
        AssertNoOutOfScopeTypes(surface);
        var references = CollectReferences(samplePaths);
        Assert.AreEqual(0, DuplicateKeys(surface).Count,
            "These keys name more than one public member, so one caller would credit both: " + string.Join("; ", DuplicateKeys(surface)));

        var unreferenced = FindUnreferenced(surface, references, ExemptMembers);
        Assert.AreEqual(0, unreferenced.Count,
            "These public members have no caller in SampleProgram.Direct or SampleProgram.Watch and no reasoned " +
            "exemption. Give each a real use, internalize it, or delete it:" + Environment.NewLine +
            Describe(unreferenced));

        var staleExemptions = ExemptMembers.Keys.Where(key => surface.Members.All(member => member.Display != key)).ToList();
        Assert.AreEqual(0, staleExemptions.Count,
            "These exempt entries name no public member: " + string.Join("; ", staleExemptions));

        var redundant = surface.Members
            .Where(member => ExemptMembers.ContainsKey(member.Display) && member.IsReferencedBy(references))
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
    ///     Records that declare members sharing a name or shape with synthesized ones (a Deconstruct overload,
    ///     ToString, a replaced positional property, a same-name constructor overload, a copy constructor): the
    ///     members the compiler marks CompilerGenerated, the synthesized EqualityContract and the primary
    ///     constructor stay off the surface; the authored ones, and every property, stay on it.
    /// </summary>
    [TestMethod]
    public void TheReader_KeepsAuthoredMembersOfAPositionalRecordWithASecondDeconstruct()
    {
        var surface = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location,
            ["MFTLib.Tests.CallerGateFixtures"]);
        var displays = surface.Members.Select(member => member.Display).ToList();

        const string Positional = "MFTLib.Tests.CallerGateFixtures.CallerGatePositionalRecord";
        const string Replaced = "MFTLib.Tests.CallerGateFixtures.CallerGateReplacedPropertyRecord";
        const string Copy = "MFTLib.Tests.CallerGateFixtures.CallerGateCopyRecord";
        string Report() => string.Join("; ", displays);

        // Authored members that share a name or shape with a synthesized one are kept.
        Assert.IsTrue(displays.Any(display => display.StartsWith($"property {Positional}.Extra", StringComparison.Ordinal)), Report());
        Assert.IsTrue(displays.Contains($"method {Positional}.Deconstruct(System.Int32&) : System.Void"), Report());
        Assert.IsTrue(displays.Contains($"method {Positional}.ToString() : System.String"), Report());
        Assert.IsTrue(displays.Contains($"method {Positional}..ctor(System.String, System.String) : System.Void"), Report());
        Assert.IsTrue(displays.Any(display => display.StartsWith($"property {Replaced}.Alpha", StringComparison.Ordinal)), Report());
        Assert.IsTrue(displays.Contains($"method {Copy}..ctor({Copy}) : System.Void"), Report());

        // Every property is listed, whatever its name, type or accessors.
        const string Sibling = "MFTLib.Tests.CallerGateFixtures.CallerGateCaseSiblingRecord";
        const string Contract = "MFTLib.Tests.CallerGateFixtures.CallerGateAuthoredContractRecord";
        const string Mixed = "MFTLib.Tests.CallerGateFixtures.CallerGateMixedAccessorRecord";
        Assert.IsTrue(displays.Contains($"property {Sibling}.first : System.String"), Report());
        Assert.IsTrue(displays.Contains($"property {Contract}.EqualityContract : System.Type"), Report());
        Assert.IsTrue(displays.Contains($"property {Mixed}.First : System.Int32"), Report());

        Assert.IsTrue(displays.Contains($"property {Positional}.First : System.Int32"), Report());
        Assert.IsTrue(displays.Contains($"property {Positional}.Second : System.Int32"), Report());
        Assert.IsTrue(displays.Contains($"property {Replaced}.Beta : System.Int32"), Report());
        Assert.IsTrue(displays.Contains($"property {Sibling}.First : System.Int32"), Report());

        // Members carrying evidence of synthesis are dropped.
        Assert.IsFalse(displays.Contains($"method {Positional}..ctor(System.Int32, System.Int32) : System.Void"), Report());
        Assert.IsFalse(displays.Contains($"method {Positional}.Deconstruct(System.Int32&, System.Int32&) : System.Void"), Report());
        Assert.IsFalse(displays.Contains($"method {Positional}..ctor({Positional}) : System.Void"), Report());
        Assert.IsFalse(displays.Contains($"method {Copy}..ctor(System.Int32) : System.Void"), Report());
        foreach (var synthesized in new[] { "Equals", "GetHashCode", "PrintMembers", "<Clone>$", "op_Equality", "op_Inequality" })
        {
            Assert.IsFalse(displays.Any(display => display.StartsWith($"method {Positional}.{synthesized}(", StringComparison.Ordinal)), Report());
        }

        Assert.IsFalse(displays.Any(display => display.Contains("EqualityContract", StringComparison.Ordinal) &&
                                                 !display.Contains(Contract, StringComparison.Ordinal)), Report());
    }

    /// <summary>
    ///     A constrained callvirt of an Object virtual dispatches to the override of the constrained type, so it
    ///     references that override; interpolation names no member, and a type with no override has no member to
    ///     credit.
    /// </summary>
    [TestMethod]
    public void AConstrainedObjectCall_ReferencesTheOverrideOfTheConstrainedType()
    {
        Assert.AreEqual("<invalid FileEntry>", CallerGateFixtureExplicitToString.Describe(default));
        Assert.AreEqual("entry <invalid FileEntry>", CallerGateFixtureInterpolatedToString.Describe(default));
        Assert.AreEqual("Mft", CallerGateFixtureInheritedToString.Describe(ProducerKind.Mft));

        var surface = ReadSurface(LibraryPath);
        var toString = surface.Members.Single(member => member.Display == "method MFTLib.Index.FileEntry.ToString() : System.String");
        var assembly = typeof(PublicMemberCallerTests).Assembly.Location;

        var explicitCall = CollectReferencesFromType(assembly, "MFTLib.Tests.CallerGateFixtureExplicitToString");
        Assert.IsTrue(toString.IsReferencedBy(explicitCall), string.Join("; ", explicitCall));
        Assert.IsTrue(toString.IsReferencedBy(CollectReferences([assembly])), "the whole-assembly collector must find it too");

        var interpolated = CollectReferencesFromType(assembly, "MFTLib.Tests.CallerGateFixtureInterpolatedToString");
        Assert.IsFalse(toString.IsReferencedBy(interpolated), string.Join("; ", interpolated));

        var inherited = CollectReferencesFromType(assembly, "MFTLib.Tests.CallerGateFixtureInheritedToString");
        Assert.IsFalse(toString.IsReferencedBy(inherited), "another type's call must not credit FileEntry.ToString");
        Assert.IsFalse(surface.Members.Any(member => member.Display.StartsWith("method MFTLib.Index.ProducerKind.ToString(", StringComparison.Ordinal)),
            "an enum declares no ToString, so the constrained call credits no member");
    }

    /// <summary>
    ///     Edge cases of the evidence rule, pinned as observed. The compiler marks the protected copy constructor
    ///     of a record class CompilerGenerated, so it is dropped. A zero-parameter positional record has no
    ///     synthesized Deconstruct and a record whose author wrote the primary-signature Deconstruct suppresses the
    ///     marked one; neither leaves evidence of the primary constructor, so the reader keeps it. Keeping is the
    ///     fail-loud direction: the gate asks for a caller or an exemption, and no authored member is ever dropped.
    /// </summary>
    [TestMethod]
    public void TheReader_PinsTheRecordEdgeCasesWithoutEvidenceOfSynthesis()
    {
        var surface = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location,
            ["MFTLib.Tests.CallerGateFixtures"]);
        var displays = surface.Members.Select(member => member.Display).ToList();
        string Report() => string.Join("; ", displays);

        const string Inheritable = "MFTLib.Tests.CallerGateFixtures.CallerGateInheritableRecord";
        Assert.IsFalse(displays.Any(display => display.StartsWith($"method {Inheritable}..ctor({Inheritable})", StringComparison.Ordinal)), Report());
        Assert.IsFalse(displays.Any(display => display.StartsWith($"method {Inheritable}.PrintMembers(", StringComparison.Ordinal)), Report());
        Assert.IsFalse(displays.Contains($"method {Inheritable}..ctor(System.Int32) : System.Void"), Report());

        const string Empty = "MFTLib.Tests.CallerGateFixtures.CallerGateEmptyRecord";
        Assert.IsTrue(displays.Contains($"method {Empty}..ctor() : System.Void"), Report());
        Assert.IsFalse(displays.Any(display => display.StartsWith($"method {Empty}..ctor({Empty})", StringComparison.Ordinal)), Report());

        const string Authored = "MFTLib.Tests.CallerGateFixtures.CallerGateAuthoredDeconstructRecord";
        Assert.IsTrue(displays.Contains($"method {Authored}.Deconstruct(System.Int32&, System.Int32&) : System.Void"), Report());
        Assert.IsTrue(displays.Contains($"method {Authored}..ctor(System.Int32, System.Int32) : System.Void"), Report());
        Assert.IsTrue(displays.Any(display => display.StartsWith($"property {Authored}.Left", StringComparison.Ordinal)), Report());
        Assert.IsTrue(displays.Any(display => display.StartsWith($"property {Authored}.Right", StringComparison.Ordinal)), Report());
        Assert.IsFalse(displays.Any(display => display.StartsWith($"method {Authored}..ctor({Authored})", StringComparison.Ordinal)), Report());
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

    [TestMethod]
    public void TheLibrary_ExposesTypesOnlyInTheGatedNamespaces() => AssertNoOutOfScopeTypes(ReadSurface(LibraryPath));

    /// <summary>
    ///     Negative control for the namespace guard: the test assembly's public types sit outside the namespace
    ///     handed to the reader, so the guard must fail and name one of them.
    /// </summary>
    [TestMethod]
    public void TheNamespaceGuard_NamesAPublicTypeOutsideTheSurfaceNamespaces()
    {
        var surface = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location, ["MFTLib.Tests.CallerGateFixtures"]);

        var failure = Assert.ThrowsException<AssertFailedException>(() => AssertNoOutOfScopeTypes(surface));
        StringAssert.Contains(failure.Message, "MFTLib.Tests.PublicMemberCallerTests");
    }

    [TestMethod]
    public void TheGate_FailsOnWindowsWhenTheWatchSampleIsMissing()
    {
        var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var failure = Assert.ThrowsException<AssertFailedException>(() => GateSamplePaths(true, absent));
        StringAssert.Contains(failure.Message, Path.Combine(absent, WatchSample));
        Assert.ThrowsException<AssertInconclusiveException>(() => GateSamplePaths(false, absent));
    }

    [TestMethod]
    public void AMissingLibraryOutput_Fails()
    {
        var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "MFTLib.dll");

        var failure = Assert.ThrowsException<AssertFailedException>(() => ResolveLibraryPath(absent));
        StringAssert.Contains(failure.Message, absent);
        Assert.ThrowsException<AssertFailedException>(() => ResolveLibraryPath(null));
        Assert.AreEqual(typeof(FileIndex).Assembly.Location, ResolveLibraryPath(typeof(FileIndex).Assembly.Location));
    }
}

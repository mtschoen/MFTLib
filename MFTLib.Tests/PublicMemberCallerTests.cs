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
        ["type MFTLib.BrokerDiagnostics"] =
            "file-wizard file-wizard/ProgramEntry.cs and FileWizardMaui/App.xaml.cs enable broker diagnostics at startup",
        ["method MFTLib.BrokerDiagnostics.Enable(System.String, System.String) : System.Void"] =
            "file-wizard file-wizard/ProgramEntry.cs and FileWizardMaui/App.xaml.cs enable broker diagnostics at startup",
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
        ["method MFTLib.Index.CacheTag.ToString() : System.String"] =
            "reached through string interpolation, which emits no member reference; file-wizard file-wizard/BlockCacheCatalog.cs:98 and git-wizard GitWizard/MftIndexCacheStatusFormatter.cs:50 print the stored and requested tags",
        ["property MFTLib.Index.DriveBlockStatus.Generation : System.UInt64"] =
            "file-wizard FileWizardMaui.Logic/DuplicateNameCounter.cs skips a duplicate-name recount when no drive's Generation changed (lands in file-wizard's 0.3.0 pin-bump pull request)",
        ["method MFTLib.Index.DriveKey.TryParse(System.String, MFTLib.Index.DriveKey&) : System.Boolean"] =
            "Owner-approved consumer migrations: file-wizard FileWizard/DriveSelectionParser.cs and FileWizard/FileWizardSettings.cs parse CLI and saved drive specs; git-wizard GitWizard/MftIndexSession.Windows.cs NormalizeVolume parses requested volumes",
        ["method MFTLib.Index.JournalCheckpointLoss.TryGetGrowthTarget(MFTLib.UsnJournalSettings&) : System.Boolean"] =
            "file-wizard file-wizard/JournalCommand.cs and FileWizardMaui.Logic/JournalSettingsPresenter.cs, and git-wizard GitWizardUI/ViewModels/MainViewModel.Journals.cs use the recorded target for explicit journal growth",
        ["type MFTLib.Index.DirectoryWalkOptions"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["method MFTLib.Index.DirectoryWalkOptions..ctor() : System.Void"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["property MFTLib.Index.DirectoryWalkOptions.PruneDotDirectories : System.Boolean"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["property MFTLib.Index.DirectoryWalkOptions.PrunedDirectoryAttributes : System.IO.FileAttributes"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["property MFTLib.Index.DirectoryWalkOptions.PrunedDirectoryNames : System.Collections.Generic.IReadOnlyCollection`1<System.String>"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["property MFTLib.Index.DirectoryWalkOptions.PrunedDirectoryPaths : System.Collections.Generic.IReadOnlyCollection`1<System.String>"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk",
        ["property MFTLib.Index.FileIndexOptions.DirectoryWalk : MFTLib.Index.DirectoryWalkOptions"] =
            "git-wizard GitWizard/MftIndexSession.cs (git-wizard issue 328) builds its index options with the directory walk rules that replace its hand-rolled walk"
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
        [.. surface.Members
            .Where(member => !exempt.ContainsKey(member.Display) && !member.IsReferencedBy(references))
            .OrderBy(member => member.Display, StringComparer.Ordinal)];

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
        Assert.IsEmpty(surface.OutOfScopeTypes,
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
        Assert.IsEmpty(DuplicateKeys(surface),
            "These keys name more than one public member, so one caller would credit both: " + string.Join("; ", DuplicateKeys(surface)));

        var unreferenced = FindUnreferenced(surface, references, ExemptMembers);
        Assert.IsEmpty(unreferenced,
            "These public members have no caller in SampleProgram.Direct or SampleProgram.Watch and no reasoned " +
            "exemption. Give each a real use, internalize it, or delete it:" + Environment.NewLine +
            Describe(unreferenced));

        var staleExemptions = ExemptMembers.Keys.Where(key => surface.Members.All(member => member.Display != key)).ToList();
        Assert.IsEmpty(staleExemptions,
            "These exempt entries name no public member: " + string.Join("; ", staleExemptions));

        var redundant = surface.Members
            .Where(member => ExemptMembers.ContainsKey(member.Display) && member.IsReferencedBy(references))
            .Select(member => member.Display).ToList();
        Assert.IsEmpty(redundant,
            "These exempt members now have a sample caller; drop the exemption: " + string.Join("; ", redundant));
    }

    [TestMethod]
    public void TheConstantList_EqualsTheLiteralFieldsTheLibraryDeclares()
    {
        var declared = ReadSurface(LibraryPath).LiteralFields.ToHashSet(StringComparer.Ordinal);

        var unlisted = declared.Except(InlinedConstants.Keys).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var lingering = InlinedConstants.Keys.Except(declared).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.IsEmpty(unlisted, "Public constants with no reason: " + string.Join("; ", unlisted));
        Assert.IsEmpty(lingering, "Listed constants the library no longer declares: " + string.Join("; ", lingering));
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
        Assert.Contains(key => references.Contains(key), openAsync.Keys, "the control needs a member the sample really calls");

        var before = FindUnreferenced(surface, references, new Dictionary<string, string>());
        var withoutOpenAsync = references.Except(openAsync.Keys).ToHashSet(StringComparer.Ordinal);
        var after = FindUnreferenced(surface, withoutOpenAsync, new Dictionary<string, string>());

        var added = after.Except(before).ToList();
        Assert.HasCount(1, added);
        Assert.AreEqual(openAsync.Display, added[0].Display);
        Assert.Contains("FileIndex.OpenAsync(", Describe(after));
    }

    /// <summary>
    ///     Negative control, part two. A fixture inside the test assembly has a compiled, uninvoked constructor
    ///     call for one library overload and nothing else. The collector, run over that fixture type's IL (the rest of the
    ///     test assembly calls every overload somewhere), must find that overload and not its sibling, which proves
    ///     overloads are told apart and that a reference absent from the IL is reported.
    /// </summary>
    [TestMethod]
    public void TheCollector_TellsOverloadsApart()
    {
        var surface = ReadSurface(LibraryPath);
        var references = CollectReferencesFromType(typeof(PublicMemberCallerTests).Assembly.Location,
            "MFTLib.Tests.CallerGateFixtureCaller");
        var called = surface.Members.Single(member => member.Display ==
            "method MFTLib.BrokerSession..ctor(System.Func`2<System.String,System.Boolean>, System.Nullable`1<System.TimeSpan>) : System.Void");
        var sibling = surface.Members.Single(member => member.Display ==
            "method MFTLib.BrokerSession..ctor() : System.Void");

        Assert.Contains(key => references.Contains(key), called.Keys, "the overload the fixture calls must be found");
        Assert.DoesNotContain(key => references.Contains(key), sibling.Keys, "the sibling overload no IL names must not be found");
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

        const string positional = "MFTLib.Tests.CallerGateFixtures.CallerGatePositionalRecord";
        const string replaced = "MFTLib.Tests.CallerGateFixtures.CallerGateReplacedPropertyRecord";
        const string copy = "MFTLib.Tests.CallerGateFixtures.CallerGateCopyRecord";
        string Report() => string.Join("; ", displays);

        // Authored members that share a name or shape with a synthesized one are kept.
        Assert.Contains(display => display.StartsWith($"property {positional}.Extra", StringComparison.Ordinal), displays, Report());
        Assert.Contains($"method {positional}.Deconstruct(System.Int32&) : System.Void", displays, Report());
        Assert.Contains($"method {positional}.ToString() : System.String", displays, Report());
        Assert.Contains($"method {positional}..ctor(System.String, System.String) : System.Void", displays, Report());
        Assert.Contains(display => display.StartsWith($"property {replaced}.Alpha", StringComparison.Ordinal), displays, Report());
        Assert.Contains($"method {copy}..ctor({copy}) : System.Void", displays, Report());

        // Every property is listed, whatever its name, type or accessors.
        const string sibling = "MFTLib.Tests.CallerGateFixtures.CallerGateCaseSiblingRecord";
        const string contract = "MFTLib.Tests.CallerGateFixtures.CallerGateAuthoredContractRecord";
        const string mixed = "MFTLib.Tests.CallerGateFixtures.CallerGateMixedAccessorRecord";
        Assert.Contains($"property {sibling}.first : System.String", displays, Report());
        Assert.Contains($"property {contract}.EqualityContract : System.Type", displays, Report());
        Assert.Contains($"property {mixed}.First : System.Int32", displays, Report());

        Assert.Contains($"property {positional}.First : System.Int32", displays, Report());
        Assert.Contains($"property {positional}.Second : System.Int32", displays, Report());
        Assert.Contains($"property {replaced}.Beta : System.Int32", displays, Report());
        Assert.Contains($"property {sibling}.First : System.Int32", displays, Report());

        // Members carrying evidence of synthesis are dropped.
        Assert.DoesNotContain($"method {positional}..ctor(System.Int32, System.Int32) : System.Void", displays, Report());
        Assert.DoesNotContain($"method {positional}.Deconstruct(System.Int32&, System.Int32&) : System.Void", displays, Report());
        Assert.DoesNotContain($"method {positional}..ctor({positional}) : System.Void", displays, Report());
        Assert.DoesNotContain($"method {copy}..ctor(System.Int32) : System.Void", displays, Report());
        foreach (var synthesized in new[] { "Equals", "GetHashCode", "PrintMembers", "<Clone>$", "op_Equality", "op_Inequality" })
        {
            Assert.DoesNotContain(display => display.StartsWith($"method {positional}.{synthesized}(", StringComparison.Ordinal), displays, Report());
        }

        Assert.DoesNotContain(display => display.Contains("EqualityContract", StringComparison.Ordinal) &&
                                                 !display.Contains(contract, StringComparison.Ordinal), displays, Report());
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
        Assert.DoesNotContain(member => member.Display.StartsWith("method MFTLib.Index.ProducerKind.ToString(", StringComparison.Ordinal), surface.Members,
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

        const string inheritable = "MFTLib.Tests.CallerGateFixtures.CallerGateInheritableRecord";
        Assert.DoesNotContain(display => display.StartsWith($"method {inheritable}..ctor({inheritable})", StringComparison.Ordinal), displays, Report());
        Assert.DoesNotContain(display => display.StartsWith($"method {inheritable}.PrintMembers(", StringComparison.Ordinal), displays, Report());
        Assert.DoesNotContain($"method {inheritable}..ctor(System.Int32) : System.Void", displays, Report());

        const string empty = "MFTLib.Tests.CallerGateFixtures.CallerGateEmptyRecord";
        Assert.Contains($"method {empty}..ctor() : System.Void", displays, Report());
        Assert.DoesNotContain(display => display.StartsWith($"method {empty}..ctor({empty})", StringComparison.Ordinal), displays, Report());

        const string authored = "MFTLib.Tests.CallerGateFixtures.CallerGateAuthoredDeconstructRecord";
        Assert.Contains($"method {authored}.Deconstruct(System.Int32&, System.Int32&) : System.Void", displays, Report());
        Assert.Contains($"method {authored}..ctor(System.Int32, System.Int32) : System.Void", displays, Report());
        Assert.Contains(display => display.StartsWith($"property {authored}.Left", StringComparison.Ordinal), displays, Report());
        Assert.Contains(display => display.StartsWith($"property {authored}.Right", StringComparison.Ordinal), displays, Report());
        Assert.DoesNotContain(display => display.StartsWith($"method {authored}..ctor({authored})", StringComparison.Ordinal), displays, Report());
    }

    /// <summary>
    ///     The exempt key names one member: overloads that differ by generic arity, indexers that differ by
    ///     parameter, and real broker constructor overloads each get their own text, so one entry never covers a sibling.
    /// </summary>
    [TestMethod]
    public void AnExemptEntry_CoversExactlyOneMember()
    {
        var fixtures = ReadSurface(typeof(PublicMemberCallerTests).Assembly.Location, ["MFTLib.Tests.CallerGateFixtures"]);
        var fixtureDisplays = fixtures.Members.Select(member => member.Display).ToList();
        Assert.HasCount(fixtureDisplays.Count, fixtureDisplays.Distinct(), string.Join("; ", fixtureDisplays));

        var surface = ReadSurface(LibraryPath);
        var exempt = surface.Members.Single(member => member.Display ==
            "method MFTLib.BrokerSession..ctor(System.Func`2<System.String,System.Boolean>, System.Nullable`1<System.TimeSpan>) : System.Void");
        var sibling = surface.Members.Single(member => member.Display ==
            "method MFTLib.BrokerSession..ctor() : System.Void");
        var unreferenced = FindUnreferenced(surface, new HashSet<string>(),
            new Dictionary<string, string> { [exempt.Display] = "control" });

        Assert.DoesNotContain(exempt, unreferenced);
        Assert.Contains(sibling, unreferenced);
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

        var failure = Assert.ThrowsExactly<AssertFailedException>(() => AssertNoOutOfScopeTypes(surface));
        Assert.Contains("MFTLib.Tests.PublicMemberCallerTests", failure.Message);
    }

    [TestMethod]
    public void TheGate_FailsOnWindowsWhenTheWatchSampleIsMissing()
    {
        var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var failure = Assert.ThrowsExactly<AssertFailedException>(() => GateSamplePaths(true, absent));
        Assert.Contains(Path.Combine(absent, WatchSample), failure.Message);
        Assert.ThrowsExactly<AssertInconclusiveException>(() => GateSamplePaths(false, absent));
    }

    [TestMethod]
    public void AMissingLibraryOutput_Fails()
    {
        var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "MFTLib.dll");

        var failure = Assert.ThrowsExactly<AssertFailedException>(() => ResolveLibraryPath(absent));
        Assert.Contains(absent, failure.Message);
        Assert.ThrowsExactly<AssertFailedException>(() => ResolveLibraryPath(null));
        Assert.AreEqual(typeof(FileIndex).Assembly.Location, ResolveLibraryPath(typeof(FileIndex).Assembly.Location));
    }
}

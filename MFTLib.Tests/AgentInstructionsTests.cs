using System.Text;
using System.Text.RegularExpressions;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class AgentInstructionsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void WholeDocument_RemainsBelowCharacterBudget()
    {
        var text = ReadInstructions();
        var characters = CountCharacters(text);
        if (characters > 14500)
        {
            TestContext.WriteLine(
                $"warning: AGENTS.md has {characters} characters, within 500 of the 15000 budget.");
        }

        Assert.IsLessThan(15000, characters,
            $"AGENTS.md has {characters} characters; it must stay below 15000. " +
            "Move narrative unchanged into linked docs pages.");
    }

    [TestMethod]
    public void EveryBlock_RemainsBelowCharacterBudget()
    {
        var blocks = Regex.Split(ReadInstructions(), @"\r?\n[\t ]*\r?\n");
        var longest = blocks.Max(CountCharacters);
        Assert.IsLessThan(3000, longest,
            $"AGENTS.md has a {longest}-character block; each must stay below 3000. " +
            "Separate concise rules with blank lines.");
    }

    [TestMethod]
    public void EveryLinkedDocsPage_Exists()
    {
        var links = Regex.Matches(ReadInstructions(), @"\]\((docs/[A-Za-z0-9_.-]+\.md)\)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.IsNotEmpty(links, "AGENTS.md links no docs pages.");
        foreach (var link in links)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "repository-docs", link);
            Assert.IsTrue(File.Exists(path), $"AGENTS.md links {link}, which does not exist.");
        }
    }

    [TestMethod]
    public void LinuxJobs_UseConfiguredImageAndRetainFileLimit()
    {
        var workflow = RepositoryFiles.Read(".gitea/workflows/test.yml");
        foreach (var name in new[] { "linux", "docs", "linux-package" })
        {
            var job = Regex.Match(workflow,
                @"(?ms)^  " + Regex.Escape(name) + @":\r?\n(?<body>.*?)(?=^  [\w-]+:\r?$|\z)");
            Assert.IsTrue(job.Success, $"Missing job {name}.");
            var container = Regex.Match(job.Groups["body"].Value,
                @"(?ms)^    container:\r?\n(?<body>(?:^      [^\r\n]*\r?\n)+)");
            Assert.IsTrue(container.Success, $"Missing container for {name}.");
            var body = container.Groups["body"].Value;
            Assert.IsTrue(Regex.IsMatch(body,
                @"(?m)^      image: \$\{\{ vars\.MFTLIB_CI_IMAGE \}\}\r?$"),
                $"{name} must consume the provisioned image variable.");
            Assert.IsTrue(Regex.IsMatch(body,
                @"(?m)^      options: --ulimit nofile=65536:65536\r?$"),
                $"{name} must retain the open-file limit.");
        }
    }

    [TestMethod]
    public void ScannerAndConsumerWorkflows_UseServerContext()
    {
        var scanner = RepositoryFiles.Read(".gitea/workflows/aislop.yml");
        Assert.IsTrue(scanner.Contains(
            "AISLOP_REPOSITORY_URL: ${{ format('{0}/schoen/aislop.git', github.server_url) }}",
            StringComparison.Ordinal));
        Assert.IsTrue(scanner.Contains("git clone \"$env:AISLOP_REPOSITORY_URL\" $aislopDir",
            StringComparison.Ordinal));
        Assert.IsTrue(scanner.Contains("git -C $aislopDir checkout \"$env:AISLOP_FORK_COMMIT\"",
            StringComparison.Ordinal));
        Assert.IsFalse(scanner.Contains("NODE_EXTRA_CA_CERTS:", StringComparison.Ordinal));
        var consumer = RepositoryFiles.Read(".gitea/workflows/sync-consumers.yml");
        Assert.IsTrue(consumer.Contains("GITEA_URL: ${{ github.server_url }}", StringComparison.Ordinal));
        Assert.IsTrue(consumer.Contains("GITEA_TOKEN: ${{ secrets.MFTLIB_SYNC_TOKEN }}", StringComparison.Ordinal));
    }

    // Assemble detector signatures so the guard can scan its own source without exemptions.
    static string[] PrivateContentPatterns() =>
    [
        @"(?i)[a-z]:[\\/]+" + @"Users(?:[\\/]|\b)",
        @"(?i)/mnt/[a-z]/" + @"Users(?:/|\b)",
        @"(?i)" + Regex.Escape(string.Join('.', "fleet", "sticktoitive", "net")),
        @"(?i)\b" + "chon" + @"kers\b",
        @"(?i)\b" + "llama" + @"box\b",
        Regex.Escape("~/" + "schoen-lab/")
    ];

    [TestMethod]
    public void TrackedFiles_ContainNoPrivateDeploymentReferences()
    {
        var paths = RepositoryFiles.Git("ls-files", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsNotEmpty(paths, "Git returned no tracked files.");
        Assert.Contains(".plan", paths, "The backlog must remain tracked.");
        var patterns = PrivateContentPatterns();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            var text = RepositoryFiles.Read(path);
            if (path.StartsWith("MFTLib/", StringComparison.Ordinal))
            {
                Assert.IsFalse(Regex.IsMatch(text, @"Func<DateTime>|=> DateTime\.UtcNow"),
                    $"{path}: block completion must use TimeProvider.");
            }

            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(text, @"(?i)no library " + "default|default-cache " + "guard"))
            {
                failures.Add($"{path}: describe explicit cache ownership using the current contract");
            }
            for (var index = 0; index < patterns.Length; index++)
            {
                if (Regex.IsMatch(text, patterns[index]))
                {
                    failures.Add($"{path}: private-content rule {index + 1}");
                }
            }
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        CollectionAssert.DoesNotContain(paths, ".maintenance.json", "Maintenance state must be local.");
        Assert.AreEqual(".maintenance.json",
            RepositoryFiles.Git("check-ignore", "--no-index", ".maintenance.json").Trim());
        CollectionAssert.DoesNotContain(paths, "TEST-REPORT.md",
            "Per-run measurements belong in coverage artifacts.");
        Assert.AreEqual("TEST-REPORT.md",
            RepositoryFiles.Git("check-ignore", "--no-index", "TEST-REPORT.md").Trim());
    }

    [TestMethod]
    public void PrivateContentPatterns_RejectFixturesAndAcceptPortableInputs()
    {
        var rejected = new[]
        {
            "C:" + @"\Users\fixture\source", "c:" + "/users/fixture/source",
            "C:" + @"\\Users\\fixture\\source", "/mnt/c/" + "Users/fixture/source",
            "https://" + string.Join('.', "gitea", "fleet", "sticktoitive", "net") + "/team/repo",
            "CHON" + "KERS", "llama" + "box", "~/" + "schoen-lab/packages/guide.md"
        };
        var patterns = PrivateContentPatterns();
        foreach (var text in rejected)
        {
            Assert.Contains(pattern => Regex.IsMatch(text, pattern), patterns,
                "A private-content negative control was not detected.");
        }

        foreach (var text in new[]
        {
            "docs/quality-gates.md", "${{ vars.MFTLIB_CI_IMAGE }}",
            "$env:MFTLIB_GITEA_REPOSITORY_URL", "https://forge.example.invalid/team/repo",
            @"C:\Data\test\file.txt", "https://github.com/mtschoen/MFTLib"
        })
        {
            Assert.DoesNotContain(pattern => Regex.IsMatch(text, pattern), patterns,
                $"Portable content was rejected: {text}");
        }
    }

    [TestMethod]
    public void SampleSharedMembers_AreUsedByBothSamples()
    {
        var paths = RepositoryFiles.Git("ls-files", "-z", "--",
                "SampleProgram.Shared", "SampleProgram.Direct", "SampleProgram.Watch")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)).ToArray();
        var sources = paths.ToDictionary(path => path,
            path => Regex.Replace(RepositoryFiles.Read(path), @"//[^\r\n]*|/\*[\s\S]*?\*/", ""));
        var shared = sources.Where(pair => pair.Key.StartsWith("SampleProgram.Shared/", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(shared, "The shared source set must not be empty.");
        var sharedText = string.Join("\n", shared.Select(pair => pair.Value));
        var failures = new List<string>();
        foreach (var name in new[]
        {
            "ElevationNeed", "RunWithElevation", "SelfElevationReason", "BrokerLaunchReason",
            "_tryRunElevated", "_acrtIobFunc", "_wFreopen", "_outputRedirectedToFile",
            "AcrtIobFuncNative", "WFreopenNative", "RedirectStdout", "OutputLogPath",
            "PrintElevatedRunIncomplete", "Integer", "Date", "DriveOpenedReporter"
        })
        {
            if (Regex.IsMatch(sharedText, @"\b" + Regex.Escape(name) + @"\b"))
            {
                failures.Add($"{name} belongs in its sample, not Shared.");
            }
        }

        // Shared uses four-space member declarations; nested types are ownership units.
        var declaration = new Regex(@"(?m)^    (?:[\w<>,?\[\].]+[ \t]+)+(?<name>\w+)[ \t]*(?:\(|=>|[=;{])");
        var members = new Dictionary<string, (string Path, string Body)>();
        foreach (var (path, text) in shared)
        {
            var declarations = declaration.Matches(text).ToArray();
            Assert.IsNotEmpty(declarations, $"No shared members found in {path}.");
            for (var index = 0; index < declarations.Length; index++)
            {
                var match = declarations[index];
                var end = index + 1 < declarations.Length ? declarations[index + 1].Index : text.Length;
                members.Add(match.Groups["name"].Value, (path, text[(match.Index + match.Length)..end]));
            }
        }

        foreach (var sample in new[] { "Direct", "Watch" })
        {
            var local = sources.Where(pair => pair.Key.StartsWith($"SampleProgram.{sample}/", StringComparison.Ordinal)).ToArray();
            Assert.IsNotEmpty(local, $"No source found for {sample}.");
            var identifiers = Regex.Matches(string.Join("\n", local.Select(pair => pair.Value)), @"\b[A-Za-z_]\w*\b")
                .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            var pending = new Queue<string>(members.Keys.Where(identifiers.Contains));
            var reached = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var name))
            {
                if (!reached.Add(name))
                {
                    continue;
                }

                foreach (Match token in Regex.Matches(members[name].Body, @"\b[A-Za-z_]\w*\b"))
                {
                    if (members.ContainsKey(token.Value))
                    {
                        pending.Enqueue(token.Value);
                    }
                }
            }

            foreach (var (name, member) in members)
            {
                if (!reached.Contains(name))
                {
                    failures.Add($"{member.Path}: {name} has no {sample} source use.");
                }
            }
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void MaintainedTools_ListEveryScript()
    {
        var paths = RepositoryFiles.Git("ls-files", "--cached", "--others",
                "--exclude-standard", "-z", "--", "scripts")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".py", ".cmd", ".bat" };
        var scripts = paths.Where(path => extensions.Contains(Path.GetExtension(path)) ||
            RepositoryFiles.Read(path).StartsWith("#!", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(scripts, "No scripts found under scripts/.");
        var required = scripts.Concat(new[]
        {
            "SampleProgram.Direct/SampleProgram.Direct.csproj",
            "SampleProgram.Watch/SampleProgram.Watch.csproj"
        }).ToArray();
        var failures = ValidateMaintainedTools(
            RepositoryFiles.Read("docs/maintained-developer-tools.md"), required,
            path => File.Exists(Path.Combine(RepositoryFiles.Root, path)));
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        Assert.Contains("(docs/maintained-developer-tools.md)", ReadInstructions());
    }

    [TestMethod]
    public void MaintainedTools_RejectInvalidInventory()
    {
        const string valid = "| `scripts/fixture.ps1` | Parse fixtures | agent | Syntax gate |\n";
        var required = new[] { "scripts/fixture.ps1" };
        Assert.IsEmpty(ValidateMaintainedTools(valid, required, _ => true));
        var cases = new[]
        {
            (Text: "", Required: required, Exists: true, Error: "Missing maintained tool"),
            (Text: valid, Required: new[] { "scripts/nested/new.sh" }, Exists: true,
                Error: "Missing maintained tool: scripts/nested/new.sh"),
            (Text: valid + valid, Required: required, Exists: true, Error: "Duplicate maintained tool"),
            (Text: valid.Replace("Parse fixtures", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid.Replace("agent", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid.Replace("Syntax gate", ""), Required: required, Exists: true,
                Error: "Incomplete maintained tool"),
            (Text: valid, Required: required, Exists: false, Error: "Missing tool path"),
            (Text: "| `scripts/fixture.ps1` | purpose | agent |\n", Required: required,
                Exists: true, Error: "Malformed maintained tool row"),
            (Text: "| `scripts/fixture.ps1 | purpose | agent | Syntax gate |\n", Required: required,
                Exists: true, Error: "Malformed maintained tool path")
        };
        foreach (var item in cases)
        {
            var failures = ValidateMaintainedTools(item.Text, item.Required, _ => item.Exists);
            Assert.Contains(message => message.Contains(item.Error, StringComparison.Ordinal), failures,
                $"Expected {item.Error}: {string.Join(';', failures)}");
        }
    }

    static string[] ValidateMaintainedTools(string text, IEnumerable<string> required,
        Func<string, bool> exists)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal)) { continue; }
            var cells = line.Trim().Split('|');
            if (cells.Length != 6)
            {
                failures.Add($"Malformed maintained tool row: {line}");
                continue;
            }
            var name = cells[1].Trim();
            if (name.Length < 3 || name[0] != '`' || name[^1] != '`')
            {
                failures.Add($"Malformed maintained tool path: {name}");
                continue;
            }
            var path = name[1..^1];
            if (!seen.Add(path)) { failures.Add($"Duplicate maintained tool: {path}"); }
            if (!exists(path)) { failures.Add($"Missing tool path: {path}"); }
            if (cells.Skip(2).Take(3).Any(string.IsNullOrWhiteSpace))
            {
                failures.Add($"Incomplete maintained tool: {path}");
            }
        }
        foreach (var path in required)
        {
            if (!seen.Contains(path)) { failures.Add($"Missing maintained tool: {path}"); }
        }
        return failures.ToArray();
    }

    static string ReadInstructions() =>
        new UTF8Encoding(false, true).GetString(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "repository-docs", "AGENTS.md")));

    static int CountCharacters(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}

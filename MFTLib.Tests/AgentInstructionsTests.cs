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

        Assert.IsTrue(characters < 15000,
            $"AGENTS.md has {characters} characters; it must stay below 15000. " +
            "Move narrative unchanged into linked docs pages.");
    }

    [TestMethod]
    public void EveryBlock_RemainsBelowCharacterBudget()
    {
        var blocks = Regex.Split(ReadInstructions(), @"\r?\n[\t ]*\r?\n");
        var longest = blocks.Max(CountCharacters);
        Assert.IsTrue(longest < 3000,
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
        Assert.IsTrue(links.Count > 0, "AGENTS.md links no docs pages.");
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
        Assert.IsTrue(paths.Length > 0, "Git returned no tracked files.");
        CollectionAssert.Contains(paths, ".plan", "The backlog must remain tracked.");
        var patterns = PrivateContentPatterns();
        var failures = new List<string>();
        foreach (var path in paths)
        {
            var text = RepositoryFiles.Read(path);
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

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        CollectionAssert.DoesNotContain(paths, ".maintenance.json", "Maintenance state must be local.");
        Assert.AreEqual(".maintenance.json",
            RepositoryFiles.Git("check-ignore", "--no-index", ".maintenance.json").Trim());
    }

    [TestMethod]
    public void TestSources_DeclareNoPerClassHangGuard()
    {
        var declaration = new Regex(
            @"\bTimeSpan\s+HangGuard\b|\bHangGuard\s*=\s*(TimeSpan\.FromSeconds|HostChannelHarness|ScriptedWatchSource)");
        var paths = RepositoryFiles.Git("ls-files", "-z", "--", "MFTLib.Tests/*.cs")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsTrue(paths.Length > 0, "Git returned no tracked test sources.");
        var offenders = paths.SelectMany(path => RepositoryFiles.Read(path)
                .Split('\n')
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => declaration.IsMatch(entry.line))
            .Select(entry => $"{entry.path}:{entry.number}")
            .ToList();
        Assert.AreEqual(0, offenders.Count,
            "Use the shared TestTimeouts.HangGuard instead of: " + string.Join(", ", offenders));
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
            Assert.IsTrue(patterns.Any(pattern => Regex.IsMatch(text, pattern)),
                "A private-content negative control was not detected.");
        }

        foreach (var text in new[]
        {
            "docs/quality-gates.md", "${{ vars.MFTLIB_CI_IMAGE }}",
            "$env:MFTLIB_GITEA_REPOSITORY_URL", "https://forge.example.invalid/team/repo",
            @"C:\Data\test\file.txt", "https://github.com/mtschoen/MFTLib"
        })
        {
            Assert.IsFalse(patterns.Any(pattern => Regex.IsMatch(text, pattern)),
                $"Portable content was rejected: {text}");
        }
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

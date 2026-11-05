using System.Text.RegularExpressions;
using Tinyprobe.Core.Parsing;

namespace Tinyprobe.Core.Tests.Docs;

/// <summary>
/// The YAML examples of the documentation must stay valid: coding agents copy them
/// (docs/llm-guide.md is printed by <c>tinyprobe guide</c>), so a stale example teaches wrong syntax.
/// </summary>
public sealed partial class DocumentationTests : IDisposable
{
    [GeneratedRegex(@"```yaml\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex YamlBlock();

    [GeneratedRegex(@"^# file: (\S+)")]
    private static partial Regex FileMarker();

    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-docs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tinyprobe.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relativePath);
    }

    private static List<string> YamlBlocks(string markdownPath) =>
        YamlBlock().Matches(File.ReadAllText(RepositoryFile(markdownPath))).Select(m => m.Groups[1].Value).ToList();

    /// <summary>Writes the blocks that start with "# file: path" to their path, so imports resolve.</summary>
    private List<string> WriteMarkedFiles(string markdownPath)
    {
        var written = new List<string>();
        foreach (var block in YamlBlocks(markdownPath))
        {
            var marker = FileMarker().Match(block);
            if (!marker.Success)
                continue;

            var path = Path.Combine(_directory, marker.Groups[1].Value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, block);
            written.Add(path);
        }
        return written;
    }

    private static bool IsWorkflow(string yaml) => Regex.IsMatch(yaml, @"^steps:", RegexOptions.Multiline);

    private static void AssertLoads(Action load, string what)
    {
        try
        {
            load();
        }
        catch (WorkflowLoadException ex)
        {
            Assert.Fail($"{what} is not valid:{Environment.NewLine}{string.Join(Environment.NewLine, ex.Errors)}");
        }
    }

    [Fact]
    public void LlmGuide_CompleteExamples_AreValid()
    {
        var files = WriteMarkedFiles("docs/llm-guide.md");

        var workflows = files.Where(f => IsWorkflow(File.ReadAllText(f))).ToList();
        var environments = files.Where(f => Path.GetFileName(f).StartsWith("env.", StringComparison.Ordinal)).ToList();

        // Guards the extraction itself: the complete example (an API file, two workflows, two env files),
        // the sequence example (a sequence, a workflow that uses it), the sequence run per item (a sequence, a
        // workflow) and the Postman migration example (an API file, one workflow).
        Assert.Equal(5, workflows.Count);
        Assert.Equal(2, environments.Count);
        Assert.Equal(11, files.Count);

        foreach (var workflow in workflows)
            AssertLoads(() => WorkflowLoader.Load(workflow), Path.GetFileName(workflow));
        foreach (var environment in environments)
            AssertLoads(() => WorkflowLoader.LoadEnvironment(environment), Path.GetFileName(environment));
    }

    [Fact]
    public void LlmGuide_StandaloneExamples_AreValid()
    {
        // Blocks without a file marker that are whole workflows (with their own endpoints) must load too.
        var standalone = YamlBlocks("docs/llm-guide.md")
            .Where(b => !FileMarker().IsMatch(b) && IsWorkflow(b) && !b.Contains("import:"))
            .ToList();

        foreach (var block in standalone)
            AssertLoads(() => WorkflowLoader.Parse(block, Path.Combine(_directory, "snippet.yaml")), "a guide snippet");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/llm-guide.md")]
    [InlineData("docs/workflow-format.md")]
    [InlineData("docs/cli.md")]
    public void EveryYamlBlock_IsValidYaml(string markdownPath)
    {
        // Fragments (a step, an expect block) are not whole workflows, but they must still be YAML:
        // e.g. `with: { id: ${id} }` is not, because `{` opens another flow mapping.
        var blocks = YamlBlocks(markdownPath);
        Assert.NotEmpty(blocks);

        foreach (var block in blocks)
        {
            try
            {
                new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(block));
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                Assert.Fail($"Invalid YAML in {markdownPath} ({ex.Start.Line}:{ex.Start.Column}: {ex.Message}):{Environment.NewLine}{block}");
            }
        }
    }

    [Theory]
    [InlineData("example", "example/environment.yaml")]
    [InlineData("docs/example", "docs/environment.yaml")]
    public void ExampleProjects_AreValid(string folder, string environment)
    {
        // Users open these folders first: every workflow, its imports and the environment must load.
        var workflows = Directory.GetFiles(RepositoryFile(Path.Combine(folder, "workflows")), "*.yaml");

        Assert.NotEmpty(workflows);
        foreach (var workflow in workflows)
            AssertLoads(() => WorkflowLoader.Load(workflow), Path.GetFileName(workflow));
        AssertLoads(() => WorkflowLoader.LoadEnvironment(RepositoryFile(environment)), environment);
        foreach (var api in Directory.GetFiles(RepositoryFile(Path.Combine(folder, "api")), "*.yaml"))
            AssertLoads(() => WorkflowLoader.LoadAny(api), Path.GetFileName(api));
    }

    [Fact]
    public void AgentPackages_ShareTheirText_AndSendAgentsToTheGuide()
    {
        // agents/: the Claude Code skill, the Cursor rule and the Copilot instructions. Only their front matter differs.
        static string Body(string text) => text.ReplaceLineEndings("\n").Split("---\n", 3)[2];
        var skill = Body(File.ReadAllText(RepositoryFile("agents/claude/skills/tinyprobe/SKILL.md")));

        Assert.Equal(skill, Body(File.ReadAllText(RepositoryFile("agents/cursor/tinyprobe.mdc"))));
        Assert.Equal(skill, Body(File.ReadAllText(RepositoryFile("agents/copilot/tinyprobe.instructions.md"))));
        Assert.Contains("`tinyprobe guide`", skill);
        Assert.Contains("tinyprobe/env.local.yaml", skill);
        Assert.Contains("\"args\": [\"mcp\"]", File.ReadAllText(RepositoryFile("agents/claude/.mcp.json")));
    }

    [Fact]
    public void Readme_StandaloneWorkflows_AreValid()
    {
        var standalone = YamlBlocks("README.md").Where(b => IsWorkflow(b) && !b.Contains("import:")).ToList();

        Assert.NotEmpty(standalone);
        foreach (var block in standalone)
            AssertLoads(() => WorkflowLoader.Parse(block, Path.Combine(_directory, "readme.yaml")), "a README workflow");
    }
}

namespace Tinyprobe.Cli.Tests;

public sealed class InitCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tinyprobe-init-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PathOf(string relative) => Path.Combine(_root, relative);
    private string Read(string relative) => File.ReadAllText(PathOf(relative));

    [Fact]
    public void EmptyFolder_GetsTheLayoutOfTheGuide()
    {
        var changes = InitCommand.Apply(_root, "http://localhost:5000");

        Assert.Equal(
            ["tinyprobe/api/", "tinyprobe/workflows/", "tinyprobe/sequences/", "tinyprobe/env.example.yaml", "tinyprobe/env.local.yaml", ".gitignore", "AGENTS.md"],
            changes.Select(c => c.Path));
        Assert.All(changes, c => Assert.True(c.Created));
        Assert.True(Directory.Exists(PathOf("tinyprobe/api")));
        Assert.True(Directory.Exists(PathOf("tinyprobe/workflows")));
        Assert.Contains("baseUrl: http://localhost:5000\n", Read("tinyprobe/env.example.yaml"));
        Assert.Contains("baseUrl: http://localhost:5000\n", Read("tinyprobe/env.local.yaml"));
        Assert.Equal("tinyprobe/env.*.yaml\n!tinyprobe/env.example.yaml\nglobals.local.json\n", Read(".gitignore"));
        Assert.StartsWith("## API tests (tinyprobe)\n", Read("AGENTS.md"));
        Assert.Contains("run `tinyprobe guide` and follow it", Read("AGENTS.md"));
        Assert.False(File.Exists(PathOf("CLAUDE.md")));
    }

    [Fact]
    public void SecondRun_ChangesNothing()
    {
        InitCommand.Apply(_root, InitCommand.DefaultBaseUrl);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

        var changes = InitCommand.Apply(_root, "http://other:1");

        Assert.All(changes, c => Assert.False(c.Created));
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText));
    }

    [Fact]
    public void ExistingFiles_AreNeverOverwritten()
    {
        Directory.CreateDirectory(PathOf("tinyprobe"));
        File.WriteAllText(PathOf("tinyprobe/env.local.yaml"), "baseUrl: http://mine\npassword: secret\n");

        var changes = InitCommand.Apply(_root, InitCommand.DefaultBaseUrl);

        Assert.False(changes.Single(c => c.Path == "tinyprobe/env.local.yaml").Created);
        Assert.Equal("baseUrl: http://mine\npassword: secret\n", Read("tinyprobe/env.local.yaml"));
    }

    [Fact]
    public void GitIgnore_GetsOnlyTheMissingLines()
    {
        File.WriteAllText(PathOf(".gitignore"), "bin/\ntinyprobe/env.*.yaml");

        var change = InitCommand.Apply(_root, InitCommand.DefaultBaseUrl).Single(c => c.Path == ".gitignore");

        Assert.Equal("2 lines added", change.Note);
        Assert.Equal("bin/\ntinyprobe/env.*.yaml\n!tinyprobe/env.example.yaml\nglobals.local.json\n", Read(".gitignore"));
    }

    [Fact]
    public void AgentsFile_GetsTheSection_UnlessItAlreadyMentionsTheGuide()
    {
        File.WriteAllText(PathOf("AGENTS.md"), "# Agents\n\nUse pnpm.\n");
        File.WriteAllText(PathOf("CLAUDE.md"), "Run `tinyprobe guide` before touching tinyprobe/.\n");

        var changes = InitCommand.Apply(_root, InitCommand.DefaultBaseUrl);

        Assert.True(changes.Single(c => c.Path == "AGENTS.md").Created);
        Assert.StartsWith("# Agents\n\nUse pnpm.\n\n## API tests (tinyprobe)\n", Read("AGENTS.md"));
        var claude = changes.Single(c => c.Path == "CLAUDE.md");
        Assert.False(claude.Created);
        Assert.Equal("already mentions tinyprobe guide", claude.Note);
        Assert.Equal("Run `tinyprobe guide` before touching tinyprobe/.\n", Read("CLAUDE.md"));
    }

    [Fact]
    public void ExistingClaudeFile_GetsTheSectionToo()
    {
        File.WriteAllText(PathOf("CLAUDE.md"), "# Project");

        InitCommand.Apply(_root, InitCommand.DefaultBaseUrl);

        Assert.StartsWith("# Project\n\n## API tests (tinyprobe)\n", Read("CLAUDE.md"));
    }
}

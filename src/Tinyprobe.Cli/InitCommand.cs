using Spectre.Console;

namespace Tinyprobe.Cli;

/// <summary>
/// <c>tinyprobe init</c>: the project layout of the guide, the .gitignore lines and an AGENTS.md section
/// that sends coding agents to <c>tinyprobe guide</c>. It never overwrites: running it twice changes nothing.
/// </summary>
internal static class InitCommand
{
    public const string DefaultBaseUrl = "http://localhost:8080";

    // globals.local.json: the global variables saved by single calls (spec 0023), tokens included, wherever it is.
    private static readonly string[] IgnoredLines = ["tinyprobe/env.*.yaml", "!tinyprobe/env.example.yaml", "globals.local.json"];

    /// <summary>The sentence agents need; a file that already contains it is left as it is.</summary>
    private const string GuideMention = "tinyprobe guide";

    private const string AgentSection = """
        ## API tests (tinyprobe)

        The API tests are tinyprobe files in `tinyprobe/`: endpoints in `api/`, workflows in `workflows/`,
        and the steps that several workflows share (a login) in `sequences/`.
        Before you write or change them, run `tinyprobe guide` and follow it. Check a file with
        `tinyprobe inspect <file>`, and run the tests with `tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml`.
        With the tinyprobe MCP server (`tinyprobe mcp`), use its tools `inspect`, `call` and `run` instead of the shell.
        Credentials go only in `tinyprobe/env.local.yaml`, which is not committed.

        """;

    private const string AgentPrompt = """
        Run `tinyprobe guide` and follow it: look at this backend's routes, authentication and seed data,
        write the tinyprobe API file and workflows in tinyprobe/, then run them until they pass.
        """;

    /// <param name="Created">True when the file or folder was created or changed, false when it was left as it was.</param>
    internal sealed record Change(string Path, bool Created, string Note = "");

    public static int Run(IAnsiConsole console, string version, string root, string baseUrl)
    {
        List<Change> changes;
        try
        {
            changes = Apply(root, baseUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot initialize {root}: {ex.Message}");
            return 2;
        }

        var unicode = console.Profile.Capabilities.Unicode;
        console.WriteLine();
        console.MarkupLine($"  [bold orange1]{(unicode ? "⚡ " : "")}tinyprobe[/] [grey]{Markup.Escape(version)}[/]");
        console.WriteLine();

        int width = changes.Max(c => c.Path.Length);
        foreach (var change in changes)
        {
            var mark = change.Created ? "[green]+[/]" : "[grey]=[/]";
            var line = change.Note.Length > 0
                ? $"{Markup.Escape(change.Path.PadRight(width))}   [grey]{Markup.Escape(change.Note)}[/]"
                : Markup.Escape(change.Path);
            console.MarkupLine($"  {mark} {line}");
        }

        console.WriteLine();
        console.MarkupLine("  Next: ask your coding agent to write the tests:");
        console.WriteLine();
        foreach (var line in AgentPrompt.TrimEnd().Split('\n'))
            console.WriteLine($"    {line}");
        console.WriteLine();
        return 0;
    }

    /// <summary>Creates what is missing under <paramref name="root"/> and lists every item, created or not.</summary>
    internal static List<Change> Apply(string root, string baseUrl)
    {
        var changes = new List<Change>
        {
            Folder(root, "tinyprobe/api/"),
            Folder(root, "tinyprobe/workflows/"),
            Folder(root, "tinyprobe/sequences/"),
            NewFile(root, "tinyprobe/env.example.yaml", EnvFile(baseUrl,
                "Committed: every variable of the workflows, with placeholder values. Real values go in env.local.yaml.")),
            NewFile(root, "tinyprobe/env.local.yaml", EnvFile(baseUrl,
                "Not committed (.gitignore): the real values for the local backend, credentials included."),
                note: "not committed: put real credentials here"),
            GitIgnore(root),
            AgentInstructions(root, "AGENTS.md", createIfMissing: true)
        };

        // Claude Code reads CLAUDE.md: add the section there too, but only to a file the project already has.
        if (File.Exists(Path.Combine(root, "CLAUDE.md")))
            changes.Add(AgentInstructions(root, "CLAUDE.md", createIfMissing: false));

        return changes;
    }

    private static string EnvFile(string baseUrl, string comment) => $"""
        # {comment}
        # Run the tests: tinyprobe run tinyprobe/workflows -e tinyprobe/env.local.yaml
        baseUrl: {baseUrl}

        """;

    private static Change Folder(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        if (Directory.Exists(path))
            return new Change(relative, Created: false);

        Directory.CreateDirectory(path);
        return new Change(relative, Created: true);
    }

    private static Change NewFile(string root, string relative, string content, string note = "")
    {
        var path = Path.Combine(root, relative);
        if (File.Exists(path))
            return new Change(relative, Created: false);

        File.WriteAllText(path, content);
        return new Change(relative, Created: true, note);
    }

    private static Change GitIgnore(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var present = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = IgnoredLines.Where(l => !present.Contains(l)).ToList();
        if (missing.Count == 0)
            return new Change(".gitignore", Created: false);

        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        File.AppendAllText(path, separator + string.Join("\n", missing) + "\n");
        return new Change(".gitignore", Created: true, $"{missing.Count} {(missing.Count == 1 ? "line" : "lines")} added");
    }

    private static Change AgentInstructions(string root, string name, bool createIfMissing)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path))
        {
            if (!createIfMissing)
                return new Change(name, Created: false);
            File.WriteAllText(path, AgentSection);
            return new Change(name, Created: true, "section \"API tests (tinyprobe)\" added");
        }

        var existing = File.ReadAllText(path);
        if (existing.Contains(GuideMention, StringComparison.Ordinal))
            return new Change(name, Created: false, $"already mentions {GuideMention}");

        var separator = existing.EndsWith("\n\n") ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
        File.AppendAllText(path, separator + AgentSection);
        return new Change(name, Created: true, "section \"API tests (tinyprobe)\" added");
    }
}

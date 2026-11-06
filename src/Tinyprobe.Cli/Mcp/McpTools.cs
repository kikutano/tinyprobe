using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Protocol;
using Tinyprobe.Core.Reporting;

namespace Tinyprobe.Cli.Mcp;

/// <summary>A tool argument is missing or has the wrong type: a tool error, shown to the agent as one sentence.</summary>
internal sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// The tools of <c>tinyprobe mcp</c>: guide, inspect, call and run. They use the same loader, engine and
/// protocol JSON as the CLI, and keep nothing between calls (decision 0003).
/// </summary>
internal sealed class McpTools(string version, Func<CookieContainer, HttpMessageHandler> createHandler)
{
    public static readonly string[] Names = ["guide", "inspect", "call", "run"];

    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonElement arguments)
    {
        try
        {
            if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
                throw new ToolArgumentException("The arguments of a tool are a JSON object.");

            return name switch
            {
                "guide" => (GuideCommand.Text, false),
                "inspect" => Inspect(arguments),
                "call" => await CallEndpointAsync(arguments),
                _ => await RunAsync(arguments)
            };
        }
        catch (ToolArgumentException ex)
        {
            return (ex.Message, true);
        }
    }

    private (string, bool) Inspect(JsonElement arguments)
    {
        var file = ExistingFile(arguments, "file");
        try
        {
            return (ProtocolJson.Inspection(version, WorkflowLoader.LoadAny(file), indented: false), false);
        }
        catch (WorkflowLoadException ex)
        {
            return (ProtocolJson.Invalid(version, file, ex.Problems, indented: false, endpoints: ex.Endpoints), true);
        }
    }

    private async Task<(string, bool)> CallEndpointAsync(JsonElement arguments)
    {
        var file = ExistingFile(arguments, "file");
        var endpoint = RequiredString(arguments, "endpoint");

        // JSON is YAML: the raw JSON of each value keeps its type through the loader that types --with.
        var with = Object(arguments, "with")
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetRawText()))
            .ToList();

        WorkflowDefinition workflow;
        Dictionary<string, JsonElement> environment;
        try
        {
            workflow = WorkflowLoader.CreateCall(WorkflowLoader.LoadAny(file), endpoint, with);
            environment = Environment(arguments);
        }
        catch (WorkflowLoadException ex)
        {
            return (ProtocolJson.Invalid(version, file, ex.Problems, indented: false), true);
        }

        var (report, _) = await ExecuteAsync(workflow, environment);
        var step = report.StepReports.FirstOrDefault();
        return step is null
            ? ($"The call of '{endpoint}' did not run.", true)
            : (ProtocolJson.McpCallResult(endpoint, step), false);
    }

    private async Task<(string, bool)> RunAsync(JsonElement arguments)
    {
        var paths = Paths(arguments);
        IReadOnlyList<string> files;
        try
        {
            files = WorkflowFiles.Find(paths);
        }
        catch (WorkflowFilesException ex)
        {
            return (ex.Message, true);
        }

        var (workflows, problems) = RunCommand.LoadSuite(files);
        var stopAfter = OptionalStep(arguments);
        var skip = SkippedSteps(arguments);
        if ((stopAfter is not null || skip.Count > 0) && problems.Count == 0)
        {
            if (workflows.Count != 1 || Directory.Exists(paths[0]))
                return (stopAfter is not null
                    ? "'to' runs one workflow file: give one path, a workflow file."
                    : "'skip' applies to one workflow file: give one path, a workflow file.", true);
            if (!WorkflowLoader.TryNarrow(workflows[0].Workflow, stopAfter, skip, out var narrowed, out var stepError))
                return (stepError, true);
            workflows[0] = (workflows[0].File, narrowed);
        }
        Dictionary<string, JsonElement> environment;
        try
        {
            environment = Environment(arguments);
        }
        catch (WorkflowLoadException ex)
        {
            problems.AddRange(ex.Problems);
            environment = [];
        }
        if (problems.Count > 0)
            return (ProtocolJson.Invalid(version, paths[0], problems, indented: false), true);

        var runs = new List<McpWorkflowRun>();
        foreach (var (_, workflow) in workflows)
        {
            var (report, elapsed) = await ExecuteAsync(workflow, environment);
            runs.Add(new McpWorkflowRun(workflow, report, WorkflowSummary.Create(workflow, report), elapsed));
        }
        return (ProtocolJson.McpRunResult(runs), false);
    }

    private async Task<(Core.Models.WorkflowReport Report, TimeSpan Elapsed)> ExecuteAsync(
        WorkflowDefinition workflow, Dictionary<string, JsonElement> environment)
    {
        // A new client and handler for each workflow, as in a suite: nothing is shared, cookies included.
        var cookies = new CookieContainer();
        using var httpClient = new HttpClient(createHandler(cookies)) { Timeout = TimeSpan.FromSeconds(workflow.Settings.Timeout) };
        // An agent connected only through MCP must not be able to upload any file the user can read.
        var engine = new WorkflowEngine(httpClient, TextWriter.Null, cookies) { RecordExchanges = true, FileRoot = System.Environment.CurrentDirectory };
        var stopwatch = Stopwatch.StartNew();
        var report = await engine.ExecuteAsync(workflow, environment);
        return (report, stopwatch.Elapsed);
    }

    /// <summary>The environment file, then <c>vars</c> over it, like <c>-e</c> and <c>--var</c>.</summary>
    private static Dictionary<string, JsonElement> Environment(JsonElement arguments)
    {
        var environment = new Dictionary<string, JsonElement>();
        if (OptionalString(arguments, "env_file") is { } envFile)
        {
            if (!File.Exists(envFile))
                throw new ToolArgumentException($"Environment file not found: {envFile}");
            foreach (var (name, value) in WorkflowLoader.LoadEnvironment(envFile))
                environment[name] = value;
        }
        foreach (var property in Object(arguments, "vars"))
            environment[property.Name] = property.Value.Clone();
        return environment;
    }

    // --- arguments -------------------------------------------------------------

    private static bool TryGet(JsonElement arguments, string name, out JsonElement value)
    {
        value = default;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null;
    }

    private static string RequiredString(JsonElement arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ToolArgumentException($"Missing argument '{name}'.");

    /// <summary>The <c>to</c> argument of <c>run</c>: a step number or a step name.</summary>
    private static string? OptionalStep(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("to", out var to)
            ? to.ValueKind switch
            {
                JsonValueKind.Number => to.GetRawText(),
                JsonValueKind.String => to.GetString(),
                JsonValueKind.Null => null,
                _ => throw new ToolArgumentException("'to' must be a step number or a step name.")
            }
            : null;

    /// <summary>The <c>skip</c> argument of <c>run</c>: step numbers or names; one alone is accepted too.</summary>
    private static List<string> SkippedSteps(JsonElement arguments)
    {
        if (!TryGet(arguments, "skip", out var value))
            return [];
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
        return items.Select(item => item.ValueKind switch
        {
            JsonValueKind.Number => item.GetRawText(),
            JsonValueKind.String when item.GetString() is { Length: > 0 } name => name,
            _ => throw new ToolArgumentException("'skip' must be a list of step numbers or step names.")
        }).ToList();
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        if (!TryGet(arguments, name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            throw new ToolArgumentException($"Argument '{name}' must be a non-empty string.");
        return text;
    }

    private static string ExistingFile(JsonElement arguments, string name)
    {
        var file = RequiredString(arguments, name);
        return File.Exists(file) ? file : throw new ToolArgumentException($"File not found: {file}");
    }

    private static IEnumerable<JsonProperty> Object(JsonElement arguments, string name)
    {
        if (!TryGet(arguments, name, out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ToolArgumentException($"Argument '{name}' must be an object, for example {{ \"id\": 42 }}.");
        return value.EnumerateObject().ToList();
    }

    /// <summary><c>paths</c> is a list; a single string is accepted too, since agents often send one.</summary>
    private static List<string> Paths(JsonElement arguments)
    {
        if (!TryGet(arguments, "paths", out var value))
            throw new ToolArgumentException("Missing argument 'paths': workflow files or folders, for example [\"tinyprobe/workflows\"].");
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single)
            return [single];
        if (value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().ToList() is { Count: > 0 } items
            && items.All(i => i.ValueKind == JsonValueKind.String && i.GetString() is { Length: > 0 }))
            return items.Select(i => i.GetString()!).ToList();
        throw new ToolArgumentException("Argument 'paths' must be a list of workflow files or folders, for example [\"tinyprobe/workflows\"].");
    }

    // --- tools/list ------------------------------------------------------------

    public static void WriteList(Utf8JsonWriter w)
    {
        w.WriteStartArray("tools");

        Tool(w, "guide", "Read the tinyprobe guide",
            "The guide for writing tinyprobe files: the YAML format of API files and workflows, the rules that are easy to get " +
            "wrong, and the procedures to test a backend or to migrate a Postman collection, an OpenAPI document or another " +
            "tool's export. Read it before writing or changing tinyprobe files.",
            readOnly: true, schema: _ => { });

        Tool(w, "inspect", "Validate a tinyprobe file",
            "Validate a tinyprobe API file or workflow without sending any request. Returns what it defines (endpoints with " +
            "their params, variables, steps), or every problem with file:line:column and a suggestion.",
            readOnly: true, schema: s => StringProperty(s, "file", "The API file or workflow, relative to the workspace."),
            required: ["file"]);

        Tool(w, "call", "Call one endpoint",
            "Send one request to an endpoint of a tinyprobe API file or workflow, and return the request and the whole response " +
            "(status, headers, body), whatever the status. Use it to see the real shape of a response before writing checks. " +
            "It remembers nothing: pass a token in 'with' or 'vars'.",
            readOnly: false, schema: s =>
            {
                StringProperty(s, "file", "The API file or workflow that defines or imports the endpoint.");
                StringProperty(s, "endpoint", "The endpoint name, as in a step's 'call'.");
                ObjectProperty(s, "with", "The endpoint's arguments, as in a step's 'with'. JSON values keep their type: 42 is a number, \"42\" a string.");
                EnvironmentProperties(s);
            },
            required: ["file", "endpoint"]);

        Tool(w, "run", "Run workflows",
            "Run tinyprobe workflows: files or folders (a folder is read recursively; API and env files in it are skipped). " +
            "Every file is validated before the first request; each workflow starts from zero, and a failing one does not stop " +
            "the others. Returns, for each workflow, whether it passed, its summary and its steps with their checks; failed " +
            "steps include the request and the response.",
            readOnly: false, schema: s =>
            {
                s.WriteStartObject("paths");
                s.WriteString("type", "array");
                s.WriteStartObject("items");
                s.WriteString("type", "string");
                s.WriteEndObject();
                s.WriteNumber("minItems", 1);
                s.WriteString("description", "Workflow files or folders, relative to the workspace, for example [\"tinyprobe/workflows\"].");
                s.WriteEndObject();
                s.WriteStartObject("to");
                s.WriteStartArray("type");
                s.WriteStringValue("integer");
                s.WriteStringValue("string");
                s.WriteEndArray();
                s.WriteString("description", "Run to here (like --to): with one workflow file, run up to this step, included, and stop. " +
                    "The step's number as inspect lists the steps (1-based), or its name. Use it to look at one step of a flow.");
                s.WriteEndObject();
                s.WriteStartObject("skip");
                s.WriteString("type", "array");
                s.WriteStartObject("items");
                s.WriteStartArray("type");
                s.WriteStringValue("integer");
                s.WriteStringValue("string");
                s.WriteEndArray();
                s.WriteEndObject();
                s.WriteString("description", "Steps to leave out of this run (like --skip), with one workflow file; the file is not changed. " +
                    "Each is a step's number as inspect lists the steps (1-based), or its name. Use it to leave out a step that deletes " +
                    "what you want to look at, or a slow one.");
                s.WriteEndObject();
                EnvironmentProperties(s);
            },
            required: ["paths"]);

        w.WriteEndArray();
    }

    private static void EnvironmentProperties(Utf8JsonWriter s)
    {
        StringProperty(s, "env_file", "The environment file, for example tinyprobe/env.local.yaml (like -e).");
        ObjectProperty(s, "vars", "Variables over the environment file (like --var), as JSON values.");
    }

    private static void Tool(Utf8JsonWriter w, string name, string title, string description, bool readOnly,
        Action<Utf8JsonWriter> schema, string[]? required = null)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("title", title);
        w.WriteString("description", description);

        w.WriteStartObject("inputSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        schema(w);
        w.WriteEndObject();
        if (required is not null)
        {
            w.WriteStartArray("required");
            foreach (var property in required)
                w.WriteStringValue(property);
            w.WriteEndArray();
        }
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();

        // call and run send requests to the backend: clients use these hints to ask the user first.
        w.WriteStartObject("annotations");
        w.WriteString("title", title);
        w.WriteBoolean("readOnlyHint", readOnly);
        w.WriteBoolean("openWorldHint", !readOnly);
        if (!readOnly)
            w.WriteBoolean("idempotentHint", false);
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static void StringProperty(Utf8JsonWriter s, string name, string description)
    {
        s.WriteStartObject(name);
        s.WriteString("type", "string");
        s.WriteString("description", description);
        s.WriteEndObject();
    }

    private static void ObjectProperty(Utf8JsonWriter s, string name, string description)
    {
        s.WriteStartObject(name);
        s.WriteString("type", "object");
        s.WriteString("description", description);
        s.WriteEndObject();
    }
}

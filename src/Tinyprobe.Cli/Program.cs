using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text;
using Tinyprobe.Cli;
using Tinyprobe.Cli.Mcp;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Reporting;
using Spectre.Console;
using YamlDotNet.Core;

const int ExitSuccess = 0;
const int ExitWorkflowFailed = 1;
const int ExitUsageError = 2;

const string Usage = """
    Usage:
      tinyprobe run <workflow.yaml | folder>... [options]
      tinyprobe call <file.yaml> <endpoint> [--with name=value]... [options]
      tinyprobe inspect <file.yaml> [--json [--source]]
      tinyprobe init [folder] [--base-url <url>]
      tinyprobe update [--check] [--force]
      tinyprobe guide       Print the guide for coding agents (LLMs) that write tinyprobe files.
      tinyprobe mcp         MCP server on stdin/stdout: the tools guide, inspect, call and run, for agents.
      tinyprobe schema      Print the JSON Schema of tinyprobe files, for editors.
      tinyprobe --help
      tinyprobe --version

    'run' runs workflows. A folder is read recursively: every YAML file with a top-level
    'steps' key is a workflow, other files are skipped. With several workflows, each one
    starts from zero, a failing one does not stop the others, and a summary comes last.
    'call' sends one request to an endpoint of an API or workflow file and prints the response.
    'inspect' validates a file and lists its endpoints, variables and steps.
    'init' creates the tinyprobe/ folder, the .gitignore lines and an AGENTS.md section for
    coding agents, in the current folder or the one given. It never overwrites a file.

    Options for 'run' and 'call':
      -e, --env <file>        Environment file (YAML) with initial variables, e.g. baseUrl, apiKey.
      --var <name=value>      Set a variable, over the environment file. Repeatable.
      --with <name=value>     ('call' only) An argument of the endpoint, like a step's 'with'. Repeatable.
      --reporter <name>       console (default) or ndjson: one JSON event per line, for tools.
      --junit <file>          ('run' only) Also write a JUnit XML report, for CI servers.
      --to <step>             ('run' only, one workflow) Run up to this step, included, and stop:
                              its number, as 'inspect' lists the steps, or its name.
      --skip <step>           ('run' only, one workflow) Leave this step out of this run, the file
                              unchanged: its number or its name, as for --to. Repeatable.
      -v, --verbose           Print variables, requests and responses while running.
      --no-color              Disable colors (also honored: the NO_COLOR environment variable).

    Values of --var and --with are typed like YAML: 42 is a number, true a boolean,
    '"42"' a string, '[a, b]' a list.

    Options for 'inspect':
      --json                  Print JSON, for tools.
      --source                With --json, also the file as written and where its blocks are, for editors.
      --no-color              Disable colors.

    Options for 'init':
      --base-url <url>        baseUrl of the env files (default: http://localhost:8080).

    Options for 'update':
      --check                 Only tell whether a newer version is available.
      --force                 Reinstall the latest version, also over a development build.

      -h, --help              Show this help.
      --version               Show the version.

    Exit codes ('run', 'call', 'inspect'):
      0  all steps passed ('call': a response was received; 'inspect': the file is valid)
      1  at least one step failed (several workflows: at least one workflow failed)
      2  invalid arguments, file not found, invalid file, or a folder without workflows
    """;

UpdateCommand.CleanupPreviousVersion();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? ExitUsageError : ExitSuccess;
}

if (args[0] is "--version")
{
    Console.WriteLine(GetVersion());
    return ExitSuccess;
}

if (args[0] == "guide")
    return GuideCommand.Run();

if (args[0] == "update")
{
    bool checkOnly = false, force = false, updateNoColor = false;
    foreach (var option in args.Skip(1))
    {
        switch (option)
        {
            case "--check": checkOnly = true; break;
            case "--force": force = true; break;
            case "--no-color": updateNoColor = true; break;
            default: return UsageError($"Unknown option '{option}'.");
        }
    }

    return await UpdateCommand.RunAsync(CreateConsole(updateNoColor), GetVersion(), checkOnly, force);
}

if (args[0] == "schema")
{
    if (args.Length > 1)
        return UsageError(args[1].StartsWith('-') ? $"Unknown option '{args[1]}' for 'schema'." : $"Unexpected argument '{args[1]}'.");
    Console.Out.Write(FormatSchema.Json());
    return ExitSuccess;
}

if (args[0] == "mcp")
{
    if (args.Length > 1)
        return UsageError(args[1].StartsWith('-') ? $"Unknown option '{args[1]}' for 'mcp'." : $"Unexpected argument '{args[1]}'.");

    // Standard output carries only MCP messages, in UTF-8 without a BOM.
    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var input = new StreamReader(Console.OpenStandardInput(), encoding);
    using var output = new StreamWriter(Console.OpenStandardOutput(), encoding);
    await new McpServer(GetVersion(), CreateHandler).RunAsync(input, output);
    return ExitSuccess;
}

if (args[0] == "init")
{
    string? root = null;
    string baseUrl = InitCommand.DefaultBaseUrl;
    bool initNoColor = false;
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--no-color":
                initNoColor = true;
                break;
            case "--base-url":
                if (i + 1 >= args.Length)
                    return UsageError("Option '--base-url' requires a value.");
                baseUrl = args[++i];
                break;
            case var option when option.StartsWith('-'):
                return UsageError($"Unknown option '{option}' for 'init'.");
            case var folder when root is null:
                root = folder;
                break;
            case var extra:
                return UsageError($"Unexpected argument '{extra}'.");
        }
    }

    root ??= ".";
    if (!Directory.Exists(root))
        return UsageError($"Folder not found: {root}");

    return InitCommand.Run(CreateConsole(initNoColor), GetVersion(), root, baseUrl);
}

if (args[0] is not ("run" or "call" or "inspect"))
{
    return UsageError($"Unknown command '{args[0]}'.");
}

var command = args[0];
var positional = new List<string>();
string? envFilePath = null;
string? junitPath = null;
string? stopAfter = null;
var skip = new List<string>();
string reporterName = "console";
bool verbose = false, noColor = false, json = false, source = false;
var variables = new List<KeyValuePair<string, string>>();
var arguments = new List<KeyValuePair<string, string>>();

for (int i = 1; i < args.Length; i++)
{
    var option = args[i];
    switch (option)
    {
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return ExitSuccess;
        case "--json" when command == "inspect":
            json = true;
            break;
        case "--source" when command == "inspect":
            source = true;
            break;
        case "--no-color":
            noColor = true;
            break;
        case "-v" or "--verbose" when command != "inspect":
            verbose = true;
            break;
        case "--junit" or "--to" or "--skip" when command == "call":
            return UsageError($"Option '{option}' is not available for 'call'.");
        case "-e" or "--env" or "--reporter" or "--var" when command != "inspect":
        case "--with" when command == "call":
        case "--junit" or "--to" or "--skip" when command == "run":
            if (i + 1 >= args.Length)
                return UsageError($"Option '{option}' requires a value.");
            var value = args[++i];
            switch (option)
            {
                case "-e" or "--env":
                    envFilePath = value;
                    break;
                case "--junit":
                    junitPath = value;
                    break;
                case "--to":
                    stopAfter = value;
                    break;
                case "--skip":
                    skip.Add(value);
                    break;
                case "--reporter":
                    if (value is not ("console" or "ndjson"))
                        return UsageError($"Unknown reporter '{value}': use console or ndjson.");
                    reporterName = value;
                    break;
                default:
                    var separator = value.IndexOf('=');
                    if (separator <= 0)
                        return UsageError($"Option '{option}' expects name=value, not '{value}'.");
                    (option == "--var" ? variables : arguments).Add(new(value[..separator], value[(separator + 1)..]));
                    break;
            }
            break;
        default:
            if (option.StartsWith('-'))
                return UsageError($"Unknown option '{option}' for '{command}'.");
            positional.Add(option);
            break;
    }
}

int expectedPositional = command == "call" ? 2 : 1;
if (positional.Count < expectedPositional)
    return UsageError(command switch
    {
        "call" when positional.Count == 1 => "Missing endpoint name.",
        "run" => "Missing workflow file.",
        _ => "Missing file."
    });
if (positional.Count > expectedPositional && command != "run")
    return UsageError($"Unexpected argument '{positional[expectedPositional]}'.");

var filePath = positional[0];
// One workflow file keeps the output and the events it always had; a folder or several files are a suite.
var suite = command == "run" && (positional.Count > 1 || Directory.Exists(filePath));
if (suite && stopAfter is not null)
    return UsageError("--to runs one workflow file, not a folder or several files.");
// A step's number means a different step in each file.
if (suite && skip.Count > 0)
    return UsageError("--skip applies to one workflow file, not a folder or several files.");
IReadOnlyList<string> suiteFiles = [];
if (suite)
{
    try
    {
        suiteFiles = WorkflowFiles.Find(positional);
    }
    catch (WorkflowFilesException ex)
    {
        return UsageError(ex.Message);
    }
}
else if (!File.Exists(filePath))
{
    return UsageError($"File not found: {filePath}");
}

if (envFilePath is not null && !File.Exists(envFilePath))
    return UsageError($"Environment file not found: {envFilePath}");

if (command == "inspect")
{
    if (source && !json)
        return UsageError("Option '--source' works with '--json'.");
    return InspectCommand.Run(CreateConsole(noColor), GetVersion(), filePath, json, source);
}

var console = CreateConsole(noColor);
IRunReporter reporter = reporterName == "ndjson"
    ? new NdjsonReporter(Console.Out, GetVersion())
    // In verbose mode the engine's log is printed, so the spinner is disabled: it would interleave with it.
    : new ConsoleReporter(console, live: !verbose) { ShowResponse = command == "call" };

WorkflowDefinition? workflow = null;
var environment = new Dictionary<string, JsonElement>();
try
{
    // Everything is validated before the first request: a typo is reported, not half-run.
    // A suite loads its workflows in RunCommand, after the environment.
    if (!suite)
    {
        workflow = command == "call"
            ? WorkflowLoader.CreateCall(WorkflowLoader.LoadAny(filePath), positional[1], arguments)
            : WorkflowLoader.Load(filePath);
        // Run to here (the steps up to the one given, that one included) and the steps left out of this run.
        if (stopAfter is not null || skip.Count > 0)
        {
            if (!WorkflowLoader.TryNarrow(workflow, stopAfter, skip, out var narrowed, out var stepError))
                return UsageError(stepError);
            workflow = narrowed;
        }
    }
    if (envFilePath is not null)
    {
        foreach (var (name, value) in WorkflowLoader.LoadEnvironment(envFilePath))
            environment[name] = value;
    }
}
catch (WorkflowLoadException ex)
{
    reporter.WriteLoadErrors(filePath, ex.Problems);
    // The CI server then shows why nothing ran, instead of a missing report.
    if (junitPath is not null)
        RunCommand.WriteJunit(junitPath, JunitReport.WriteInvalid(GetVersion(), ex.Problems));
    return ExitUsageError;
}

foreach (var (name, text) in variables)
{
    try
    {
        environment[name] = WorkflowLoader.ParseYamlValue(text);
    }
    catch (YamlException ex)
    {
        return UsageError($"Invalid value for --var {name}: {ex.Message}");
    }
}

// The ndjson stream owns stdout, so the verbose log goes to stderr there.
var log = !verbose ? TextWriter.Null : reporterName == "ndjson" ? Console.Error : Console.Out;
if (verbose)
{
    if (workflow is not null)
    {
        log.WriteLine("Workflow variables:");
        foreach (var (name, value) in workflow.Variables)
            log.WriteLine($"  {name}: {value}");
    }
    log.WriteLine("Environment variables:");
    foreach (var (name, value) in environment)
        log.WriteLine($"  {name}: {value.GetRawText()}");
}

if (suite)
{
    return await RunCommand.RunSuiteAsync(reporter, GetVersion(), filePath, suiteFiles, envFilePath, environment, log,
        recordExchanges: reporterName == "ndjson", createHandler: CreateHandler, junitPath: junitPath);
}

var cookies = new CookieContainer();
using var httpClient = new HttpClient(CreateHandler(cookies)) { Timeout = TimeSpan.FromSeconds(workflow!.Settings.Timeout) };
var engine = new WorkflowEngine(httpClient, log, cookies)
{
    RecordExchanges = reporterName == "ndjson" || command == "call"
};

reporter.WriteHeader(GetVersion(), filePath, envFilePath, workflow, environment);

var started = DateTime.UtcNow;
var stopwatch = Stopwatch.StartNew();
var report = await reporter.RunAsync(engine, workflow, environment);
var summary = WorkflowSummary.Create(workflow, report);
reporter.WriteSummary(summary, stopwatch.Elapsed);

if (junitPath is not null
    && !RunCommand.WriteJunit(junitPath, JunitReport.Write(GetVersion(), [new JunitRun(filePath, workflow, report, stopwatch.Elapsed, started)])))
    return ExitUsageError;

return summary.IsSuccess ? ExitSuccess : ExitWorkflowFailed;

static IAnsiConsole CreateConsole(bool noColor)
{
    try
    {
        // Needed on Windows for ✓ ✗ ⚡; the reporter falls back to ASCII when Unicode is unavailable.
        Console.OutputEncoding = Encoding.UTF8;
    }
    catch (IOException)
    {
    }

    var console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect
    });

    // Without a terminal (CI logs, pipes) Spectre assumes 80 columns and wraps long lines.
    if (Console.IsOutputRedirected)
        console.Profile.Width = 240;

    return console;
}

static int UsageError(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    Console.Error.WriteLine("Run 'tinyprobe --help' for usage.");
    return ExitUsageError;
}

static string GetVersion()
{
    var informational = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;

    // Strip the "+<commit sha>" suffix the SDK appends to the informational version.
    return informational?.Split('+')[0] ?? "unknown";
}

// The handler keeps the cookies of one workflow run in the engine's jar, and sends them, redirects included.
static HttpMessageHandler CreateHandler(CookieContainer cookies) => new HttpClientHandler { CookieContainer = cookies };

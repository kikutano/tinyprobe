using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Tinyprobe.Core.Parsing;

/// <summary>One problem of a file; <see cref="Line"/> is 0 when it concerns the whole file.</summary>
public sealed record LoadProblem(string File, int Line, int Column, string Message)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}:{Column}: {Message}" : $"{File}: {Message}";
}

/// <summary>A workflow, API or environment file is invalid; <see cref="Problems"/> lists every problem found.</summary>
public sealed class WorkflowLoadException(IReadOnlyList<LoadProblem> problems)
    : Exception(string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<LoadProblem> Problems { get; } = problems;

    /// <summary>The problems as <c>file:line:column: message</c>.</summary>
    public IReadOnlyList<string> Errors { get; } = problems.Select(p => p.ToString()).ToList();

    /// <summary>
    /// The endpoints of an invalid workflow whose imports all loaded (its own and the imported ones), for editors
    /// that complete endpoint names while the file is being written; null otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, EndpointDefinition>? Endpoints { get; init; }
}

/// <summary>
/// Loads workflow files (and the API files they import) and environment files.
/// The whole workflow is validated before anything runs; problems are reported as
/// <c>file:line:column: message</c>.
/// </summary>
public static class WorkflowLoader
{
    public static WorkflowDefinition Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new Loader().LoadWorkflow(File.ReadAllText(fullPath), fullPath);
    }

    /// <summary>Loads a workflow from text; <paramref name="filePath"/> names it in errors and anchors relative imports.</summary>
    public static WorkflowDefinition Parse(string yaml, string filePath) =>
        new Loader().LoadWorkflow(yaml, Path.GetFullPath(filePath));

    /// <summary>
    /// Loads a workflow, or an API file (a file without <c>steps</c>) as a workflow with no steps:
    /// what <c>tinyprobe inspect</c> and <c>tinyprobe call</c> accept.
    /// </summary>
    /// <summary>
    /// <c>run --to</c>: the workflow up to a step, that step included. <paramref name="step"/> is its number, as
    /// <c>inspect</c> lists the steps (1-based, sleeps and the steps of sequences included), or its exact name.
    /// </summary>
    public static bool TryStopAfter(WorkflowDefinition workflow, string step, out WorkflowDefinition upTo, out string error)
    {
        upTo = workflow;
        error = "";
        var names = workflow.Steps.Select(s => s is CallStep call ? call.Name : ((SleepStep)s).Name).ToList();

        int index;
        if (int.TryParse(step, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (number < 1 || number > names.Count)
            {
                error = $"--to {step}: the workflow has {names.Count} step{(names.Count == 1 ? "" : "s")}";
                return false;
            }
            index = number - 1;
        }
        else
        {
            var matches = names.Select((name, i) => (name, i)).Where(n => n.name == step).Select(n => n.i).ToList();
            if (matches.Count == 0)
            {
                error = $"--to \"{step}\": no step has this name{Loader.Suggest(step, names.Distinct())}";
                return false;
            }
            if (matches.Count > 1)
            {
                error = $"--to \"{step}\": {matches.Count} steps have this name ({string.Join(", ", matches.Select(i => i + 1))}): use the number";
                return false;
            }
            index = matches[0];
        }

        upTo = new WorkflowDefinition
        {
            Name = workflow.Name,
            Description = workflow.Description,
            SourceFile = workflow.SourceFile,
            Endpoints = workflow.Endpoints,
            Imports = workflow.Imports,
            Steps = workflow.Steps.Take(index + 1).ToList(),
            Variables = workflow.Variables,
            Settings = workflow.Settings,
            StoppedAfter = index,
            StepsInFile = workflow.Steps.Count
        };
        return true;
    }

    public static WorkflowDefinition LoadAny(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new Loader().LoadAny(File.ReadAllText(fullPath), fullPath);
    }

    /// <summary>
    /// A workflow of one step that calls <paramref name="endpoint"/> of <paramref name="source"/> with
    /// <paramref name="arguments"/> (values as YAML text, see <see cref="ParseYamlValue"/>) and accepts any status.
    /// The arguments are validated like a step's <c>with</c>.
    /// </summary>
    public static WorkflowDefinition CreateCall(
        WorkflowDefinition source, string endpoint, IReadOnlyList<KeyValuePair<string, string>> arguments) =>
        new Loader().CreateCall(source, endpoint, arguments);

    /// <summary>Loads an environment file: a flat map of variable names to values.</summary>
    public static IReadOnlyDictionary<string, JsonElement> LoadEnvironment(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new Loader().LoadEnvironment(File.ReadAllText(fullPath), fullPath);
    }

    /// <summary>
    /// A value given on the command line (<c>--var id=42</c>), typed like a YAML value: <c>42</c> is a number,
    /// <c>true</c> a boolean, <c>"42"</c> a string, <c>[a, b]</c> a list; an empty value is an empty string.
    /// Placeholders are not resolved, as in an environment file.
    /// </summary>
    public static JsonElement ParseYamlValue(string text) => TemplateRenderer.ToLiteral(Loader.ParseValueNode(text));
}

internal sealed partial class Loader
{
    // The keys of each section. FormatSchema builds `tinyprobe schema` from them: a new key needs a description there.
    internal static readonly string[] ApiKeys = ["base_url", "headers", "auth", "endpoints"];
    internal static readonly string[] WorkflowKeys = ["name", "description", "import", "variables", "settings", "steps", .. ApiKeys];
    internal static readonly string[] EndpointKeys = ["method", "path", "query", "headers", "auth", "body", "form", "multipart", "params"];
    internal static readonly string[] MultipartFileKeys = ["file", "filename", "content_type"];
    internal static readonly string[] StepKeys = ["name", "call", "use", "with", "for_each", "expect", "until", "save", "sleep"];
    internal static readonly string[] SequenceKeys = ["name", "description", "import", "params", "sequence"];
    /// <summary>The top-level keys of any tinyprobe file, for the JSON Schema: a workflow's, then those only a sequence has.</summary>
    internal static readonly string[] FileKeys = [.. WorkflowKeys, .. SequenceKeys.Except(WorkflowKeys)];
    internal static readonly string[] ForEachFileKeys = ["file"];
    internal static readonly string[] ExpectKeys = ["status", "headers", "cookies", "body", "duration"];
    internal static readonly string[] UntilKeys = ["status", "headers", "cookies", "body", "every", "timeout"];
    internal static readonly string[] SaveKeys = ["cookie"];
    internal static readonly string[] BasicKeys = ["username", "password"];
    internal static readonly string[] OAuth2Keys = ["token_url", "client_id", "client_secret", "scope", "client_auth", "form"];
    /// <summary>Keys of an auth set that are a scheme, not a header.</summary>
    internal static readonly string[] AuthSchemeKeys = ["basic", "oauth2"];
    internal static readonly string[] SettingsKeys = ["timeout", "max_retries"];

    internal static readonly Dictionary<string, Operator> Operators = new()
    {
        ["eq"] = Operator.Equal,
        ["ne"] = Operator.NotEqual,
        ["gt"] = Operator.GreaterThan,
        ["gte"] = Operator.GreaterOrEqual,
        ["lt"] = Operator.LessThan,
        ["lte"] = Operator.LessOrEqual,
        ["exists"] = Operator.Exists,
        ["contains"] = Operator.Contains,
        ["in"] = Operator.In,
        ["any"] = Operator.Any,
        ["matches"] = Operator.Matches,
        ["schema"] = Operator.Schema
    };

    internal static readonly string[] DurationOperators = ["gt", "gte", "lt", "lte"];

    [GeneratedRegex(@"^[A-Za-z]+$")]
    private static partial Regex HttpMethod();

    private readonly List<(SourceLocation Location, string Message)> _problems = new();
    private readonly SchemaLoader _schemas = new();
    private readonly List<string> _files = new();

    private sealed record FileContext(string Display, string Directory);

    // The workflow being loaded: sequences add the endpoints of their imports to it, each API file once.
    private Dictionary<string, EndpointDefinition> _endpoints = new();
    private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
    // Imports in load order, and the file each import name stands for.
    private readonly List<KeyValuePair<string?, string>> _imports = new();
    private readonly Dictionary<string, string> _importNames = new();
    private readonly List<string> _sequenceStack = new();

    public WorkflowDefinition LoadWorkflow(string text, string fullPath)
    {
        var root = Open(text, fullPath, out var file);
        if (root is null)
            throw Problems();
        return LoadWorkflow(file, root, fullPath);
    }

    /// <summary>A workflow from its parsed root: parsing is most of the cost of loading, so it is done once.</summary>
    private WorkflowDefinition LoadWorkflow(FileContext file, YamlMappingNode root, string fullPath)
    {
        if (root.Children.ContainsKey(new YamlScalarNode("sequence")))
        {
            Error(file, root, $"{file.Display} is a sequence, not a workflow: run it with `use:` from a workflow");
            throw Problems();
        }

        var entries = Collect(file, root, "workflow", WorkflowKeys);

        var endpoints = _endpoints = new Dictionary<string, EndpointDefinition>();
        ResetImports();
        var problemsBeforeImports = _problems.Count;
        if (entries.TryGetValue("import", out var imports))
            Import(file, imports);

        // An editor completes endpoint names while the workflow is still invalid: when every import loaded,
        // the problems carry the endpoints found.
        var importsLoaded = _problems.Count == problemsBeforeImports;

        // A workflow can also define its own endpoints, handy for small single-file tests.
        AddEndpoints(endpoints, ParseApi(file, entries));

        var steps = entries.TryGetValue("steps", out var stepsNode) ? ParseSteps(file, stepsNode) : [];
        if (steps.Count == 0 && _problems.Count == 0)
            Error(file, root, "the workflow has no steps");

        foreach (var step in steps.OfType<CallStep>())
            Bind(step, endpoints);

        var workflow = new WorkflowDefinition
        {
            Name = entries.TryGetValue("name", out var name) ? Scalar(file, name, "name") ?? "" : Path.GetFileNameWithoutExtension(fullPath),
            Description = entries.TryGetValue("description", out var description) ? Scalar(file, description, "description") ?? "" : "",
            SourceFile = fullPath,
            Endpoints = endpoints,
            Imports = _imports.ToList(),
            Steps = steps,
            Variables = entries.TryGetValue("variables", out var variables) ? ParseTemplates(file, variables, "variables") : new Dictionary<string, Template>(),
            Settings = entries.TryGetValue("settings", out var settings) ? ParseSettings(file, settings) : new WorkflowSettings()
        };

        ValidateGeneratedValues(endpoints.Values, steps.OfType<CallStep>(), workflow.Variables.Values);

        if (_problems.Count > 0)
            throw Problems(importsLoaded ? endpoints : null);

        return workflow;
    }

    public WorkflowDefinition LoadAny(string text, string fullPath)
    {
        var root = Open(text, fullPath, out var file);
        if (root is null)
            throw Problems();

        if (root.Children.Keys.Any(k => k is YamlScalarNode { Value: "steps" }))
            return LoadWorkflow(file, root, fullPath);
        if (root.Children.ContainsKey(new YamlScalarNode("sequence")))
            return LoadSequence(file, root, fullPath);

        var endpoints = new Dictionary<string, EndpointDefinition>();
        AddEndpoints(endpoints, ParseApi(file, Collect(file, root, "an API file", ApiKeys)));
        if (_problems.Count > 0)
            throw Problems();

        return new WorkflowDefinition
        {
            Name = Path.GetFileNameWithoutExtension(fullPath),
            SourceFile = fullPath,
            Endpoints = endpoints
        };
    }

    public WorkflowDefinition CreateCall(
        WorkflowDefinition source, string endpoint, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        var location = new SourceLocation("command line", 0, 0);
        _files.Add(location.File);

        var with = new Dictionary<string, Template>();
        foreach (var (name, text) in arguments)
        {
            try
            {
                with[name] = new Template(ParseValueNode(text), location);
            }
            catch (YamlException ex)
            {
                Report(location, $"invalid value for '{name}': {ex.Message}");
            }
        }

        var step = new CallStep { Call = endpoint, With = with, Expect = new Expectation { AnyStatus = true }, Location = location };
        Bind(step, source.Endpoints.ToDictionary());
        ValidateGeneratedValues([], [step], []);

        if (_problems.Count > 0)
            throw Problems();

        return new WorkflowDefinition
        {
            Name = source.Name,
            Description = source.Description,
            SourceFile = source.SourceFile,
            Endpoints = source.Endpoints,
            Steps = [step],
            Variables = source.Variables,
            Settings = source.Settings
        };
    }

    /// <summary>A command-line value as a YAML node; an empty value is an empty string, not null.</summary>
    internal static YamlNode ParseValueNode(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0
            ? new YamlScalarNode(text) { Style = ScalarStyle.DoubleQuoted }
            : stream.Documents[0].RootNode;
    }

    public IReadOnlyDictionary<string, JsonElement> LoadEnvironment(string text, string fullPath)
    {
        var root = Open(text, fullPath, out var file);
        var values = root is null ? new Dictionary<string, JsonElement>() : ParseLiterals(file, root, "the environment file");

        if (_problems.Count > 0)
            throw Problems();

        return values;
    }

    // --- Files ---------------------------------------------------------------

    private YamlMappingNode? Open(string text, string fullPath, out FileContext file)
    {
        file = new FileContext(DisplayPath(fullPath), Path.GetDirectoryName(fullPath)!);
        if (!_files.Contains(file.Display))
            _files.Add(file.Display);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            Report(new SourceLocation(file.Display, (int)ex.Start.Line, (int)ex.Start.Column), ex.Message);
            return null;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            Report(new SourceLocation(file.Display, 0, 0), "expected a YAML mapping at the top level");
            return null;
        }

        return root;
    }

    private void ResetImports()
    {
        _imported.Clear();
        _imports.Clear();
        _importNames.Clear();
    }

    /// <summary>
    /// The API files of <c>import</c>, relative to the file: their endpoints join the workflow's, each file once per name.
    /// An item is a path, or a <c>name: path</c> pair whose endpoints are called <c>name.endpoint</c>.
    /// </summary>
    private void Import(FileContext file, YamlNode imports)
    {
        foreach (var item in Items(file, imports, "import"))
        {
            string? importName = null;
            YamlNode pathNode = item;
            if (item is YamlMappingNode named)
            {
                if (named.Children.Count != 1 || named.Children.Keys.First() is not YamlScalarNode { Value: { } key })
                {
                    Error(file, item, "an import with a name is one `name: path` pair");
                    continue;
                }
                if (!ImportName().IsMatch(key))
                {
                    Error(file, item, $"import name '{key}' must be lowercase letters, digits and _, starting with a letter (billing)");
                    continue;
                }
                importName = key;
                pathNode = named.Children.Values.First();
            }

            if (Scalar(file, pathNode, "an import") is not { } importPath)
                continue;

            var importFullPath = Path.GetFullPath(Path.Combine(file.Directory, importPath));
            if (!File.Exists(importFullPath))
            {
                Error(file, pathNode, $"imported file not found: {importPath}");
                continue;
            }

            if (importName is not null)
            {
                if (_importNames.TryGetValue(importName, out var other) && !other.Equals(importFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    Error(file, item, $"import name '{importName}' is already used for {DisplayPath(other)}");
                    continue;
                }
                _importNames[importName] = importFullPath;
            }

            if (!_imported.Add($"{importName}|{importFullPath}"))
                continue;
            _imports.Add(new(importName, importFullPath));
            var endpoints = LoadApiFile(importFullPath);
            AddEndpoints(_endpoints, importName is null
                ? endpoints
                : endpoints.Select(e => e with { Name = $"{importName}.{e.Name}", ImportName = importName }));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ImportName();

    /// <summary><c>inspect</c> of a sequence file: its params and steps, checked against its own imports.</summary>
    private WorkflowDefinition LoadSequence(FileContext file, YamlMappingNode root, string fullPath)
    {
        var entries = Collect(file, root, "a sequence", SequenceKeys);
        _endpoints = new Dictionary<string, EndpointDefinition>();
        ResetImports();
        if (entries.TryGetValue("import", out var imports))
            Import(file, imports);

        var parameters = ParseSequenceParams(file, entries);
        _sequenceStack.Add(fullPath);
        var steps = ParseSteps(file, entries["sequence"]);
        if (steps.Count == 0 && _problems.Count == 0)
            Error(file, entries["sequence"], "the sequence has no steps");
        foreach (var step in steps.OfType<CallStep>())
            Bind(step, _endpoints);
        ValidateGeneratedValues(_endpoints.Values, steps.OfType<CallStep>(), []);

        if (_problems.Count > 0)
            throw Problems();

        return new WorkflowDefinition
        {
            Name = SequenceName(file, entries, fullPath),
            Description = entries.TryGetValue("description", out var description) ? Scalar(file, description, "description") ?? "" : "",
            SourceFile = fullPath,
            Endpoints = _endpoints,
            Imports = _imports.ToList(),
            Steps = steps,
            IsSequence = true,
            Params = parameters
        };
    }

    private string SequenceName(FileContext file, Dictionary<string, YamlNode> entries, string fullPath) =>
        entries.TryGetValue("name", out var name) && Scalar(file, name, "name") is { Length: > 0 } text
            ? text
            : Path.GetFileNameWithoutExtension(fullPath);

    private Dictionary<string, Template?> ParseSequenceParams(FileContext file, Dictionary<string, YamlNode> entries)
    {
        var parameters = new Dictionary<string, Template?>();
        if (entries.TryGetValue("params", out var paramsNode))
        {
            foreach (var (name, _, value) in Entries(file, paramsNode, "params"))
                parameters[name] = IsNull(value) ? null : Template(file, value);
        }
        return parameters;
    }

    /// <summary>
    /// <c>use: path</c>: the steps of the sequence file, parsed again for this use, so each use has its own params.
    /// They are reported at the outermost <c>use</c> step, and remember where they are written.
    /// </summary>
    private List<StepDefinition> ExpandUse(
        FileContext file, YamlNode item, YamlNode useNode, Dictionary<string, YamlNode> entries, SequenceUse? parent, SourceLocation reportedAt)
    {
        if (Scalar(file, useNode, "use") is not { Length: > 0 } path)
            return [];

        var fullPath = Path.GetFullPath(Path.Combine(file.Directory, path));
        if (!File.Exists(fullPath))
        {
            Error(file, useNode, $"sequence not found: {path}");
            return [];
        }
        if (_sequenceStack.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            var chain = _sequenceStack.SkipWhile(p => !p.Equals(fullPath, StringComparison.OrdinalIgnoreCase)).Append(fullPath).Select(Path.GetFileName);
            Error(file, useNode, $"sequence '{Path.GetFileNameWithoutExtension(fullPath)}' uses itself: {string.Join(" → ", chain)}");
            return [];
        }

        var root = Open(File.ReadAllText(fullPath), fullPath, out var sequenceFile);
        if (root is null)
            return [];
        if (!root.Children.ContainsKey(new YamlScalarNode("sequence")))
        {
            Error(file, useNode, $"{path} is not a sequence (it has no 'sequence:')");
            return [];
        }

        var sequenceEntries = Collect(sequenceFile, root, "a sequence", SequenceKeys);
        if (sequenceEntries.TryGetValue("import", out var imports))
            Import(sequenceFile, imports);
        var parameters = ParseSequenceParams(sequenceFile, sequenceEntries);
        var name = SequenceName(sequenceFile, sequenceEntries, fullPath);

        var arguments = new Dictionary<string, Template>();
        if (entries.TryGetValue("with", out var withNode))
        {
            foreach (var (key, keyNode, value) in Entries(file, withNode, "with"))
            {
                if (parameters.ContainsKey(key))
                    arguments[key] = Template(file, value);
                else
                    Error(file, keyNode, $"unknown param '{key}' for sequence '{name}'{Suggest(key, parameters.Keys)}");
            }
        }
        foreach (var (param, defaultValue) in parameters)
        {
            if (defaultValue is null && !arguments.ContainsKey(param))
            {
                var declared = sequenceEntries.TryGetValue("params", out var paramsNode) ? At(sequenceFile, paramsNode).ToString() : sequenceFile.Display;
                Error(file, item, $"missing required param '{param}' of sequence '{name}' (defined at {declared})");
            }
        }

        var use = new SequenceUse
        {
            Name = entries.TryGetValue("name", out var nameNode) && Scalar(file, nameNode, "name") is { Length: > 0 } own ? own : name,
            File = fullPath,
            Arguments = arguments,
            Params = parameters,
            Parent = parent
        };

        _sequenceStack.Add(fullPath);
        try
        {
            var steps = ParseSteps(sequenceFile, sequenceEntries["sequence"], use, reportedAt);
            if (steps.Count == 0)
                Error(sequenceFile, sequenceEntries["sequence"], "the sequence has no steps");
            return steps;
        }
        finally
        {
            _sequenceStack.RemoveAt(_sequenceStack.Count - 1);
        }
    }

    private List<EndpointDefinition> LoadApiFile(string fullPath)
    {
        var root = Open(File.ReadAllText(fullPath), fullPath, out var file);
        return root is null ? [] : ParseApi(file, Collect(file, root, "an API file", ApiKeys));
    }

    /// <summary>A valid import name from a file name: <c>billing-api.yaml</c> gives <c>billing_api</c>.</summary>
    private static string SuggestedImportName(string file)
    {
        var name = new string(Path.GetFileNameWithoutExtension(file).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return name.Length > 0 && char.IsAsciiLetter(name[0]) ? name : "api";
    }

    private void AddEndpoints(Dictionary<string, EndpointDefinition> endpoints, IEnumerable<EndpointDefinition> added)
    {
        foreach (var endpoint in added)
        {
            if (endpoints.TryGetValue(endpoint.Name, out var existing))
            {
                // Two files of one workflow with the same names: usually two services, which a named import keeps apart.
                var hint = existing.Location.File != endpoint.Location.File && endpoint.ImportName is null
                    ? $" (give one import a name: `- {SuggestedImportName(endpoint.Location.File)}: <path>`, then `call: {SuggestedImportName(endpoint.Location.File)}.{endpoint.Name}`)"
                    : "";
                Report(endpoint.Location, $"endpoint '{endpoint.Name}' is already defined at {existing.Location}{hint}");
            }
            else
                endpoints[endpoint.Name] = endpoint;
        }
    }

    // --- API definitions -----------------------------------------------------

    private List<EndpointDefinition> ParseApi(FileContext file, Dictionary<string, YamlNode> entries)
    {
        var baseUrl = entries.TryGetValue("base_url", out var baseUrlNode) && Scalar(file, baseUrlNode, "base_url") is not null
            ? Template(file, baseUrlNode)
            : null;
        var fileHeaders = entries.TryGetValue("headers", out var headersNode) ? ParseHeaders(file, headersNode, "headers") : [];

        var authSets = new Dictionary<string, AuthSet>();
        if (entries.TryGetValue("auth", out var authNode))
        {
            foreach (var (name, _, value) in Entries(file, authNode, "auth"))
                authSets[name] = ParseAuthSet(file, name, value);
        }

        var endpoints = new List<EndpointDefinition>();
        if (entries.TryGetValue("endpoints", out var endpointsNode))
        {
            foreach (var (name, keyNode, value) in Entries(file, endpointsNode, "endpoints"))
            {
                if (ParseEndpoint(file, name, keyNode, value, baseUrl, fileHeaders, authSets) is { } endpoint)
                    endpoints.Add(endpoint);
            }
        }

        return endpoints;
    }

    private EndpointDefinition? ParseEndpoint(
        FileContext file, string name, YamlNode keyNode, YamlNode node, Template? baseUrl,
        List<KeyValuePair<string, Template>> fileHeaders, Dictionary<string, AuthSet> authSets)
    {
        var what = $"endpoint '{name}'";
        var entries = Collect(file, node, what, EndpointKeys);

        var method = "GET";
        if (entries.TryGetValue("method", out var methodNode) && Scalar(file, methodNode, "method") is { } methodText)
        {
            if (HttpMethod().IsMatch(methodText))
                method = methodText.ToUpperInvariant();
            else
                Error(file, methodNode, $"invalid HTTP method '{methodText}'");
        }

        if (!entries.TryGetValue("path", out var pathNode) || Scalar(file, pathNode, "path") is null)
        {
            if (pathNode is null)
                Error(file, keyNode, $"{what} has no path");
            return null;
        }

        var query = new List<string>();
        if (entries.TryGetValue("query", out var queryNode))
        {
            foreach (var item in Items(file, queryNode, "query"))
            {
                if (Scalar(file, item, "a query parameter") is { } parameter)
                    query.Add(parameter);
            }
        }

        var used = new List<AuthSet>();
        if (entries.TryGetValue("auth", out var authNode))
        {
            var names = authNode is YamlSequenceNode ? Items(file, authNode, "auth") : [authNode];
            foreach (var item in names)
            {
                if (Scalar(file, item, "auth") is not { } authName)
                    continue;
                if (authSets.TryGetValue(authName, out var set))
                    used.Add(set);
                else
                    Error(file, item, $"unknown auth '{authName}'{Suggest(authName, authSets.Keys)}");
            }
        }

        var ownHeaders = entries.TryGetValue("headers", out var headersNode) ? ParseHeaders(file, headersNode, $"headers of {what}") : [];

        // basic and oauth2 write the Authorization header: another one would be replaced, or would replace it.
        var schemeSet = used.FirstOrDefault(set => set.HasScheme);
        var headers = new List<KeyValuePair<string, Template>>(fileHeaders);
        if (schemeSet is not null)
        {
            headers.RemoveAll(h => IsAuthorization(h.Key));
            var setting = used.Where(set => set.HasScheme || set.Headers.Any(h => IsAuthorization(h.Key))).Select(set => set.Name).ToList();
            if (setting.Count > 1)
                Error(file, authNode!, $"{what} uses auth '{setting[0]}' and '{setting[1]}', which both set Authorization: use one of them");
            foreach (var (headerName, value) in ownHeaders.Where(h => IsAuthorization(h.Key)))
                Report(value.Location, $"{what} sets {headerName}, but its auth '{schemeSet.Name}' sets its own: remove the header");
        }
        foreach (var set in used)
            headers.AddRange(set.Headers);
        headers.AddRange(ownHeaders);

        var bodyKeys = new[] { "body", "form", "multipart" }.Where(entries.ContainsKey).ToList();
        if (bodyKeys.Count > 1)
            Error(file, keyNode, $"{what} has both {bodyKeys[0]} and {bodyKeys[1]}: use one of body, form, multipart");

        var form = entries.TryGetValue("form", out var formNode) ? ParseForm(file, formNode) : null;
        var multipart = entries.TryGetValue("multipart", out var multipartNode) ? ParseMultipart(file, multipartNode) : null;

        // These bodies set their own Content-Type (multipart with its boundary); a header would be ignored.
        if (form is not null || multipart is not null)
        {
            foreach (var (headerName, value) in ownHeaders.Where(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                Report(value.Location, $"{what} sets {headerName}, but a {(form is not null ? "form" : "multipart")} body sets its own: remove the header");
        }

        Dictionary<string, Template?>? parameters = null;
        if (entries.TryGetValue("params", out var paramsNode))
        {
            parameters = new Dictionary<string, Template?>();
            foreach (var (paramName, _, value) in Entries(file, paramsNode, "params"))
                parameters[paramName] = IsNull(value) ? null : Template(file, value);
        }

        return new EndpointDefinition
        {
            Name = name,
            Method = method,
            BaseUrl = baseUrl,
            Path = Template(file, pathNode),
            Query = query,
            Headers = headers,
            BasicAuth = schemeSet?.Basic,
            OAuth2 = schemeSet?.OAuth2,
            Body = entries.TryGetValue("body", out var bodyNode) ? Template(file, bodyNode) : null,
            Form = form,
            Multipart = multipart,
            Params = parameters,
            Location = At(file, keyNode)
        };
    }

    /// <summary><c>form</c>: text fields; a list repeats the field.</summary>
    private List<BodyField> ParseForm(FileContext file, YamlNode node)
    {
        var fields = new List<BodyField>();
        foreach (var (name, _, value) in Entries(file, node, "form"))
        {
            foreach (var item in value is YamlSequenceNode sequence ? sequence.Children : [value])
            {
                if (item is YamlScalarNode)
                    fields.Add(new BodyField { Name = name, Value = Template(file, item) });
                else
                    Error(file, item, $"form field '{name}' must be a value or a list of values");
            }
        }
        return fields;
    }

    /// <summary><c>multipart</c>: text fields and files (<c>{ file: path }</c>); a list repeats the field.</summary>
    private List<BodyField> ParseMultipart(FileContext file, YamlNode node)
    {
        var fields = new List<BodyField>();
        foreach (var (name, _, value) in Entries(file, node, "multipart"))
        {
            foreach (var item in value is YamlSequenceNode sequence ? sequence.Children : [value])
            {
                switch (item)
                {
                    case YamlScalarNode:
                        fields.Add(new BodyField { Name = name, Value = Template(file, item) });
                        break;

                    case YamlMappingNode:
                        var entries = Collect(file, item, "a multipart file", MultipartFileKeys);
                        if (!entries.ContainsKey("file"))
                        {
                            Error(file, item, "a multipart file needs file: the path of the file to send");
                            break;
                        }
                        if (ScalarTemplate(file, entries, "file") is { } path)
                        {
                            fields.Add(new BodyField
                            {
                                Name = name,
                                File = path,
                                FileName = ScalarTemplate(file, entries, "filename"),
                                ContentType = ScalarTemplate(file, entries, "content_type")
                            });
                        }
                        break;

                    default:
                        Error(file, item, $"multipart field '{name}' must be a value, a file ({{ file: path }}) or a list of them");
                        break;
                }
            }
        }
        return fields;
    }

    private Template? ScalarTemplate(FileContext file, Dictionary<string, YamlNode> entries, string key) =>
        entries.TryGetValue(key, out var node) && Scalar(file, node, key) is not null ? Template(file, node) : null;

    private static bool IsAuthorization(string header) => header.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>A named auth set: headers, and at most one scheme (<c>basic</c> or <c>oauth2</c>).</summary>
    internal sealed record AuthSet(string Name, List<KeyValuePair<string, Template>> Headers, BasicAuth? Basic, OAuth2ClientCredentials? OAuth2)
    {
        public bool HasScheme => Basic is not null || OAuth2 is not null;
    }

    private AuthSet ParseAuthSet(FileContext file, string name, YamlNode node)
    {
        var headers = new List<KeyValuePair<string, Template>>();
        BasicAuth? basic = null;
        OAuth2ClientCredentials? oauth2 = null;
        var schemes = new List<string>();
        foreach (var (key, keyNode, value) in Entries(file, node, $"auth '{name}'"))
        {
            switch (key)
            {
                case "basic":
                    schemes.Add(key);
                    basic = ParseBasic(file, name, keyNode, value);
                    break;
                case "oauth2":
                    schemes.Add(key);
                    oauth2 = ParseOAuth2(file, name, keyNode, value);
                    break;
                default:
                    if (Scalar(file, value, $"header '{key}'") is not null)
                        headers.Add(new(key, Template(file, value)));
                    break;
            }
        }

        if (schemes.Count > 1)
            Error(file, node, $"auth '{name}' has basic and oauth2: use one of them");
        else if (schemes.Count == 1)
        {
            foreach (var (_, value) in headers.Where(h => IsAuthorization(h.Key)))
                Report(value.Location, $"auth '{name}' has {schemes[0]} and an Authorization header: use one of them");
        }
        return new AuthSet(name, headers, basic, oauth2);
    }

    private BasicAuth? ParseBasic(FileContext file, string set, YamlNode keyNode, YamlNode node)
    {
        var entries = Collect(file, node, "basic", BasicKeys);
        var username = ScalarTemplate(file, entries, "username");
        var password = ScalarTemplate(file, entries, "password");
        if (username is null || password is null)
        {
            if (!entries.ContainsKey("username") || !entries.ContainsKey("password"))
                Error(file, keyNode, $"auth '{set}': basic needs username and password");
            return null;
        }
        return new BasicAuth { Username = username, Password = password };
    }

    private OAuth2ClientCredentials? ParseOAuth2(FileContext file, string set, YamlNode keyNode, YamlNode node)
    {
        var entries = Collect(file, node, "oauth2", OAuth2Keys);
        var tokenUrl = ScalarTemplate(file, entries, "token_url");
        var clientId = ScalarTemplate(file, entries, "client_id");
        var clientSecret = ScalarTemplate(file, entries, "client_secret");

        Template? scope = null;
        if (entries.TryGetValue("scope", out var scopeNode))
        {
            var items = scopeNode is YamlSequenceNode sequence ? sequence.Children : [scopeNode];
            if (items.All(item => item is YamlScalarNode))
                scope = Template(file, scopeNode);
            else
                Error(file, scopeNode, "scope must be a value or a list of values");
        }

        var clientAuth = ClientAuthentication.Basic;
        if (entries.TryGetValue("client_auth", out var clientAuthNode) && Scalar(file, clientAuthNode, "client_auth") is { } clientAuthText)
        {
            if (clientAuthText == "form")
                clientAuth = ClientAuthentication.Form;
            else if (clientAuthText != "basic")
                Error(file, clientAuthNode, $"client_auth must be basic or form, not '{clientAuthText}'");
        }

        var form = entries.TryGetValue("form", out var formNode) ? ParseHeaders(file, formNode, "form of oauth2", "form field") : [];

        if (tokenUrl is null || clientId is null || clientSecret is null)
        {
            if (new[] { "token_url", "client_id", "client_secret" }.Any(k => !entries.ContainsKey(k)))
                Error(file, keyNode, $"auth '{set}': oauth2 needs token_url, client_id and client_secret");
            return null;
        }
        return new OAuth2ClientCredentials
        {
            TokenUrl = tokenUrl, ClientId = clientId, ClientSecret = clientSecret, Scope = scope, ClientAuth = clientAuth, Form = form
        };
    }

    private List<KeyValuePair<string, Template>> ParseHeaders(FileContext file, YamlNode node, string what, string item = "header")
    {
        var headers = new List<KeyValuePair<string, Template>>();
        foreach (var (name, _, value) in Entries(file, node, what))
        {
            if (Scalar(file, value, $"{item} '{name}'") is not null)
                headers.Add(new(name, Template(file, value)));
        }
        return headers;
    }

    // --- Steps ---------------------------------------------------------------

    /// <param name="sequence">The use whose sequence these steps belong to; null for a workflow's steps.</param>
    /// <param name="reportedAt">For a sequence's steps: the location of the workflow's <c>use</c> step.</param>
    private List<StepDefinition> ParseSteps(FileContext file, YamlNode node, SequenceUse? sequence = null, SourceLocation? reportedAt = null)
    {
        var steps = new List<StepDefinition>();
        foreach (var item in Items(file, node, sequence is null ? "steps" : "sequence"))
        {
            var entries = Collect(file, item, "a step", StepKeys);
            var written = At(file, item);
            var location = reportedAt ?? written;
            SourceLocation? sourceLocation = sequence is null ? null : written;

            if (entries.TryGetValue("use", out var useNode))
            {
                foreach (var key in new[] { "call", "for_each", "expect", "until", "save", "sleep" }.Where(entries.ContainsKey))
                    Error(file, item, $"a use step cannot have '{key}'");
                steps.AddRange(ExpandUse(file, item, useNode, entries, sequence, location));
                continue;
            }

            if (entries.TryGetValue("sleep", out var sleepNode))
            {
                foreach (var key in new[] { "call", "with", "for_each", "expect", "until", "save" }.Where(entries.ContainsKey))
                    Error(file, item, $"a sleep step cannot have '{key}'");

                if (Scalar(file, sleepNode, "sleep") is { } text)
                {
                    if (Durations.TryParse(text, out var duration))
                        steps.Add(new SleepStep { Duration = duration, Location = location, SourceLocation = sourceLocation, Sequence = sequence });
                    else
                        Error(file, sleepNode, $"invalid duration '{text}': use e.g. 500ms, 2s or 1m");
                }
                continue;
            }

            if (!entries.TryGetValue("call", out var callNode))
            {
                if (entries.Count > 0 || item is not YamlMappingNode)
                    Error(file, item, "a step needs 'call' (or 'use', or 'sleep')");
                continue;
            }

            if (Scalar(file, callNode, "call") is not { } call)
                continue;

            var with = new Dictionary<string, Template>();
            if (entries.TryGetValue("with", out var withNode))
            {
                foreach (var (name, _, value) in Entries(file, withNode, "with"))
                    with[name] = Template(file, value);
            }

            var save = new Dictionary<string, SaveSource>();
            if (entries.TryGetValue("save", out var saveNode))
            {
                foreach (var (name, _, value) in Entries(file, saveNode, "save"))
                {
                    if (ParseSave(file, name, value) is { } source)
                        save[name] = source;
                }
            }

            var forEach = entries.TryGetValue("for_each", out var forEachNode) ? ParseForEach(file, forEachNode, with) : null;
            if (forEach is not null && saveNode is not null)
                Error(file, saveNode, "save cannot be used with for_each: every iteration would overwrite the value");

            var expect = entries.TryGetValue("expect", out var expectNode) ? ParseExpect(file, expectNode) : new Expectation();
            if (expect.StatusPlaceholder is { } statusPlaceholder && forEachNode is null)
                Report(statusPlaceholder.Location, "status must be a code, or a placeholder in a for_each step");

            steps.Add(new CallStep
            {
                Name = (sequence?.Prefix ?? "") + (entries.TryGetValue("name", out var nameNode) ? Scalar(file, nameNode, "name") ?? call : call),
                HasExplicitName = nameNode is not null,
                Call = call,
                With = with,
                ForEach = forEach,
                Save = save,
                Expect = expect,
                Until = entries.TryGetValue("until", out var untilNode) ? ParseUntil(file, untilNode) : null,
                Location = location,
                SourceLocation = sourceLocation,
                Sequence = sequence
            });
        }

        return steps;
    }

    private static string DefaultName(CallStep step, EndpointDefinition endpoint) =>
        DefaultName(step.Call, endpoint, step.With.Where(a => a.Value.Node is YamlScalarNode).Select(a => KeyValuePair.Create(a.Key, a.Value.ToString())));

    /// <summary>
    /// An unnamed step is shown as its call plus the arguments that go into the path or query,
    /// e.g. <c>status code=200</c>. Body and header arguments are left out: they may be secrets.
    /// </summary>
    // The arguments that show in a default name are the endpoint's, the same for every step that calls it.
    private static readonly ConditionalWeakTable<EndpointDefinition, HashSet<string>> VisibleArguments = new();

    internal static string DefaultName(string call, EndpointDefinition endpoint, IEnumerable<KeyValuePair<string, string>> scalarArguments)
    {
        var visible = VisibleArguments.GetValue(endpoint, e => TemplateRenderer.Placeholders(e.Path.Node).Concat(e.Query).ToHashSet());
        var arguments = scalarArguments.Where(a => visible.Contains(a.Key)).Select(a => $"{a.Key}={a.Value}");
        var name = string.Join(' ', [call, .. arguments]);
        return name.Length <= 40 ? name : call;
    }

    /// <summary><c>for_each</c>: a list of items (mappings of arguments), a variable (<c>${cases}</c>) or a data file.</summary>
    private ForEach? ParseForEach(FileContext file, YamlNode node, Dictionary<string, Template> with)
    {
        if (node is YamlScalarNode { Value: { } text } && LonePlaceholder().Match(text) is { Success: true } variable
            && !Generators.IsGenerated(variable.Groups[1].Value.Trim()))
            return new ForEach { Variable = variable.Groups[1].Value.Trim() };

        if (node is YamlSequenceNode sequence)
            return new ForEach { Items = ParseItems(file, sequence, with) };

        if (node is YamlMappingNode)
        {
            var entries = Collect(file, node, "for_each", ForEachFileKeys);
            if (entries.TryGetValue("file", out var fileNode) && Scalar(file, fileNode, "file") is { Length: > 0 } path)
                return ReadDataFile(file, fileNode, path, with) is { } items ? new ForEach { Items = items, File = path } : null;
            // An unknown key is already reported, with its suggestion.
            if (!entries.ContainsKey("file") && ((YamlMappingNode)node).Children.Count == 0)
                Error(file, node, "for_each must be a list of items, a variable (${cases}) or { file: path }");
            return null;
        }

        Error(file, node, "for_each must be a list of items, a variable (${cases}) or { file: path }");
        return null;
    }

    private List<IReadOnlyDictionary<string, Template>> ParseItems(FileContext file, YamlSequenceNode sequence, Dictionary<string, Template> with)
    {
        var items = new List<IReadOnlyDictionary<string, Template>>();
        int position = 0;
        foreach (var itemNode in sequence.Children)
        {
            position++;
            if (itemNode is not YamlMappingNode)
            {
                Error(file, itemNode, $"item {position} of for_each must be a mapping of arguments ({{ email: x }})");
                continue;
            }

            var item = new Dictionary<string, Template>();
            foreach (var (key, keyNode, value) in Entries(file, itemNode, $"item {position} of for_each"))
            {
                if (with.ContainsKey(key))
                    Error(file, keyNode, $"item {position} of for_each and with both set '{key}': keep one");
                item[key] = Template(file, value);
            }
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// <c>for_each: { file: path }</c>, read when the workflow loads: a JSON list of objects, or a CSV file whose first
    /// line names the columns. CSV values are text. Every item is located at the <c>file</c> value, for errors.
    /// </summary>
    private List<IReadOnlyDictionary<string, Template>>? ReadDataFile(
        FileContext file, YamlNode fileNode, string path, Dictionary<string, Template> with)
    {
        var fullPath = Path.GetFullPath(Path.Combine(file.Directory, path));
        if (!File.Exists(fullPath))
        {
            Error(file, fileNode, $"cannot read the for_each file {path}: not found");
            return null;
        }

        var location = At(file, fileNode);
        List<List<KeyValuePair<string, YamlNode>>>? rows;
        try
        {
            rows = Path.GetExtension(fullPath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? DataFiles.ReadCsv(File.ReadAllText(fullPath))
                : DataFiles.ReadJson(File.ReadAllText(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException)
        {
            Error(file, fileNode, $"cannot read the for_each file {path}: {ex.Message}");
            return null;
        }
        if (rows is null)
        {
            Error(file, fileNode, $"{path} must be a list of objects");
            return null;
        }

        var items = new List<IReadOnlyDictionary<string, Template>>();
        int position = 0;
        foreach (var row in rows)
        {
            position++;
            var item = new Dictionary<string, Template>();
            foreach (var (key, value) in row)
            {
                if (with.ContainsKey(key))
                    Report(location, $"item {position} of for_each and with both set '{key}': keep one");
                item[key] = new Template(value, location);
            }
            items.Add(item);
        }
        return items;
    }

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex LonePlaceholder();

    /// <summary>A <c>save</c> value: a JSONPath of the body (<c>$.id</c>), or <c>{ cookie: name }</c>.</summary>
    private SaveSource? ParseSave(FileContext file, string name, YamlNode node)
    {
        if (node is YamlScalarNode)
        {
            if (Scalar(file, node, $"save '{name}'") is not { } path)
                return null;
            if (JsonPath.IsValid(path))
                return new SaveSource(Path: path);
            Error(file, node, $"invalid JSONPath '{path}': use e.g. $.id or $.data[0].email");
            return null;
        }

        if (node is not YamlMappingNode mapping)
        {
            Error(file, node, $"save '{name}' must be a JSONPath ($.token) or {{ cookie: name }}");
            return null;
        }

        var entries = Collect(file, mapping, $"save '{name}'", SaveKeys);
        if (!entries.TryGetValue("cookie", out var cookieNode))
        {
            // An unknown key is already reported, with its suggestion.
            if (mapping.Children.Count == 0)
                Error(file, node, $"save '{name}' must be a JSONPath ($.token) or {{ cookie: name }}");
            return null;
        }
        if (Scalar(file, cookieNode, $"cookie of save '{name}'") is not { } cookie)
            return null;
        if (cookie.Length > 0)
            return new SaveSource(Cookie: cookie);
        Error(file, cookieNode, $"save '{name}' needs the name of a cookie: {{ cookie: name }}");
        return null;
    }

    private Expectation ParseExpect(FileContext file, YamlNode node)
    {
        var entries = Collect(file, node, "expect", ExpectKeys);
        var checks = ParseChecks(file, entries, "expect");
        var duration = entries.TryGetValue("duration", out var durationNode) ? ParseDurationConditions(file, durationNode) : [];

        return new Expectation
        {
            Status = checks.Status, StatusPlaceholder = checks.StatusPlaceholder, Headers = checks.Headers, Cookies = checks.Cookies,
            Body = checks.Body, Duration = duration
        };
    }

    /// <summary><c>until</c>: the same status, header, cookie and body checks as <c>expect</c>, plus <c>every</c> and <c>timeout</c>.</summary>
    private RetryUntil? ParseUntil(FileContext file, YamlNode node)
    {
        var entries = Collect(file, node, "until", UntilKeys);
        if (!entries.Keys.Any(k => k is "status" or "headers" or "cookies" or "body"))
        {
            Error(file, node, "until needs a condition: status, headers, cookies or body");
            return null;
        }

        var every = ParseDuration(file, entries, "every", RetryUntil.DefaultEvery);
        var timeout = ParseDuration(file, entries, "timeout", RetryUntil.DefaultTimeout);
        if (every <= TimeSpan.Zero)
            Error(file, entries["every"], "every must be greater than 0");
        else if (timeout < every)
            Error(file, entries.GetValueOrDefault("timeout") ?? node, $"timeout ({Durations.Format(timeout)}) must be at least every ({Durations.Format(every)})");

        var checks = ParseChecks(file, entries, "until");
        if (checks.StatusPlaceholder is { } placeholder)
            Report(placeholder.Location, "until.status must be a code or a list of codes");
        return new RetryUntil { Checks = checks, Every = every, Timeout = timeout };
    }

    private TimeSpan ParseDuration(FileContext file, Dictionary<string, YamlNode> entries, string key, TimeSpan fallback)
    {
        if (!entries.TryGetValue(key, out var node) || Scalar(file, node, key) is not { } text)
            return fallback;
        if (Durations.TryParse(text, out var duration))
            return duration;

        Error(file, node, $"invalid duration '{text}': use e.g. 500ms, 2s or 1m");
        return fallback;
    }

    private Expectation ParseChecks(FileContext file, Dictionary<string, YamlNode> entries, string what)
    {
        // `status: 200` or `status: [200, 204]`: any of the listed codes is accepted.
        var status = new List<int>();
        Template? statusPlaceholder = null;
        if (entries.TryGetValue("status", out var statusNode)
            && statusNode is YamlScalarNode { Value: { } statusValue } && LonePlaceholder().IsMatch(statusValue))
        {
            // Allowed only in a for_each step, where each item gives its own status; checked by the step.
            statusPlaceholder = Template(file, statusNode);
        }
        else if (statusNode is not null)
        {
            var codes = statusNode is YamlSequenceNode ? Items(file, statusNode, "status").ToList() : [statusNode];
            if (codes.Count == 0)
                Error(file, statusNode, "status needs at least one code");

            foreach (var codeNode in codes)
            {
                if (Scalar(file, codeNode, "a status code") is not { } statusText)
                    continue;
                if (int.TryParse(statusText, out var code) && code is >= 100 and <= 599)
                    status.Add(code);
                else
                    Error(file, codeNode, $"invalid status code '{statusText}'");
            }
        }

        var headers = new List<Check>();
        if (entries.TryGetValue("headers", out var headersNode))
        {
            foreach (var (name, _, value) in Entries(file, headersNode, $"{what}.headers"))
                headers.Add(new Check { Subject = name, Conditions = ParseConditions(file, value, bodyValue: false) });
        }

        var cookies = new List<Check>();
        if (entries.TryGetValue("cookies", out var cookiesNode))
        {
            foreach (var (name, _, value) in Entries(file, cookiesNode, $"{what}.cookies"))
                cookies.Add(new Check { Subject = name, Conditions = ParseConditions(file, value, bodyValue: false) });
        }

        var body = new List<Check>();
        if (entries.TryGetValue("body", out var bodyNode))
        {
            foreach (var (path, keyNode, value) in Entries(file, bodyNode, $"{what}.body"))
            {
                if (JsonPath.IsValid(path))
                    body.Add(new Check { Subject = path, Conditions = ParseConditions(file, value) });
                else
                    Error(file, keyNode, $"invalid JSONPath '{path}': use e.g. $.id or $.data[0].email");
            }
        }

        return new Expectation { Status = status, StatusPlaceholder = statusPlaceholder, Headers = headers, Cookies = cookies, Body = body };
    }

    /// <summary>
    /// A plain value means equality; a mapping made only of operators (<c>{ gte: 3 }</c>) is a list of conditions.
    /// </summary>
    /// <param name="bodyValue">false for headers and cookies, whose values are text: no <c>schema</c> there.</param>
    private List<Condition> ParseConditions(FileContext file, YamlNode node, bool bodyValue = true)
    {
        if (node is YamlMappingNode mapping && mapping.Children.Count > 0)
        {
            var keys = mapping.Children.Keys.Select(k => (k as YamlScalarNode)?.Value ?? "").ToList();
            var operatorCount = keys.Count(Operators.ContainsKey);

            if (operatorCount > 0 && operatorCount < keys.Count)
            {
                Error(file, node, "cannot mix operators (gte, exists, ...) with object fields; to compare an object use eq: { ... }");
                return [];
            }

            if (operatorCount == keys.Count)
            {
                var conditions = new List<Condition>();
                foreach (var (key, value) in mapping.Children)
                {
                    var op = Operators[((YamlScalarNode)key).Value!];
                    switch (op)
                    {
                        case Operator.Exists:
                            if (value is YamlScalarNode { Value: "true" or "false" } flag)
                                conditions.Add(new Condition { Operator = op, Exists = flag.Value == "true" });
                            else
                                Error(file, value, "exists must be true or false");
                            break;

                        case Operator.In:
                            if (value is YamlSequenceNode { Children.Count: > 0 })
                                conditions.Add(new Condition { Operator = op, Value = Template(file, value) });
                            else
                                Error(file, value, "in needs a list of values, e.g. { in: [active, pending] }");
                            break;

                        case Operator.Any:
                            // Each alternative is a value (equality) or a mapping of operators (all must hold).
                            if (value is YamlSequenceNode { Children.Count: > 0 } alternatives)
                                conditions.Add(new Condition { Operator = op, Alternatives = alternatives.Children.Select(a => ParseConditions(file, a, bodyValue)).ToList() });
                            else
                                Error(file, value, "any needs a list of conditions, e.g. { any: [ { lt: 0 }, { gt: 100 } ] }");
                            break;

                        case Operator.Matches:
                            if (value is not YamlScalarNode { Value: { } pattern })
                            {
                                Error(file, value, "matches needs a regular expression as text, e.g. { matches: '^ORD-[0-9]+$' }");
                                break;
                            }
                            // A pattern with placeholders is known only at run time.
                            if (pattern.Contains("${", StringComparison.Ordinal))
                            {
                                conditions.Add(new Condition { Operator = op, Value = Template(file, value) });
                                break;
                            }
                            if (Patterns.TryCreate(pattern, out var regex, out var reason))
                                conditions.Add(new Condition { Operator = op, Value = Template(file, value), Pattern = regex });
                            else
                                Error(file, value, $"invalid regular expression '{pattern}': {reason}");
                            break;

                        case Operator.Schema:
                            if (!bodyValue)
                            {
                                Error(file, value, "schema works on body values only: headers and cookies are text, use matches");
                                break;
                            }
                            var schema = value switch
                            {
                                YamlScalarNode { Value.Length: > 0 } written => _schemas.FromFile(written.Value!, file.Directory, problem => Error(file, value, problem)),
                                YamlMappingNode => _schemas.Inline(TemplateRenderer.ToLiteral(value), file.Directory, problem => Error(file, value, problem)),
                                _ => null
                            };
                            if (value is not (YamlScalarNode { Value.Length: > 0 } or YamlMappingNode))
                                Error(file, value, "schema needs a file or a mapping, e.g. { schema: ../schemas/user.json }");
                            else if (schema is not null)
                                conditions.Add(new Condition { Operator = op, Schema = schema });
                            break;

                        default:
                            conditions.Add(new Condition { Operator = op, Value = Template(file, value) });
                            break;
                    }
                }
                return conditions;
            }
        }

        return [new Condition { Operator = Operator.Equal, Value = Template(file, node) }];
    }

    private List<Condition> ParseDurationConditions(FileContext file, YamlNode node)
    {
        var conditions = new List<Condition>();
        if (node is not YamlMappingNode mapping || mapping.Children.Count == 0)
        {
            Error(file, node, "duration needs a comparison, e.g. { lt: 500ms }");
            return conditions;
        }

        foreach (var (name, keyNode, value) in Entries(file, mapping, "expect.duration", DurationOperators))
        {
            if (Scalar(file, value, "a duration") is not { } text)
                continue;
            if (Durations.TryParse(text, out var limit))
                conditions.Add(new Condition { Operator = Operators[name], DurationValue = limit });
            else
                Error(file, value, $"invalid duration '{text}': use e.g. 500ms, 2s or 1m");
        }

        return conditions;
    }

    private void Bind(CallStep step, Dictionary<string, EndpointDefinition> endpoints)
    {
        if (!endpoints.TryGetValue(step.Call, out var endpoint))
        {
            Report(step.WrittenAt, $"unknown endpoint '{step.Call}'{Suggest(step.Call, endpoints.Keys)}");
            return;
        }

        step.Endpoint = endpoint;
        if (!step.HasExplicitName)
            step.Name = step.NamePrefix + DefaultName(step, endpoint);

        foreach (var (name, defaultValue) in endpoint.Params ?? new Dictionary<string, Template?>())
        {
            // Items of a variable are known only when the step runs.
            var fromItems = step.ForEach is { } items && (items.Variable is not null || items.Items!.All(item => item.ContainsKey(name)));
            if (defaultValue is null && !step.With.ContainsKey(name) && !fromItems)
                Report(step.WrittenAt, $"missing required param '{name}' of endpoint '{endpoint.Name}' (defined at {endpoint.Location})");
        }

        // An argument nothing reads is almost always a typo (`pagesize` for `pageSize`): it would
        // silently send nothing, and the step could still pass.
        var accepted = AcceptedArguments(step, endpoint);
        foreach (var (name, value) in step.With)
        {
            if (!accepted.Contains(name))
                Report(value.Location, $"unknown argument '{name}' for endpoint '{endpoint.Name}'{Suggest(name, accepted)}");
        }

        // An item's keys may also be read by `with` (`email: "${name}@example.com"`).
        accepted.UnionWith(step.With.Values.SelectMany(v => TemplateRenderer.Placeholders(v.Node)));
        var reported = new HashSet<string>();
        foreach (var item in step.ForEach?.Items ?? [])
        {
            foreach (var (name, value) in item)
            {
                if (!accepted.Contains(name) && reported.Add(name))
                    Report(value.Location, $"unknown argument '{name}' for endpoint '{endpoint.Name}'{Suggest(name, accepted)}");
            }
        }
    }

    /// <summary>
    /// Names a step can pass in <c>with</c>: the endpoint's params and query names, every placeholder
    /// of its base URL, path, headers (auth sets included) and body, and the placeholders of the
    /// step's own expectations.
    /// </summary>
    private static HashSet<string> AcceptedArguments(CallStep step, EndpointDefinition endpoint)
    {
        var names = EndpointTemplates(endpoint).Concat(CheckTemplates(step))
            .SelectMany(t => TemplateRenderer.Placeholders(t.Node))
            .ToHashSet();
        names.UnionWith(endpoint.Query);
        names.UnionWith(endpoint.Params?.Keys ?? []);
        return names;
    }

    /// <summary>Names a call of the endpoint can pass, sorted; generated values excluded.</summary>
    internal static List<string> EndpointArguments(EndpointDefinition endpoint)
    {
        var names = EndpointTemplates(endpoint).SelectMany(t => TemplateRenderer.Placeholders(t.Node)).ToHashSet();
        names.UnionWith(endpoint.Query);
        names.UnionWith(endpoint.Params?.Keys ?? []);
        return names.Where(n => !Generators.IsGenerated(n)).Order(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Template> EndpointTemplates(EndpointDefinition endpoint) =>
        new[] { endpoint.BaseUrl, endpoint.Path, endpoint.Body }
            .Concat(endpoint.Headers.Select(h => h.Value))
            .Concat((endpoint.Form ?? []).Concat(endpoint.Multipart ?? []).SelectMany(f => f.Templates))
            .Concat(endpoint.BasicAuth?.Templates ?? [])
            .Concat(endpoint.OAuth2?.Templates ?? [])
            .Concat(endpoint.Params?.Values ?? [])
            .OfType<Template>();

    /// <summary>Expected values of the step's <c>expect</c> and <c>until</c> checks.</summary>
    private static IEnumerable<Template> CheckTemplates(CallStep step)
    {
        var checks = step.Expect.Headers.Concat(step.Expect.Cookies).Concat(step.Expect.Body);
        if (step.Until is { } until)
            checks = checks.Concat(until.Checks.Headers).Concat(until.Checks.Cookies).Concat(until.Checks.Body);
        return checks.SelectMany(c => Values(c.Conditions)).Append(step.Expect.StatusPlaceholder).OfType<Template>();

        static IEnumerable<Template?> Values(IEnumerable<Condition> conditions) =>
            conditions.SelectMany(c => c.Alternatives.SelectMany(Values).Append(c.Value));
    }

    /// <summary>Every <c>${$...}</c> placeholder must be a known generator: a typo like <c>${$uid}</c> is reported.</summary>
    private void ValidateGeneratedValues(
        IEnumerable<EndpointDefinition> endpoints, IEnumerable<CallStep> steps, IEnumerable<Template> variables)
    {
        var templates = endpoints.SelectMany(EndpointTemplates)
            .Concat(steps.SelectMany(s => s.With.Values.Concat(CheckTemplates(s)).Concat((s.ForEach?.Items ?? []).SelectMany(i => i.Values))))
            .Concat(variables);

        foreach (var template in templates)
        {
            foreach (var name in TemplateRenderer.Placeholders(template.Node).Where(Generators.IsGenerated).Distinct())
            {
                if (Generators.Validate(name) is { } problem)
                    Report(template.Location, $"{problem}{Suggest(name, Generators.Names)}");
            }
        }
    }

    // --- Values and settings -------------------------------------------------

    private Dictionary<string, Template> ParseTemplates(FileContext file, YamlNode node, string what)
    {
        var values = new Dictionary<string, Template>();
        foreach (var (name, _, value) in Entries(file, node, what))
            values[name] = Template(file, value);
        return values;
    }

    private Dictionary<string, JsonElement> ParseLiterals(FileContext file, YamlNode node, string what)
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var (name, _, value) in Entries(file, node, what))
            values[name] = TemplateRenderer.ToLiteral(value);
        return values;
    }

    private WorkflowSettings ParseSettings(FileContext file, YamlNode node)
    {
        var entries = Collect(file, node, "settings", SettingsKeys);
        int timeout = 30, maxRetries = 0;

        if (entries.TryGetValue("timeout", out var timeoutNode) && Scalar(file, timeoutNode, "timeout") is { } timeoutText
            && !(int.TryParse(timeoutText, out timeout) && timeout > 0))
            Error(file, timeoutNode, $"timeout must be a positive number of seconds, not '{timeoutText}'");

        if (entries.TryGetValue("max_retries", out var retriesNode) && Scalar(file, retriesNode, "max_retries") is { } retriesText
            && !(int.TryParse(retriesText, out maxRetries) && maxRetries >= 0))
            Error(file, retriesNode, $"max_retries must be zero or more, not '{retriesText}'");

        return new WorkflowSettings { Timeout = Math.Max(timeout, 1), MaxRetries = Math.Max(maxRetries, 0) };
    }

    // --- YAML helpers --------------------------------------------------------

    private static SourceLocation At(FileContext file, YamlNode node) =>
        new(file.Display, (int)node.Start.Line, (int)node.Start.Column);

    private static Template Template(FileContext file, YamlNode node) => new(node, At(file, node));

    private void Error(FileContext file, YamlNode node, string message) => Report(At(file, node), message);

    private void Report(SourceLocation location, string message)
    {
        // A sequence used twice is parsed twice: its problems are reported once.
        if (!_problems.Contains((location, message)))
            _problems.Add((location, message));
    }

    /// <summary>All problems, ordered by file (in the order the files were read) and position.</summary>
    private WorkflowLoadException Problems(IReadOnlyDictionary<string, EndpointDefinition>? endpoints = null) => new(_problems
        .OrderBy(p => _files.IndexOf(p.Location.File))
        .ThenBy(p => p.Location.Line)
        .ThenBy(p => p.Location.Column)
        .Select(p => new LoadProblem(p.Location.File, p.Location.Line, p.Location.Column, p.Message))
        .ToList())
    {
        Endpoints = endpoints
    };

    /// <summary>A path relative to the current directory when the file is inside it, otherwise the full path.</summary>
    private static string DisplayPath(string fullPath)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative;
    }

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: null or "" or "~" or "null" or "Null" or "NULL" };

    /// <summary>The entries of a mapping, by key; unknown keys are reported (with a suggestion) and skipped.</summary>
    private Dictionary<string, YamlNode> Collect(FileContext file, YamlNode node, string what, string[] allowed) =>
        Entries(file, node, what, allowed).ToDictionary(e => e.Key, e => e.Value);

    private IEnumerable<(string Key, YamlNode KeyNode, YamlNode Value)> Entries(
        FileContext file, YamlNode node, string what, string[]? allowed = null)
    {
        if (node is not YamlMappingNode mapping)
        {
            if (!IsNull(node))
                Error(file, node, $"{what} must be a mapping (key: value)");
            yield break;
        }

        foreach (var (keyNode, value) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                Error(file, keyNode, $"keys of {what} must be text");
                continue;
            }

            if (allowed is not null && !allowed.Contains(key))
            {
                Error(file, keyNode, $"unknown key '{key}' in {what}{Suggest(key, allowed)}");
                continue;
            }

            yield return (key, keyNode, value);
        }
    }

    private IEnumerable<YamlNode> Items(FileContext file, YamlNode node, string what)
    {
        if (node is YamlSequenceNode sequence)
            return sequence.Children;

        if (!IsNull(node))
            Error(file, node, $"{what} must be a list");
        return [];
    }

    private string? Scalar(FileContext file, YamlNode node, string what)
    {
        if (node is YamlScalarNode scalar)
            return scalar.Value ?? string.Empty;

        Error(file, node, $"{what} must be a single value");
        return null;
    }

    /// <summary>" (did you mean 'x'?)" for the closest candidate, or nothing when none is close.</summary>
    internal static string Suggest(string value, IEnumerable<string> candidates)
    {
        var best = candidates
            .Select(c => (Candidate: c, Distance: Distance(value.ToLowerInvariant(), c.ToLowerInvariant())))
            .OrderBy(c => c.Distance)
            .FirstOrDefault();

        return best.Candidate is not null && best.Distance <= Math.Max(2, value.Length / 3)
            ? $" (did you mean '{best.Candidate}'?)"
            : string.Empty;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[b.Length];
    }
}

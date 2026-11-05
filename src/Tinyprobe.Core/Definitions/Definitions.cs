using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Tinyprobe.Core.Definitions;

/// <summary>Where something is defined, for error messages: <c>file:line:column</c>.</summary>
public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

/// <summary>
/// A YAML value that may contain <c>${name}</c> placeholders, resolved when the step runs.
/// </summary>
public sealed class Template
{
    internal Template(YamlNode node, SourceLocation location)
    {
        Node = node;
        Location = location;
    }

    internal YamlNode Node { get; }

    public SourceLocation Location { get; }

    public override string ToString() => Node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : Node.ToString();
}

/// <summary>A workflow ready to run: its steps, with every <c>call</c> bound to an endpoint.</summary>
public sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, EndpointDefinition> Endpoints { get; init; } = new Dictionary<string, EndpointDefinition>();
    /// <summary>The imported API files, with the import's name or null, in the order they were loaded (sequences' included).</summary>
    public IReadOnlyList<KeyValuePair<string?, string>> Imports { get; init; } = [];
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
    /// <summary>
    /// Default variables; the environment file overrides them. Each one is resolved once per run,
    /// the first time it is used, so a generated value (<c>${$uuid}</c>) stays the same in every step.
    /// </summary>
    public IReadOnlyDictionary<string, Template> Variables { get; init; } = new Dictionary<string, Template>();
    public WorkflowSettings Settings { get; init; } = new();
    /// <summary>
    /// <c>run --to</c>: the index of the last step that runs (<see cref="Steps"/> ends there); null for a whole run.
    /// </summary>
    public int? StoppedAfter { get; init; }
    /// <summary>How many steps the workflow has in its file, the steps after <see cref="StoppedAfter"/> included.</summary>
    public int StepsInFile { get; init; }
    /// <summary>The file is a sequence (<c>sequence:</c>), loaded on its own by <c>inspect</c>; it cannot run.</summary>
    public bool IsSequence { get; init; }
    /// <summary>A sequence's declared params, with their default (null = required).</summary>
    public IReadOnlyDictionary<string, Template?>? Params { get; init; }
}

public sealed class WorkflowSettings
{
    /// <summary>Timeout of each HTTP request, in seconds.</summary>
    public int Timeout { get; init; } = 30;
    /// <summary>Retries on network errors and timeouts; HTTP error status codes are never retried.</summary>
    public int MaxRetries { get; init; }
}

public sealed record EndpointDefinition
{
    /// <summary>The name a step calls: <c>login</c>, or <c>billing.login</c> from an import named <c>billing</c>.</summary>
    public required string Name { get; init; }
    /// <summary>The name of the import the endpoint comes from (<c>- billing: ../api/billing.yaml</c>), or null.</summary>
    public string? ImportName { get; init; }
    public string Method { get; init; } = "GET";
    public Template? BaseUrl { get; init; }
    public required Template Path { get; init; }
    /// <summary>Query parameters, sent only when the step passes them (or a param default exists).</summary>
    public IReadOnlyList<string> Query { get; init; } = [];
    /// <summary>Headers in the order they apply: file headers, auth sets, endpoint headers. Later ones win.</summary>
    public IReadOnlyList<KeyValuePair<string, Template>> Headers { get; init; } = [];
    public Template? Body { get; init; }
    /// <summary><c>form</c>: an <c>application/x-www-form-urlencoded</c> body, the fields in order. At most one of body, form, multipart.</summary>
    public IReadOnlyList<BodyField>? Form { get; init; }
    /// <summary><c>multipart</c>: a <c>multipart/form-data</c> body, the fields in order.</summary>
    public IReadOnlyList<BodyField>? Multipart { get; init; }
    /// <summary>From an auth set with <c>basic</c>: the endpoint sends <c>Authorization: Basic …</c>.</summary>
    public BasicAuth? BasicAuth { get; init; }
    /// <summary>From an auth set with <c>oauth2</c>: the endpoint sends <c>Authorization: Bearer</c> with a token from the token URL.</summary>
    public OAuth2ClientCredentials? OAuth2 { get; init; }
    /// <summary>Declared params with their default (null = required), or null when the endpoint declares none.</summary>
    public IReadOnlyDictionary<string, Template?>? Params { get; init; }
    public required SourceLocation Location { get; init; }
}

/// <summary>
/// One field of a <c>form</c> or <c>multipart</c> body: a text <see cref="Value"/>, or a <see cref="File"/> to send.
/// A list in YAML is already one field per item, under the same name.
/// </summary>
public sealed class BodyField
{
    public required string Name { get; init; }
    public Template? Value { get; init; }
    /// <summary>The path of the file, relative to the file tinyprobe was given (the workflow, or the API file of a call).</summary>
    public Template? File { get; init; }
    public Template? FileName { get; init; }
    public Template? ContentType { get; init; }

    internal IEnumerable<Template> Templates => new[] { Value, File, FileName, ContentType }.OfType<Template>();
}

/// <summary><c>basic</c> in an auth set: <c>Authorization: Basic</c> with the base64 of <c>username:password</c>.</summary>
public sealed class BasicAuth
{
    public required Template Username { get; init; }
    public required Template Password { get; init; }

    internal IEnumerable<Template> Templates => [Username, Password];
}

/// <summary>How an OAuth2 client sends its id and secret to the token URL.</summary>
public enum ClientAuthentication
{
    /// <summary>An <c>Authorization: Basic</c> header (RFC 6749 section 2.3.1).</summary>
    Basic,
    /// <summary><c>client_id</c> and <c>client_secret</c> fields of the form body.</summary>
    Form
}

/// <summary><c>oauth2</c> in an auth set: a token from the client credentials grant, reused for the run until it expires.</summary>
public sealed class OAuth2ClientCredentials
{
    public required Template TokenUrl { get; init; }
    public required Template ClientId { get; init; }
    public required Template ClientSecret { get; init; }
    /// <summary>A value, or a list joined with spaces.</summary>
    public Template? Scope { get; init; }
    public ClientAuthentication ClientAuth { get; init; } = ClientAuthentication.Basic;
    /// <summary>Other fields of the token request, such as <c>audience</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, Template>> Form { get; init; } = [];

    internal IEnumerable<Template> Templates =>
        new[] { TokenUrl, ClientId, ClientSecret, Scope }.OfType<Template>().Concat(Form.Select(f => f.Value));
}

public abstract class StepDefinition
{
    /// <summary>Where the step is reported: for a step of a sequence, the <c>use</c> step of the workflow.</summary>
    public required SourceLocation Location { get; init; }
    /// <summary>Where a step of a sequence is written, in the sequence file; null for a step of the workflow.</summary>
    public SourceLocation? SourceLocation { get; init; }
    /// <summary>The <c>use</c> that brought the step into the workflow; null for a step of the workflow.</summary>
    public SequenceUse? Sequence { get; init; }

    /// <summary>Where the step is written: errors about it point there.</summary>
    internal SourceLocation WrittenAt => SourceLocation ?? Location;

    /// <summary><c>Log in › </c> for a step of a sequence, nested sequences included.</summary>
    internal string NamePrefix => Sequence?.Prefix ?? string.Empty;
}

/// <summary>
/// One <c>use</c> of a sequence: its steps are expanded in place of the <c>use</c> step, and see its params
/// (<see cref="Arguments"/>, then the <see cref="Params"/> defaults) before the workflow's variables.
/// </summary>
public sealed class SequenceUse
{
    /// <summary>The sequence's name, or the <c>use</c> step's own <c>name</c>.</summary>
    public required string Name { get; init; }
    public required string File { get; init; }
    public IReadOnlyDictionary<string, Template> Arguments { get; init; } = new Dictionary<string, Template>();
    public IReadOnlyDictionary<string, Template?> Params { get; init; } = new Dictionary<string, Template?>();
    /// <summary>The <c>use</c> this one is written in, for a nested sequence.</summary>
    public SequenceUse? Parent { get; init; }
    /// <summary>With <c>for_each</c> on the <c>use</c> step: this item (1-based) and how many there are (spec 0020).</summary>
    public int? Item { get; init; }
    public int? Items { get; init; }

    internal string Prefix => $"{Parent?.Prefix}{Name}{(Item is { } item ? $" [{item}/{Items}]" : "")} › ";
}

public sealed class SleepStep : StepDefinition
{
    public required TimeSpan Duration { get; init; }

    /// <summary>How the step is shown: <c>sleep 500ms</c>.</summary>
    public string Name => $"{NamePrefix}sleep {Execution.Durations.Format(Duration)}";
}

public sealed class CallStep : StepDefinition
{
    /// <summary>The step's <c>name</c>, or a default made of the call and its path/query arguments.</summary>
    public string Name { get; internal set; } = string.Empty;
    internal bool HasExplicitName { get; init; }
    public required string Call { get; init; }
    public IReadOnlyDictionary<string, Template> With { get; init; } = new Dictionary<string, Template>();
    public Expectation Expect { get; init; } = new();
    /// <summary>When set, the call is repeated until these checks pass (polling), then <see cref="Expect"/> is evaluated.</summary>
    public RetryUntil? Until { get; init; }
    /// <summary>When set, the step runs once per item, with the item's keys as arguments.</summary>
    public ForEach? ForEach { get; init; }
    /// <summary>Variable name to where its value is read: a JSONPath of the response body, or a cookie.</summary>
    public IReadOnlyDictionary<string, SaveSource> Save { get; init; } = new Dictionary<string, SaveSource>();
    /// <summary>The endpoint named by <see cref="Call"/>, bound when the workflow is loaded.</summary>
    public EndpointDefinition Endpoint { get; internal set; } = null!;
}

/// <summary>Where a <c>save</c> value is read: a JSONPath of the response body, or (<c>{ cookie: name }</c>) a cookie in the jar.</summary>
public sealed record SaveSource(string? Path = null, string? Cookie = null)
{
    /// <summary>How it is shown, in <c>inspect --json</c> too: <c>$.id</c> or <c>cookie XSRF-TOKEN</c>.</summary>
    public override string ToString() => Cookie is null ? Path ?? string.Empty : $"cookie {Cookie}";
}

/// <summary>
/// <c>for_each</c>: the items of a step, each one a call whose arguments are the item's keys plus <c>with</c>.
/// Either <see cref="Items"/> (written in the workflow, or read from a data file when it loads) or <see cref="Variable"/>.
/// </summary>
public sealed class ForEach
{
    public IReadOnlyList<IReadOnlyDictionary<string, Template>>? Items { get; init; }
    /// <summary><c>for_each: ${name}</c>: the variable's value when the step runs, a list of objects.</summary>
    public string? Variable { get; init; }
    /// <summary>The data file of <see cref="Items"/>, as written (<c>{ file: ../data/roles.csv }</c>).</summary>
    public string? File { get; init; }
}

/// <summary><c>until</c>: repeat a call every <see cref="Every"/> until <see cref="Checks"/> pass, for at most <see cref="Timeout"/>.</summary>
public sealed class RetryUntil
{
    public static readonly TimeSpan DefaultEvery = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Status, header, cookie and body checks; duration checks are not used.</summary>
    public Expectation Checks { get; init; } = new();
    public TimeSpan Every { get; init; } = DefaultEvery;
    public TimeSpan Timeout { get; init; } = DefaultTimeout;
}

public sealed class Expectation
{
    /// <summary>Accepted status codes; when empty any 2xx is accepted.</summary>
    public IReadOnlyList<int> Status { get; init; } = [];
    /// <summary><c>status: ${status}</c> in a <c>for_each</c> step: the codes come from each item.</summary>
    public Template? StatusPlaceholder { get; init; }
    /// <summary>Accept any status, also 4xx and 5xx: set by <c>tinyprobe call</c>, which explores an endpoint.</summary>
    public bool AnyStatus { get; init; }
    public IReadOnlyList<Check> Headers { get; init; } = [];
    /// <summary>Cookies by name, read from the jar after the response, for the request's URL.</summary>
    public IReadOnlyList<Check> Cookies { get; init; } = [];
    public IReadOnlyList<Check> Body { get; init; } = [];
    public IReadOnlyList<Condition> Duration { get; init; } = [];
}

/// <summary>Conditions on one header or cookie (by name) or one body value (by JSONPath).</summary>
public sealed class Check
{
    public required string Subject { get; init; }
    public required IReadOnlyList<Condition> Conditions { get; init; }
}

public enum Operator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Exists,
    Contains,
    /// <summary>Equal to one of the values of a list.</summary>
    In,
    /// <summary>At least one of the <see cref="Condition.Alternatives"/> holds.</summary>
    Any,
    /// <summary>A regular expression finds a match in the value's text.</summary>
    Matches,
    /// <summary>The value is valid against a JSON Schema.</summary>
    Schema
}

public sealed class Condition
{
    public required Operator Operator { get; init; }
    /// <summary>The expected value; null for <see cref="Operator.Exists"/> and duration conditions.</summary>
    public Template? Value { get; init; }
    /// <summary>For <see cref="Operator.Exists"/>: whether the value must exist.</summary>
    public bool Exists { get; init; }
    /// <summary>For duration conditions.</summary>
    public TimeSpan? DurationValue { get; init; }
    /// <summary>For <see cref="Operator.Any"/>: each alternative is a list of conditions that must all hold.</summary>
    public IReadOnlyList<IReadOnlyList<Condition>> Alternatives { get; init; } = [];
    /// <summary>For <see cref="Operator.Matches"/> without placeholders: the pattern, compiled when the file is loaded.</summary>
    public System.Text.RegularExpressions.Regex? Pattern { get; init; }
    /// <summary>For <see cref="Operator.Schema"/>: the schema, read and checked when the file is loaded.</summary>
    public SchemaReference? Schema { get; init; }
}

/// <summary>The JSON Schema of a <c>schema</c> check (docs/specs/0016-json-schema.md).</summary>
public sealed class SchemaReference
{
    /// <summary>As the report shows it: the path and pointer as written, or a schema written in the step as JSON.</summary>
    public required string Display { get; init; }
    /// <summary>The full path of the file the schema is in; null for a schema written in the step.</summary>
    public string? File { get; init; }
    public required JsonElement Schema { get; init; }
    /// <summary>The root of every file the schema reaches, by full path, for <c>$ref</c>.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Documents { get; init; }
    /// <summary>The <c>pattern</c> and <c>patternProperties</c> of the schema, compiled when it was loaded.</summary>
    public required IReadOnlyDictionary<string, System.Text.RegularExpressions.Regex> Patterns { get; init; }
    /// <summary>Where a schema written in the step resolves a <c>$ref</c> to a file: the directory of its workflow.</summary>
    public required string Directory { get; init; }
}

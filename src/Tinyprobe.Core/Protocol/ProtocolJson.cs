using System.Text;
using System.Text.Json;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Models;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Reporting;
using YamlDotNet.RepresentationModel;

namespace Tinyprobe.Core.Protocol;

/// <summary>
/// The JSON of <c>tinyprobe inspect --json</c> and the events of <c>--reporter ndjson</c>, read by tools
/// such as a UI. Keys are snake_case; durations are milliseconds; YAML values are shown as JSON with
/// their placeholders unresolved (<c>"${email}"</c>). <see cref="Version"/> changes only when a key is
/// removed or changes meaning: new keys may appear at any time.
/// </summary>
public static class ProtocolJson
{
    public const int Version = 1;

    // --- inspect ---------------------------------------------------------------

    /// <summary>
    /// A valid workflow, API file or sequence: its endpoints, and for a workflow its variables, settings and steps
    /// (with the steps of its sequences in place of their <c>use</c>); for a sequence its params and steps.
    /// </summary>
    /// <param name="sourceFile">With <c>--source</c>: the file whose text is added as <c>source</c> and <c>blocks</c>.</param>
    public static string Inspection(string version, WorkflowDefinition workflow, bool indented = true, string? sourceFile = null) => Write(indented, w =>
    {
        WriteHeader(w, version);
        w.WriteBoolean("valid", true);
        w.WriteString("kind", workflow.IsSequence ? "sequence" : workflow.Steps.Count > 0 ? "workflow" : "api");
        w.WriteString("file", workflow.SourceFile);
        w.WriteString("name", workflow.Name);
        w.WriteString("description", workflow.Description);

        w.WriteStartObject("settings");
        w.WriteNumber("timeout_ms", workflow.Settings.Timeout * 1000);
        w.WriteNumber("max_retries", workflow.Settings.MaxRetries);
        w.WriteEndObject();

        w.WriteStartArray("variables");
        foreach (var (name, value) in workflow.Variables)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            WriteLiteral(w, "value", value);
            WriteLocation(w, value.Location);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("imports");
        foreach (var (name, file) in workflow.Imports)
        {
            w.WriteStartObject();
            WriteNullableString(w, "name", name);
            w.WriteString("file", file);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("endpoints");
        foreach (var endpoint in workflow.Endpoints.Values)
            WriteEndpoint(w, endpoint);
        w.WriteEndArray();

        w.WriteStartArray("steps");
        for (int index = 0; index < workflow.Steps.Count; index++)
            WriteStep(w, index, workflow.Steps[index]);
        w.WriteEndArray();

        if (workflow.Params is { } parameters)
            WriteParams(w, parameters);

        if (sourceFile is not null)
            SourceJson.Write(w, sourceFile);
    });

    /// <summary>The problems of an invalid file; as a run event, <paramref name="eventName"/> is <c>invalid</c>.</summary>
    /// <param name="endpoints">The endpoints found in an invalid workflow whose imports loaded, for editors.</param>
    public static string Invalid(string version, string file, IReadOnlyList<LoadProblem> problems,
        string? eventName = null, bool indented = true,
        IReadOnlyDictionary<string, EndpointDefinition>? endpoints = null, string? sourceFile = null) => Write(indented, w =>
    {
        if (eventName is not null)
            w.WriteString("event", eventName);
        WriteHeader(w, version);
        w.WriteBoolean("valid", false);
        w.WriteString("file", Path.GetFullPath(file));
        w.WriteStartArray("errors");
        foreach (var problem in problems)
        {
            w.WriteStartObject();
            w.WriteString("file", problem.File);
            w.WriteNumber("line", problem.Line);
            w.WriteNumber("column", problem.Column);
            w.WriteString("message", problem.Message);
            w.WriteString("text", problem.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (endpoints is not null)
        {
            w.WriteStartArray("endpoints");
            foreach (var endpoint in endpoints.Values)
                WriteEndpoint(w, endpoint);
            w.WriteEndArray();
        }

        // An editor can show the forms of a file that is not valid yet, to fix it.
        if (sourceFile is not null && File.Exists(sourceFile))
            SourceJson.Write(w, sourceFile);
    });

    private static void WriteEndpoint(Utf8JsonWriter w, EndpointDefinition endpoint)
    {
        w.WriteStartObject();
        w.WriteString("name", endpoint.Name);
        WriteNullableString(w, "import_name", endpoint.ImportName);
        w.WriteString("method", endpoint.Method);
        if (endpoint.BaseUrl is null)
            w.WriteNull("base_url");
        else
            w.WriteString("base_url", endpoint.BaseUrl.ToString());
        w.WriteString("path", endpoint.Path.ToString());

        w.WriteStartArray("query");
        foreach (var name in endpoint.Query)
            w.WriteStringValue(name);
        w.WriteEndArray();

        // Effective headers: a later one with the same name replaces an earlier one, as in the request.
        var headers = new Dictionary<string, KeyValuePair<string, Template>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in endpoint.Headers)
            headers[header.Key] = header;
        w.WriteStartArray("headers");
        foreach (var (name, value) in headers.Values)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("value", value.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (endpoint.Body is null)
            w.WriteNull("body");
        else
            WriteLiteral(w, "body", endpoint.Body);
        WriteFields(w, "form", endpoint.Form);
        WriteFields(w, "multipart", endpoint.Multipart);
        WriteBasicAuth(w, endpoint.BasicAuth);
        WriteOAuth2(w, endpoint.OAuth2);

        WriteParams(w, endpoint.Params ?? new Dictionary<string, Template?>());

        w.WriteStartArray("arguments");
        foreach (var name in Loader.EndpointArguments(endpoint))
            w.WriteStringValue(name);
        w.WriteEndArray();

        WriteLocation(w, endpoint.Location);
        w.WriteEndObject();
    }

    private static void WriteParams(Utf8JsonWriter w, IReadOnlyDictionary<string, Template?> parameters)
    {
        w.WriteStartArray("params");
        foreach (var (name, defaultValue) in parameters)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteBoolean("required", defaultValue is null);
            if (defaultValue is null)
                w.WriteNull("default");
            else
                WriteLiteral(w, "default", defaultValue);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// A step of a sequence is located at its workflow's <c>use</c> step (where tools show its result);
    /// <c>source_location</c> is its line in the sequence file, and <c>sequence</c> the use it comes from.
    /// </summary>
    private static void WriteOrigin(Utf8JsonWriter w, StepDefinition step)
    {
        WriteLocation(w, step.Location);
        if (step.SourceLocation is { } source)
            WriteLocation(w, source, "source_location");
        else
            w.WriteNull("source_location");

        if (step.Sequence is { } use)
        {
            w.WriteStartObject("sequence");
            w.WriteString("name", use.Name);
            w.WriteString("file", use.File);
            if (use.Item is { } item)
            {
                w.WriteNumber("item", item);
                w.WriteNumber("items", use.Items ?? 0);
            }
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("sequence");
        }
    }

    private static void WriteStep(Utf8JsonWriter w, int index, StepDefinition step)
    {
        w.WriteStartObject();
        w.WriteNumber("index", index);

        if (step is SleepStep sleep)
        {
            w.WriteString("type", "sleep");
            w.WriteString("name", sleep.Name);
            w.WriteNumber("duration_ms", sleep.Duration.TotalMilliseconds);
            WriteOrigin(w, step);
            w.WriteEndObject();
            return;
        }

        var call = (CallStep)step;
        w.WriteString("type", "call");
        w.WriteString("name", call.Name);
        w.WriteString("call", call.Call);

        w.WriteStartObject("with");
        foreach (var (name, value) in call.With)
            WriteLiteral(w, name, value);
        w.WriteEndObject();

        WriteForEach(w, call.ForEach);

        w.WritePropertyName("expect");
        WriteChecks(w, call.Expect);

        if (call.Until is { } until)
        {
            w.WritePropertyName("until");
            WriteChecks(w, until.Checks, until);
        }
        else
        {
            w.WriteNull("until");
        }

        w.WriteStartObject("save");
        foreach (var (name, source) in call.Save)
            w.WriteString(name, source.ToString());
        w.WriteEndObject();

        WriteOrigin(w, step);
        w.WriteEndObject();
    }

    /// <summary><c>for_each</c>: the items with their literal values (a data file's too, with its path), or the variable.</summary>
    private static void WriteForEach(Utf8JsonWriter w, ForEach? forEach)
    {
        if (forEach is null)
        {
            w.WriteNull("for_each");
            return;
        }

        w.WriteStartObject("for_each");
        if (forEach.Items is { } items)
        {
            w.WriteStartArray("items");
            foreach (var item in items)
            {
                w.WriteStartObject();
                foreach (var (name, value) in item)
                    WriteLiteral(w, name, value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        else
        {
            w.WriteNull("items");
        }
        WriteNullableString(w, "variable", forEach.Variable);
        WriteNullableString(w, "file", forEach.File);
        w.WriteEndObject();
    }

    private static void WriteChecks(Utf8JsonWriter w, Expectation expectation, RetryUntil? until = null)
    {
        w.WriteStartObject();
        w.WriteStartArray("status");
        foreach (var code in expectation.Status)
            w.WriteNumberValue(code);
        w.WriteEndArray();
        WriteNullableString(w, "status_placeholder", expectation.StatusPlaceholder?.ToString());

        w.WriteStartArray("checks");
        foreach (var check in expectation.Headers)
            WriteConditions(w, $"header {check.Subject}", check.Conditions);
        foreach (var check in expectation.Cookies)
            WriteConditions(w, $"cookie {check.Subject}", check.Conditions);
        foreach (var check in expectation.Body)
            WriteConditions(w, check.Subject, check.Conditions);
        WriteConditions(w, "duration", expectation.Duration);
        w.WriteEndArray();

        if (until is not null)
        {
            w.WriteNumber("every_ms", until.Every.TotalMilliseconds);
            w.WriteNumber("timeout_ms", until.Timeout.TotalMilliseconds);
        }
        w.WriteEndObject();
    }

    private static void WriteConditions(Utf8JsonWriter w, string subject, IReadOnlyList<Condition> conditions)
    {
        foreach (var condition in conditions)
        {
            w.WriteStartObject();
            w.WriteString("subject", subject);
            w.WriteString("condition", CheckEvaluator.Describe(condition, scope: null));
            w.WriteEndObject();
        }
    }

    // --- run events ------------------------------------------------------------

    public static string RunStarted(string version, WorkflowDefinition workflow, string? environmentFile) => Write(false, w =>
    {
        w.WriteString("event", "run_started");
        WriteHeader(w, version);
        w.WriteString("workflow", workflow.Name);
        w.WriteString("file", workflow.SourceFile);
        WriteNullableNumber(w, "to", workflow.StoppedAfter);
        w.WriteStartArray("skipped");
        foreach (var index in workflow.SkippedInRun)
            w.WriteNumberValue(index);
        w.WriteEndArray();
        WriteNullableNumber(w, "resumed_after", workflow.ResumedAfter);
        if (environmentFile is null)
            w.WriteNull("env_file");
        else
            w.WriteString("env_file", Path.GetFullPath(environmentFile));

        w.WriteStartArray("steps");
        for (int index = 0; index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            w.WriteStartObject();
            w.WriteNumber("index", index);
            w.WriteString("type", step is SleepStep ? "sleep" : "call");
            w.WriteString("name", step is CallStep call ? call.Name : ((SleepStep)step).Name);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    public static string StepStarted(StepProgress progress) => Write(false, w =>
    {
        w.WriteString("event", "step_started");
        w.WriteNumber("index", progress.Index);
        w.WriteString("type", progress.IsSleep ? "sleep" : "call");
        w.WriteString("name", progress.Name);
        w.WriteNumber("attempt", progress.Attempt);
        WriteNullableNumber(w, "iteration", progress.Iteration);
        WriteNullableNumber(w, "iterations", progress.Iterations);
    });

    public static string StepCompleted(StepReport step) => StepCompleted([step]);

    /// <summary>
    /// One <c>step_completed</c> per step. For a <c>for_each</c> step, <paramref name="reports"/> are its iterations:
    /// the event's own fields are the first failing one's (or the last one's), so a tool that does not read
    /// <c>iterations</c> still shows the right result.
    /// </summary>
    public static string StepCompleted(IReadOnlyList<StepReport> reports) => Write(false, w =>
    {
        w.WriteString("event", "step_completed");
        WriteStepGroup(w, reports, exchange: _ => true);
    });

    private static void WriteStepGroup(Utf8JsonWriter w, IReadOnlyList<StepReport> reports, Func<StepReport, bool> exchange)
    {
        var shown = reports.FirstOrDefault(r => !r.IsSuccess) ?? reports[^1];
        WriteStepFields(w, shown, exchange(shown));
        if (reports[0].Iterations is null)
        {
            w.WriteNull("iterations");
            return;
        }

        w.WriteStartArray("iterations");
        foreach (var report in reports.Where(r => r.Iteration is not null))
        {
            w.WriteStartObject();
            w.WriteNumber("iteration", report.Iteration!.Value);
            WriteStepFields(w, report, exchange(report));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>The reports of a run, one group per step: the iterations of a <c>for_each</c> step are together.</summary>
    public static IEnumerable<IReadOnlyList<StepReport>> ByStep(IEnumerable<StepReport> reports) =>
        reports.GroupBy(r => r.Index).Select(g => (IReadOnlyList<StepReport>)g.ToList());

    /// <summary>The keys of a <c>step_completed</c> event; <paramref name="exchange"/> adds the request and the response.</summary>
    private static void WriteStepFields(Utf8JsonWriter w, StepReport step, bool exchange)
    {
        w.WriteNumber("index", step.Index);
        w.WriteString("name", step.StepName);
        w.WriteBoolean("success", step.IsSuccess);
        WriteNullableString(w, "failure", step.FailureReason);
        WriteNullableString(w, "method", step.Method);
        WriteNullableString(w, "url", step.Url?.ToString());
        if (step.StatusCode is { } status)
            w.WriteNumber("status", (int)status);
        else
            w.WriteNull("status");

        w.WriteStartArray("expected_status");
        foreach (var code in step.ExpectedStatusCodes)
            w.WriteNumberValue((int)code);
        w.WriteEndArray();

        w.WriteNumber("duration_ms", Milliseconds(step.Duration));
        w.WriteNumber("attempts", step.Attempts);
        w.WriteNumber("bytes_sent", step.BytesSent);
        w.WriteNumber("bytes_received", step.BytesReceived);

        w.WriteStartArray("assertions");
        foreach (var assertion in step.Assertions)
        {
            w.WriteStartObject();
            w.WriteString("subject", assertion.Subject);
            w.WriteString("condition", assertion.Condition);
            w.WriteString("actual", assertion.ActualValue);
            w.WriteBoolean("passed", assertion.Passed);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("saved");
        foreach (var (name, value) in step.Saved)
        {
            w.WritePropertyName(name);
            value.WriteTo(w);
        }
        w.WriteEndObject();

        if (!exchange)
            return;

        if (step.Request is { } request)
        {
            w.WriteStartObject("request");
            w.WriteString("method", request.Method);
            w.WriteString("url", request.Url);
            WriteHeaders(w, request.Headers);
            WriteNullableString(w, "body", request.Body);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("request");
        }

        if (step.Response is { } response)
        {
            w.WriteStartObject("response");
            w.WriteNumber("status", response.Status);
            WriteNullableString(w, "reason", response.Reason);
            WriteHeaders(w, response.Headers);
            w.WriteString("body", response.Body);
            w.WriteBoolean("body_truncated", response.BodyTruncated);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("response");
        }
    }

    public static string RunFinished(WorkflowSummary summary, TimeSpan elapsed) => Write(false, w =>
    {
        w.WriteString("event", "run_finished");
        w.WriteBoolean("success", summary.IsSuccess);
        WriteRunSummary(w, summary, elapsed);
        // What run --resume needs to continue this run (spec 0022): only after a --to run that passed.
        w.WritePropertyName("state");
        if (summary.State is { } state)
            state.Write(w);
        else
            w.WriteNullValue();
    });

    private static void WriteRunSummary(Utf8JsonWriter w, WorkflowSummary summary, TimeSpan elapsed)
    {
        w.WriteStartObject("summary");
        w.WriteNumber("total_steps", summary.TotalSteps);
        w.WriteNumber("passed_steps", summary.PassedSteps);
        w.WriteNumber("failed_steps", summary.FailedSteps);
        w.WriteNumber("not_run_steps", summary.NotRunSteps);
        WriteNullableNumber(w, "stopped_after", summary.StoppedAfter);
        w.WriteNumber("skipped_steps", summary.SkippedSteps);
        WriteNullableNumber(w, "resumed_after", summary.ResumedAfter);
        w.WriteNumber("checks_passed", summary.ChecksPassed);
        w.WriteNumber("checks_failed", summary.ChecksFailed);
        w.WriteNumber("http_requests", summary.HttpRequests);
        w.WriteNumber("bytes_sent", summary.BytesSent);
        w.WriteNumber("bytes_received", summary.BytesReceived);
        w.WriteNumber("duration_ms", Milliseconds(elapsed));

        if (summary.HttpRequestDuration is { } d)
        {
            w.WriteStartObject("http_req_duration_ms");
            w.WriteNumber("avg", Milliseconds(d.Average));
            w.WriteNumber("min", Milliseconds(d.Min));
            w.WriteNumber("med", Milliseconds(d.Median));
            w.WriteNumber("max", Milliseconds(d.Max));
            w.WriteNumber("p90", Milliseconds(d.P90));
            w.WriteNumber("p95", Milliseconds(d.P95));
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("http_req_duration_ms");
        }
        w.WriteEndObject();
    }

    // --- suite events (run on a folder or several files) -----------------------

    /// <summary>Before the first workflow of a suite; the events of each workflow follow, unchanged.</summary>
    public static string SuiteStarted(string version, IReadOnlyList<string> files) => Write(false, w =>
    {
        w.WriteString("event", "suite_started");
        WriteHeader(w, version);
        w.WriteStartArray("files");
        foreach (var file in files)
            w.WriteStringValue(Path.GetFullPath(file));
        w.WriteEndArray();
    });

    /// <summary>The last event of a suite.</summary>
    public static string SuiteFinished(SuiteSummary suite, TimeSpan elapsed) => Write(false, w =>
    {
        w.WriteString("event", "suite_finished");
        w.WriteBoolean("success", suite.IsSuccess);
        w.WriteStartArray("workflows");
        foreach (var entry in suite.Entries)
        {
            w.WriteStartObject();
            w.WriteString("file", Path.GetFullPath(entry.File));
            w.WriteBoolean("success", entry.Summary.IsSuccess);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartObject("summary");
        w.WriteNumber("total_workflows", suite.TotalWorkflows);
        w.WriteNumber("passed_workflows", suite.PassedWorkflows);
        w.WriteNumber("failed_workflows", suite.FailedWorkflows);
        w.WriteNumber("total_steps", suite.TotalSteps);
        w.WriteNumber("passed_steps", suite.PassedSteps);
        w.WriteNumber("failed_steps", suite.FailedSteps);
        w.WriteNumber("not_run_steps", suite.NotRunSteps);
        w.WriteNumber("duration_ms", Milliseconds(elapsed));
        w.WriteEndObject();
    });

    // --- MCP tool results (tinyprobe mcp) -------------------------------------------

    /// <summary>The <c>call</c> tool: one step with its request and full response, whatever the status.</summary>
    public static string McpCallResult(string endpoint, StepReport step) => Write(false, w =>
    {
        w.WriteString("endpoint", endpoint);
        WriteStepFields(w, step, exchange: true);
    });

    /// <summary>
    /// The <c>run</c> tool: every workflow with its summary and steps. Passing steps leave out the request and
    /// the response, so a passing suite stays short in the agent's context.
    /// </summary>
    public static string McpRunResult(IReadOnlyList<McpWorkflowRun> runs) => Write(false, w =>
    {
        w.WriteBoolean("success", runs.All(r => r.Summary.IsSuccess));
        w.WriteStartArray("workflows");
        foreach (var run in runs)
        {
            w.WriteStartObject();
            w.WriteString("file", run.Workflow.SourceFile);
            w.WriteString("workflow", run.Workflow.Name);
            w.WriteBoolean("success", run.Summary.IsSuccess);
            WriteRunSummary(w, run.Summary, run.Elapsed);
            w.WriteStartArray("steps");
            var groups = ByStep(run.Report.StepReports).ToList();
            int next = 0;
            foreach (var group in groups)
            {
                WriteSkipped(w, run.Workflow, ref next, group[0].Index);
                w.WriteStartObject();
                // Passing steps leave out the request and the response, iterations included.
                WriteStepGroup(w, group, exchange: r => !r.IsSuccess);
                w.WriteEndObject();
            }
            // Skipped steps after the last one that ran too, unless a failure stopped the run before them.
            if (run.Summary.FailedSteps == 0)
                WriteSkipped(w, run.Workflow, ref next, int.MaxValue);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary>The skipped call steps before <paramref name="before"/>, in their place: index, name and <c>skipped</c>.</summary>
    private static void WriteSkipped(Utf8JsonWriter w, WorkflowDefinition workflow, ref int next, int before)
    {
        for (; next < workflow.Skipped.Count && workflow.Skipped[next] < before; next++)
        {
            var index = workflow.Skipped[next];
            if (workflow.Steps[index] is not CallStep step)
                continue;
            w.WriteStartObject();
            w.WriteNumber("index", index);
            w.WriteString("name", step.Name);
            w.WriteBoolean("skipped", true);
            w.WriteEndObject();
        }
    }

    // --- helpers ---------------------------------------------------------------

    private static void WriteHeader(Utf8JsonWriter w, string version)
    {
        w.WriteNumber("protocol", Version);
        w.WriteString("version", version);
    }

    /// <summary>The fields of a form or multipart body, in order, one entry per sent field; null when absent.</summary>
    private static void WriteFields(Utf8JsonWriter w, string name, IReadOnlyList<BodyField>? fields)
    {
        if (fields is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartArray(name);
        foreach (var field in fields)
        {
            w.WriteStartObject();
            w.WriteString("name", field.Name);
            if (field.File is null)
            {
                WriteLiteral(w, "value", field.Value!);
            }
            else
            {
                w.WriteString("file", field.File.ToString());
                WriteNullableString(w, "filename", field.FileName?.ToString());
                WriteNullableString(w, "content_type", field.ContentType?.ToString());
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteBasicAuth(Utf8JsonWriter w, BasicAuth? basic)
    {
        if (basic is null)
        {
            w.WriteNull("basic_auth");
            return;
        }
        w.WriteStartObject("basic_auth");
        w.WriteString("username", basic.Username.ToString());
        w.WriteString("password", basic.Password.ToString());
        w.WriteEndObject();
    }

    private static void WriteOAuth2(Utf8JsonWriter w, OAuth2ClientCredentials? oauth2)
    {
        if (oauth2 is null)
        {
            w.WriteNull("oauth2");
            return;
        }
        w.WriteStartObject("oauth2");
        w.WriteString("token_url", oauth2.TokenUrl.ToString());
        w.WriteString("client_id", oauth2.ClientId.ToString());
        w.WriteString("client_secret", oauth2.ClientSecret.ToString());
        // A list of scopes is shown as it is sent: joined with spaces.
        if (oauth2.Scope is null)
            w.WriteNull("scope");
        else if (oauth2.Scope.Node is YamlSequenceNode scopes)
            w.WriteString("scope", string.Join(' ', scopes.Children.Select(item => ((YamlScalarNode)item).Value)));
        else
            w.WriteString("scope", oauth2.Scope.ToString());
        w.WriteString("client_auth", oauth2.ClientAuth == ClientAuthentication.Form ? "form" : "basic");
        w.WriteStartObject("form");
        foreach (var (name, value) in oauth2.Form)
            w.WriteString(name, value.ToString());
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteLiteral(Utf8JsonWriter w, string name, Template template)
    {
        w.WritePropertyName(name);
        TemplateRenderer.ToLiteral(template.Node).WriteTo(w);
    }

    private static void WriteLocation(Utf8JsonWriter w, SourceLocation location, string name = "location")
    {
        w.WriteStartObject(name);
        w.WriteString("file", location.File);
        w.WriteNumber("line", location.Line);
        w.WriteNumber("column", location.Column);
        w.WriteEndObject();
    }

    private static void WriteHeaders(Utf8JsonWriter w, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        w.WriteStartArray("headers");
        foreach (var (name, value) in headers)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("value", value);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteNumber(name, value.Value);
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
            w.WriteNull(name);
        else
            w.WriteString(name, value);
    }

    private static double Milliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 3);

    private static string Write(bool indented, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>One workflow run by the MCP <c>run</c> tool.</summary>
public sealed record McpWorkflowRun(WorkflowDefinition Workflow, WorkflowReport Report, WorkflowSummary Summary, TimeSpan Elapsed);

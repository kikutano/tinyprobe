using System.Globalization;
using System.Text.Json;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Models;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Reporting;
using Spectre.Console;

namespace Tinyprobe.Cli;

/// <summary>
/// Renders a workflow run: a header, one line per step as it completes (with a spinner
/// while a request is in flight on interactive terminals) and a k6-style summary.
/// </summary>
internal sealed class ConsoleReporter : IRunReporter
{
    private const int MaxNameWidth = 32;
    private const int MetricNameWidth = 20;

    private readonly IAnsiConsole _console;
    private readonly bool _live;
    private readonly bool _unicode;
    private readonly string _pass;
    private readonly string _fail;
    private readonly string _sent;
    private int _nameWidth = MaxNameWidth;
    private bool _bannerShown;
    /// <summary><c>run --skip</c>: the skipped steps not printed yet, each printed in its place among the others.</summary>
    private readonly Queue<(int Index, string Name)> _skipped = new();

    /// <param name="live">Show a spinner while steps run; ignored when the output is not interactive (CI, pipes).</param>
    public ConsoleReporter(IAnsiConsole console, bool live)
    {
        _console = console;
        _live = live && console.Profile.Capabilities.Interactive;
        _unicode = console.Profile.Capabilities.Unicode;
        _pass = _unicode ? "✓" : "v";
        _fail = _unicode ? "✗" : "x";
        _sent = _unicode ? "→" : ">";
    }

    public void WriteHeader(string version, string workflowPath, string? envPath,
        WorkflowDefinition workflow, IReadOnlyDictionary<string, JsonElement> environment)
    {
        var steps = workflow.Steps.Where((_, i) => workflow.RunsStep(i)).OfType<CallStep>().ToList();
        // A for_each step is shown with " [2/10]" after its name.
        _nameWidth = Math.Clamp(steps.Select(s => s.Name.Length + (s.ForEach is null ? 0 : 8)).DefaultIfEmpty(0).Max(), 4, MaxNameWidth);

        WriteBanner(version);

        if (ShowResponse)
            WriteHeaderRow("call", steps.Single().Call);
        else
            WriteHeaderRow("workflow", workflow.Name);
        WriteHeaderRow("file", workflowPath);
        if (envPath is not null)
            WriteHeaderRow("env", envPath);
        if (environment.TryGetValue("baseUrl", out var baseUrl) && baseUrl.ValueKind == JsonValueKind.String)
            WriteHeaderRow("base url", baseUrl.GetString()!);
        if (!ShowResponse)
            WriteHeaderRow("steps", steps.Count.ToString(CultureInfo.InvariantCulture)
                + (workflow.SkippedInRun.Count() is > 0 and var skipped ? $" ({skipped} skipped: --skip)" : ""));

        _console.WriteLine();
        if (workflow.Resume is { } state)
        {
            int values = state.Saved.Count + state.Variables.Count;
            var after = workflow.Steps[state.After];
            _console.MarkupLine($"  [grey]continuing after step {state.After + 1} ({Escape(after is CallStep call ? call.Name : ((SleepStep)after).Name)}): "
                + $"{values} value{(values == 1 ? "" : "s")}, {state.Cookies.Count} cookie{(state.Cookies.Count == 1 ? "" : "s")}[/]");
            _console.WriteLine();
        }
    }

    /// <summary>The first line of the output, once: a suite prints it before its first workflow.</summary>
    private void WriteBanner(string version)
    {
        if (_bannerShown)
            return;
        _bannerShown = true;

        _console.WriteLine();
        _console.MarkupLine($"  [bold orange1]{(_unicode ? "⚡ " : "")}tinyprobe[/] [grey]{Escape(version)}[/]");
        _console.WriteLine();
    }

    /// <summary><c>tinyprobe call</c>: print the response (status, headers, body) under the step line.</summary>
    public bool ShowResponse { get; init; }

    /// <summary>Prints the problems found while loading the workflow, one per line with its location.</summary>
    public void WriteLoadErrors(string file, IReadOnlyList<LoadProblem> problems) =>
        WriteLoadErrors(_console, problems.Select(p => p.ToString()).ToList());

    public static void WriteLoadErrors(IAnsiConsole console, IReadOnlyList<string> errors, string what = "workflow")
    {
        var fail = console.Profile.Capabilities.Unicode ? "✗" : "x";
        console.WriteLine();
        console.MarkupLine($"  [bold red]{fail} The {what} is not valid[/] [grey]({errors.Count} {(errors.Count == 1 ? "problem" : "problems")})[/]");
        console.WriteLine();
        foreach (var error in errors)
            console.MarkupLine($"  [red]{Escape(error)}[/]");
        console.WriteLine();
    }

    public async Task<WorkflowReport> RunAsync(
        WorkflowEngine engine, WorkflowDefinition workflow, IReadOnlyDictionary<string, JsonElement> environment)
    {
        engine.StepCompleted += WriteStep;
        _skipped.Clear();
        foreach (var index in workflow.SkippedInRun)
            _skipped.Enqueue((index, workflow.Steps[index] is CallStep call ? call.Name : ((SleepStep)workflow.Steps[index]).Name));

        if (!_live)
            return WriteLastSkipped(await engine.ExecuteAsync(workflow, environment));

        WorkflowReport? report = null;
        await _console.Status()
            .Spinner(_unicode ? Spinner.Known.Dots : Spinner.Known.Ascii)
            .SpinnerStyle(new Style(Color.Orange1))
            .StartAsync("starting", async context =>
            {
                engine.StepStarted += step => context.Status($"[grey]running[/] {Escape(step.Description)}");
                report = await engine.ExecuteAsync(workflow, environment);
            });

        return WriteLastSkipped(report!);
    }

    /// <summary>The skipped steps after the last one that ran, unless a failure stopped the run before them.</summary>
    private WorkflowReport WriteLastSkipped(WorkflowReport report)
    {
        if (report.StepReports.All(s => s.IsSuccess))
            WriteSkipped(int.MaxValue);
        return report;
    }

    private void WriteSkipped(int before)
    {
        while (_skipped.TryPeek(out var step) && step.Index < before)
        {
            _skipped.Dequeue();
            var name = step.Name.Length > _nameWidth ? step.Name[..(_nameWidth - 1)] + "…" : step.Name.PadRight(_nameWidth);
            _console.MarkupLine($"  [grey]{(_unicode ? "–" : "-")} {Escape(name)}  skipped (--skip)[/]");
        }
    }

    /// <summary>Which part of the workflow the run covered: <c>(continued after step 7; stopped after step 8 of 12: --to)</c>.</summary>
    private static string Range(WorkflowSummary summary)
    {
        var parts = new List<string>();
        if (summary.ResumedAfter is { } resumed)
            parts.Add($"continued after step {resumed + 1}");
        if (summary.StoppedAfter is { } last)
            parts.Add($"stopped after step {last + 1} of {summary.StepsInFile}: --to");
        return parts.Count == 0 ? "" : $"  [grey]({string.Join("; ", parts)})[/]";
    }

    public void WriteSummary(WorkflowSummary summary, TimeSpan elapsed)
    {
        _console.WriteLine();
        if (ShowResponse)
            return; // one call: the step line and the response say it all

        int checks = summary.ChecksPassed + summary.ChecksFailed;
        WriteMetric("checks", checks == 0
            ? "[grey]none[/]"
            : $"{100.0 * summary.ChecksPassed / checks:0.00}%  [green]{_pass} {summary.ChecksPassed}[/]  {Count(_fail, summary.ChecksFailed)}");

        WriteMetric("steps", $"{summary.PassedSteps}/{summary.TotalSteps} passed"
            + (summary.FailedSteps > 0 ? $"  [red]{summary.FailedSteps} failed[/]" : "")
            + (summary.NotRunSteps > 0 ? $"  [yellow]{summary.NotRunSteps} not run[/]" : "")
            + (summary.SkippedSteps > 0 ? $"  [grey]{summary.SkippedSteps} skipped[/]" : "")
            + Range(summary));

        var rate = elapsed.TotalSeconds > 0 ? summary.HttpRequests / elapsed.TotalSeconds : 0;
        WriteMetric("http_reqs", $"{summary.HttpRequests}  [grey]{rate:0.##}/s[/]");

        if (summary.HttpRequestDuration is { } d)
        {
            WriteMetric("http_req_duration",
                $"avg={Value(d.Average)} min={Value(d.Min)} med={Value(d.Median)} " +
                $"max={Value(d.Max)} p(90)={Value(d.P90)} p(95)={Value(d.P95)}");
        }

        WriteMetric("data_received", FormatBytes(summary.BytesReceived));
        WriteMetric("data_sent", FormatBytes(summary.BytesSent));
        WriteMetric("workflow_duration", FormatDuration(elapsed));

        _console.WriteLine();
        _console.MarkupLine(summary.IsSuccess
            ? $"  [bold black on green] PASSED [/]  {summary.TotalSteps} steps"
            : $"  [bold white on red] FAILED [/]  {summary.FailedSteps} failed"
              + (summary.NotRunSteps > 0 ? $", {summary.NotRunSteps} not run" : ""));
        _console.WriteLine();
    }

    public void WriteSuiteLoadErrors(string target, IReadOnlyList<LoadProblem> problems) =>
        WriteLoadErrors(_console, problems.Select(p => p.ToString()).ToList(), "suite");

    public void WriteSuiteStart(string version, IReadOnlyList<string> files) => WriteBanner(version);

    /// <summary>One line per workflow, with the first failure of each failed one, then the totals.</summary>
    public void WriteSuiteSummary(SuiteSummary suite, TimeSpan elapsed)
    {
        int fileWidth = suite.Entries.Max(e => e.File.Length);
        int stepsWidth = suite.Entries.Max(e => StepsText(e).Length);

        foreach (var entry in suite.Entries)
        {
            var passed = entry.Summary.IsSuccess;
            var icon = passed ? $"[green]{_pass}[/]" : $"[red]{_fail}[/]";
            var file = Escape(entry.File.PadRight(fileWidth));
            var failure = entry.FirstFailure is null ? "" : $"   [red]{Escape(entry.FirstFailure)}[/]";
            _console.MarkupLine(
                $"  {icon} {(passed ? file : $"[red]{file}[/]")}   {StepsText(entry).PadRight(stepsWidth)}   " +
                $"[grey]{FormatDuration(entry.Elapsed),8}[/]{failure}");
        }

        _console.WriteLine();
        WriteMetric("workflows", $"{suite.PassedWorkflows}/{suite.TotalWorkflows} passed"
            + (suite.FailedWorkflows > 0 ? $"  [red]{suite.FailedWorkflows} failed[/]" : ""));
        WriteMetric("steps", $"{suite.PassedSteps}/{suite.TotalSteps} passed"
            + (suite.FailedSteps > 0 ? $"  [red]{suite.FailedSteps} failed[/]" : "")
            + (suite.NotRunSteps > 0 ? $"  [yellow]{suite.NotRunSteps} not run[/]" : ""));
        WriteMetric("suite_duration", FormatDuration(elapsed));

        _console.WriteLine();
        _console.MarkupLine(suite.IsSuccess
            ? $"  [bold black on green] PASSED [/]  {suite.TotalWorkflows} workflows"
            : $"  [bold white on red] FAILED [/]  {suite.FailedWorkflows} of {suite.TotalWorkflows} workflows");
        _console.WriteLine();
    }

    private static string StepsText(SuiteEntry entry) =>
        $"{entry.Summary.PassedSteps}/{entry.Summary.TotalSteps} steps";

    internal void WriteStep(StepReport step)
    {
        WriteSkipped(step.Index);
        // A single call is not a test (spec 0019): a response, whatever its status, gets no verdict, only the
        // arrow and the colored status. A call with no response failed, and keeps its red mark.
        var icon = ShowResponse && step.StatusCode is not null ? $"[grey]{_sent}[/]"
            : step.IsSuccess ? $"[green]{_pass}[/]" : $"[red]{_fail}[/]";
        var name = Escape(TruncateName(step).PadRight(_nameWidth));
        var method = Escape((step.Method ?? "").PadRight(6));
        var duration = step.StatusCode is null ? "" : FormatDuration(step.Duration);
        var path = step.Url is null ? "" : step.Url.IsAbsoluteUri ? step.Url.PathAndQuery : step.Url.ToString();
        var attempts = step.Attempts > 1 ? $"  [grey]({step.Attempts} attempts)[/]" : "";

        _console.MarkupLine(
            $"  {icon} {(step.IsSuccess ? name : $"[red]{name}[/]")}  [bold]{method}[/] {StatusMarkup(step)}" +
            $"  [grey]{duration,9}  {Escape(path)}[/]{attempts}");

        if (ShowResponse && step.Response is { } response)
            WriteResponse(response);

        if (step.IsSuccess)
            return;

        if (step.FailureReason is not null)
            _console.MarkupLine($"      [red]{_fail} {Escape(step.FailureReason)}[/]");

        if (step.Assertions.Any(a => !a.Passed))
        {
            foreach (var assertion in step.Assertions)
            {
                var check = $"{Escape(assertion.Subject)} {Escape(assertion.Condition)}";
                _console.MarkupLine(assertion.Passed
                    ? $"      [green]{_pass}[/] [grey]{check}[/]"
                    : $"      [red]{_fail}[/] {check}  [grey]actual[/] [red]{Escape(assertion.ActualValue)}[/]");
            }
        }
    }

    private void WriteResponse(RecordedResponse response)
    {
        _console.WriteLine();
        _console.MarkupLine($"  [bold]HTTP {response.Status}[/] [grey]{Escape(response.Reason ?? "")}[/]");
        foreach (var (name, value) in response.Headers)
            _console.MarkupLine($"  [grey]{Escape(name)}:[/] {Escape(value)}");
        _console.WriteLine();

        foreach (var line in PrettyBody(response.Body).Split('\n'))
            _console.WriteLine($"  {line.TrimEnd('\r')}");
        if (response.BodyTruncated)
            _console.MarkupLine($"  [yellow](body cut at {FormatBytes(WorkflowEngine.MaxRecordedBodyBytes)})[/]");
    }

    /// <summary>A JSON body indented, anything else as it is.</summary>
    private static string PrettyBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                // Shown to a person, not embedded in HTML: keep accents and symbols readable.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                document.RootElement.WriteTo(writer);
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private void WriteHeaderRow(string label, string value) =>
        _console.MarkupLine($"  [grey]{label,-8}[/] : {Escape(value)}");

    private void WriteMetric(string name, string valueMarkup) =>
        _console.MarkupLine($"  {name}[grey]{new string('.', Math.Max(1, MetricNameWidth - name.Length))}[/]: {valueMarkup}");

    private string Value(TimeSpan duration) => $"[cyan]{FormatDuration(duration)}[/]";

    private static string Count(string icon, int count) =>
        count > 0 ? $"[red]{icon} {count}[/]" : $"[grey]{icon} {count}[/]";

    private static string StatusMarkup(StepReport step)
    {
        if (step.StatusCode is null)
            return "[grey]---[/]";

        int code = (int)step.StatusCode;
        var color = code switch
        {
            < 300 => "green",
            < 400 => "cyan",
            < 500 => "yellow",
            _ => "red"
        };
        return $"[{color}]{code}[/]";
    }

    private string FormatDuration(TimeSpan duration) => duration.TotalMilliseconds switch
    {
        < 1 => $"{duration.TotalMicroseconds:0}{(_unicode ? "µs" : "us")}",
        < 1000 => $"{duration.TotalMilliseconds:0.##}ms",
        _ => $"{duration.TotalSeconds:0.##}s"
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} B",
        < 1_000_000 => $"{bytes / 1000.0:0.#} kB",
        _ => $"{bytes / 1_000_000.0:0.#} MB"
    };

    /// <summary>The name of an iteration keeps its " [2/10]" when the name is cut.</summary>
    private string TruncateName(StepReport step)
    {
        var suffix = step.Iteration is { } i ? $" [{i}/{step.Iterations}]" : "";
        if (suffix.Length == 0 || !step.StepName.EndsWith(suffix, StringComparison.Ordinal) || step.StepName.Length <= _nameWidth)
            return Truncate(step.StepName, _nameWidth);
        return Truncate(step.StepName[..^suffix.Length], Math.Max(_nameWidth - suffix.Length, 4)) + suffix;
    }

    private string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + (_unicode ? "…" : "~");

    private static string Escape(string value) => Markup.Escape(value);
}

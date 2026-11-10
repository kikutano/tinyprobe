using System.Globalization;
using System.Text;
using System.Xml;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Models;
using Tinyprobe.Core.Parsing;

namespace Tinyprobe.Core.Reporting;

/// <summary>One workflow of a run, for the JUnit report.</summary>
/// <param name="File">The workflow's path as given or found, shown by CI servers.</param>
public sealed record JunitRun(string File, WorkflowDefinition Workflow, WorkflowReport Report, TimeSpan Elapsed, DateTime StartedUtc);

/// <summary>
/// <c>--junit &lt;file&gt;</c>: the JUnit XML that CI servers show in their test tabs. One testsuite per workflow,
/// one testcase per call step. No headers and no bodies: CI servers keep reports and show them to everyone who
/// can open the pipeline, and requests carry secrets. See docs/specs/0007-junit-report.md.
/// </summary>
public static class JunitReport
{
    private const string NotRun = "Not run: an earlier step failed";
    private const string SkippedByOption = "Skipped with --skip";

    public static string Write(string version, IReadOnlyList<JunitRun> runs)
    {
        var suites = runs.Select(run => Suite(run)).ToList();
        return Document(version, suites);
    }

    /// <summary>The report of a run where some files did not load: one suite per file, with its problems.</summary>
    public static string WriteInvalid(string version, IReadOnlyList<LoadProblem> problems)
    {
        var suites = problems.GroupBy(p => p.File).Select(group => new SuiteXml(
            Name: group.Key,
            File: group.Key,
            Time: TimeSpan.Zero,
            StartedUtc: null,
            Cases: [new CaseXml("load", group.Key, group.Key, Line: null, TimeSpan.Zero, Outcome.Error,
                Message: $"The file is not valid ({group.Count()} {(group.Count() == 1 ? "problem" : "problems")})",
                Type: "invalid",
                Text: string.Join("\n", group.Select(p => p.ToString())))])).ToList();
        return Document(version, suites);
    }

    private enum Outcome { Passed, Failure, Error, Skipped }

    private sealed record CaseXml(string Name, string ClassName, string File, int? Line, TimeSpan Time,
        Outcome Outcome, string? Message = null, string? Type = null, string? Text = null);

    private sealed record SuiteXml(string Name, string File, TimeSpan Time, DateTime? StartedUtc, IReadOnlyList<CaseXml> Cases);

    private static SuiteXml Suite(JunitRun run)
    {
        // A for_each step has one report per item: each one is a test case.
        var reports = run.Report.StepReports.GroupBy(s => s.Index).ToDictionary(g => g.Key, g => g.ToList());
        var cases = new List<CaseXml>();
        // A resumed run reports its own steps: the earlier ones were in the report of the state's run.
        for (int index = (run.Workflow.ResumedAfter ?? -1) + 1; index < run.Workflow.Steps.Count; index++)
        {
            if (run.Workflow.Steps[index] is not CallStep step)
                continue; // sleeps are not tests

            int? line = step.Location.Line > 0 ? step.Location.Line : null;
            if (!reports.TryGetValue(index, out var stepReports))
            {
                cases.Add(new CaseXml(step.Name, run.Workflow.Name, run.File, line, TimeSpan.Zero, Outcome.Skipped,
                    run.Workflow.Skipped.Contains(index) ? SkippedByOption : NotRun));
                continue;
            }

            foreach (var report in stepReports)
            {
                cases.Add(report.IsSuccess
                    ? new CaseXml(report.StepName, run.Workflow.Name, run.File, line, report.Duration, Outcome.Passed)
                    : Failed(report, run, line));
            }
        }
        return new SuiteXml(run.Workflow.Name, run.File, run.Elapsed, run.StartedUtc, cases);
    }

    private static CaseXml Failed(StepReport report, JunitRun run, int? line)
    {
        var failedCheck = report.Assertions.FirstOrDefault(a => !a.Passed);
        var message = report.FailureReason
            ?? (failedCheck is null ? "Failed" : $"{failedCheck.Subject} {failedCheck.Condition}, actual {failedCheck.ActualValue}");

        // No response at all (network, timeout, undefined variable) is an error; a response that did not pass is a failure.
        if (report.StatusCode is null)
            return new CaseXml(report.StepName, run.Workflow.Name, run.File, line, report.Duration, Outcome.Error, message, "request", Details(report));

        var type = report.FailureReason?.StartsWith("Expected", StringComparison.Ordinal) == true ? "status"
            : failedCheck is not null ? "check"
            : "step";
        return new CaseXml(report.StepName, run.Workflow.Name, run.File, line, report.Duration, Outcome.Failure, message, type, Details(report));
    }

    /// <summary>The request line, the status and the checks with their actual values, as in the console.</summary>
    private static string Details(StepReport report)
    {
        var text = new StringBuilder();
        if (report.Method is not null && report.Url is not null)
            text.Append($"{report.Method} {report.Url} -> {(report.StatusCode is { } status ? ((int)status).ToString(CultureInfo.InvariantCulture) : "no response")}\n");
        if (report.FailureReason is not null)
            text.Append($"{report.FailureReason}\n");
        if (report.Assertions.Count > 0)
            text.Append('\n');
        foreach (var assertion in report.Assertions)
        {
            text.Append(assertion.Passed
                ? $"✓ {assertion.Subject} {assertion.Condition}\n"
                : $"✗ {assertion.Subject} {assertion.Condition}  actual {assertion.ActualValue}\n");
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string Document(string version, IReadOnlyList<SuiteXml> suites)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            NewLineChars = "\n"
        };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            var all = suites.SelectMany(s => s.Cases).ToList();
            w.WriteStartDocument();
            w.WriteStartElement("testsuites");
            w.WriteAttributeString("name", "tinyprobe");
            Counts(w, all);
            w.WriteAttributeString("time", Seconds(TimeSpan.FromTicks(suites.Sum(s => s.Time.Ticks))));

            foreach (var suite in suites)
            {
                w.WriteStartElement("testsuite");
                w.WriteAttributeString("name", Clean(suite.Name));
                w.WriteAttributeString("file", Clean(suite.File));
                Counts(w, suite.Cases);
                w.WriteAttributeString("time", Seconds(suite.Time));
                if (suite.StartedUtc is { } started)
                    w.WriteAttributeString("timestamp", started.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

                w.WriteStartElement("properties");
                w.WriteStartElement("property");
                w.WriteAttributeString("name", "tinyprobe.version");
                w.WriteAttributeString("value", version);
                w.WriteEndElement();
                w.WriteEndElement();

                foreach (var testCase in suite.Cases)
                    Case(w, testCase);
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Counts(XmlWriter w, IReadOnlyCollection<CaseXml> cases)
    {
        w.WriteAttributeString("tests", cases.Count.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("failures", cases.Count(c => c.Outcome == Outcome.Failure).ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("errors", cases.Count(c => c.Outcome == Outcome.Error).ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("skipped", cases.Count(c => c.Outcome == Outcome.Skipped).ToString(CultureInfo.InvariantCulture));
    }

    private static void Case(XmlWriter w, CaseXml testCase)
    {
        w.WriteStartElement("testcase");
        w.WriteAttributeString("name", Clean(testCase.Name));
        w.WriteAttributeString("classname", Clean(testCase.ClassName));
        w.WriteAttributeString("file", Clean(testCase.File));
        if (testCase.Line is { } line)
            w.WriteAttributeString("line", line.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("time", Seconds(testCase.Time));

        if (testCase.Outcome != Outcome.Passed)
        {
            w.WriteStartElement(testCase.Outcome switch
            {
                Outcome.Failure => "failure",
                Outcome.Error => "error",
                _ => "skipped"
            });
            if (testCase.Message is not null)
                w.WriteAttributeString("message", Clean(testCase.Message));
            if (testCase.Type is not null)
                w.WriteAttributeString("type", testCase.Type);
            if (!string.IsNullOrEmpty(testCase.Text))
                w.WriteString(Clean(testCase.Text));
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>XML 1.0 cannot hold most control characters, which a response value can contain.</summary>
    private static string Clean(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
            return text;
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
            clean.Append(XmlConvert.IsXmlChar(c) || char.IsSurrogate(c) ? c : '?');
        return clean.ToString();
    }
}

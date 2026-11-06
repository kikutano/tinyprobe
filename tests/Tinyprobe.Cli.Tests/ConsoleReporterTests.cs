using System.Net;
using System.Text.RegularExpressions;
using Spectre.Console;
using Tinyprobe.Core.Models;

namespace Tinyprobe.Cli.Tests;

/// <summary>The step lines of the console (spec 0019: a call shows its status, not a verdict; spec 0021: skipped steps).</summary>
public sealed class ConsoleReporterTests
{
    private sealed class Ok : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static (ConsoleReporter Reporter, StringWriter Output) Reporter(bool call, bool unicode = true)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Capabilities.Unicode = unicode;
        console.Profile.Width = 200;
        return (new ConsoleReporter(console, live: false) { ShowResponse = call }, output);
    }

    /// <summary>The text written, without the color codes some consoles add (CI forces them on).</summary>
    private static string Text(StringWriter output) => Regex.Replace(output.ToString(), @"\x1b\[[0-9;]*m", "");

    private static StepReport Step(HttpStatusCode? status, bool success = true, string? failure = null) => new()
    {
        StepName = "login",
        Method = "POST",
        Url = new Uri("http://localhost:5122/api/login"),
        StatusCode = status,
        IsSuccess = success,
        FailureReason = failure
    };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void ACall_WithAResponse_ShowsTheArrowAndTheStatus_NoVerdict(HttpStatusCode status)
    {
        var (reporter, output) = Reporter(call: true);

        reporter.WriteStep(Step(status));

        var line = Text(output).Split('\n')[0];
        Assert.StartsWith("  → login", line);
        Assert.Matches($@"POST\s+{(int)status}\s", line);
        Assert.DoesNotContain("✓", Text(output));
        Assert.DoesNotContain("✗", Text(output));
    }

    [Fact]
    public void ACall_WithNoResponse_KeepsTheRedMarkAndTheReason()
    {
        var (reporter, output) = Reporter(call: true);

        reporter.WriteStep(Step(null, success: false, failure: "Connection refused (localhost:1)"));

        var lines = Text(output).Split('\n');
        Assert.StartsWith("  ✗ login", lines[0]);
        Assert.Matches(@"POST\s+---", lines[0]);
        Assert.Equal("      ✗ Connection refused (localhost:1)", lines[1].TrimEnd());
    }

    [Fact]
    public void ACall_WithoutUnicode_ShowsAGreaterThanSign()
    {
        var (reporter, output) = Reporter(call: true, unicode: false);

        reporter.WriteStep(Step(HttpStatusCode.Unauthorized));

        Assert.StartsWith("  > login", Text(output));
    }

    [Fact]
    public void AStepOfARun_KeepsItsVerdict()
    {
        var (reporter, output) = Reporter(call: false);

        reporter.WriteStep(Step(HttpStatusCode.OK));
        reporter.WriteStep(Step(HttpStatusCode.Unauthorized, success: false, failure: "expected status 200, got 401"));

        var lines = Text(output).Split('\n');
        Assert.StartsWith("  ✓ login", lines[0]);
        Assert.StartsWith("  ✗ login", lines[1]);
    }

    [Fact]
    public async Task ASkippedStep_IsPrintedInItsPlace_AndCountedInTheSummary()
    {
        var workflow = Core.Parsing.WorkflowLoader.Parse("""
            name: Users
            base_url: https://api.test
            endpoints:
              list: { path: /users }
              remove: { method: DELETE, path: /users/1 }
            steps:
              - name: First
                call: list
              - name: Delete the user
                call: remove
              - name: Last
                call: list
              - sleep: 1ms
            """, "users.yaml");
        Assert.True(Core.Parsing.WorkflowLoader.TryNarrow(workflow, null, ["2", "4"], out var narrowed, out var error), error);
        var (reporter, output) = Reporter(call: false);
        var engine = new Core.Execution.WorkflowEngine(new HttpClient(new Ok()));

        var report = await reporter.RunAsync(engine, narrowed, new Dictionary<string, System.Text.Json.JsonElement>());
        reporter.WriteSummary(Core.Reporting.WorkflowSummary.Create(narrowed, report), TimeSpan.FromSeconds(1));

        var lines = Text(output).Split('\n').Where(l => l.StartsWith("  ✓") || l.StartsWith("  –")).Select(l => l.TrimEnd()).ToList();
        Assert.True(lines.Count == 4, Text(output));
        Assert.StartsWith("  ✓ First", lines[0]);
        Assert.Matches(@"^  – Delete the user +skipped \(--skip\)$", lines[1]);
        Assert.StartsWith("  ✓ Last", lines[2]);
        Assert.StartsWith("  – sleep 1ms", lines[3]);
        Assert.Contains("2/2 passed  2 skipped", Text(output));
    }
}

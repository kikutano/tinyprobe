using System.Net;
using Spectre.Console;
using Tinyprobe.Core.Models;

namespace Tinyprobe.Cli.Tests;

/// <summary>The step line of the console (spec 0019: a call shows its status, not a verdict).</summary>
public sealed class ConsoleReporterTests
{
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

        var line = output.ToString().Split('\n')[0];
        Assert.StartsWith("  → login", line);
        Assert.Contains($"POST   {(int)status}", line);
        Assert.DoesNotContain("✓", output.ToString());
        Assert.DoesNotContain("✗", output.ToString());
    }

    [Fact]
    public void ACall_WithNoResponse_KeepsTheRedMarkAndTheReason()
    {
        var (reporter, output) = Reporter(call: true);

        reporter.WriteStep(Step(null, success: false, failure: "Connection refused (localhost:1)"));

        var lines = output.ToString().Split('\n');
        Assert.StartsWith("  ✗ login", lines[0]);
        Assert.Contains("---", lines[0]);
        Assert.Equal("      ✗ Connection refused (localhost:1)", lines[1].TrimEnd());
    }

    [Fact]
    public void ACall_WithoutUnicode_ShowsAGreaterThanSign()
    {
        var (reporter, output) = Reporter(call: true, unicode: false);

        reporter.WriteStep(Step(HttpStatusCode.Unauthorized));

        Assert.StartsWith("  > login", output.ToString());
    }

    [Fact]
    public void AStepOfARun_KeepsItsVerdict()
    {
        var (reporter, output) = Reporter(call: false);

        reporter.WriteStep(Step(HttpStatusCode.OK));
        reporter.WriteStep(Step(HttpStatusCode.Unauthorized, success: false, failure: "expected status 200, got 401"));

        var lines = output.ToString().Split('\n');
        Assert.StartsWith("  ✓ login", lines[0]);
        Assert.StartsWith("  ✗ login", lines[1]);
    }
}

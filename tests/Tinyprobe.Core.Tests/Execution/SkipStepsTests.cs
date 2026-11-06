using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Protocol;
using Tinyprobe.Core.Reporting;
using Tinyprobe.Core.Tests.Fakes;

namespace Tinyprobe.Core.Tests.Execution;

/// <summary><c>run --skip</c> (docs/specs/0021-skip-steps.md).</summary>
public sealed class SkipStepsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-skip-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Yaml = """
        name: Users
        base_url: https://api.test
        endpoints:
          login: { method: POST, path: /login }
          create: { method: POST, path: /users }
          read: { path: "/users/${id}" }
          remove: { method: DELETE, path: "/users/${userId}" }
        steps:
          - name: Log in
            call: login
          - name: Create a user
            call: create
            save: { userId: $.id }
          - sleep: 1ms
          - name: Read some users
            call: read
            for_each: [ { id: 1 }, { id: 2 } ]
          - name: Delete the user
            call: remove
          - name: Log in
            call: login
        """;

    private static WorkflowDefinition Narrow(string? to, params string[] skip)
    {
        Assert.True(WorkflowLoader.TryNarrow(WorkflowLoader.Parse(Yaml, "test.yaml"), to, skip, out var narrowed, out var error), error);
        return narrowed;
    }

    private static string Error(string? to, params string[] skip)
    {
        Assert.False(WorkflowLoader.TryNarrow(WorkflowLoader.Parse(Yaml, "test.yaml"), to, skip, out _, out var error));
        return error;
    }

    private static FakeServer Server() =>
        new(r => r.Path == "/users" ? new FakeResponse(HttpStatusCode.Created, """{"id":7}""") : new FakeResponse());

    [Fact]
    public void Steps_AreNamedByNumberOrName_InOrder_OnceEach()
    {
        var workflow = Narrow(null, "4", "sleep 1ms", "Read some users");

        Assert.Equal([2, 3], workflow.Skipped);
        Assert.Equal(6, workflow.Steps.Count); // every step keeps its place and its index
        Assert.Null(workflow.StoppedAfter);
    }

    [Theory]
    [InlineData(null, new[] { "7" }, "--skip 7: the workflow has 6 steps")]
    [InlineData(null, new[] { "Delete the usr" }, "--skip \"Delete the usr\": no step has this name (did you mean 'Delete the user'?)")]
    [InlineData(null, new[] { "Log in" }, "--skip \"Log in\": 2 steps have this name (1, 6): use the number")]
    [InlineData("5", new[] { "2", "Delete the user" }, "--skip \"Delete the user\": it is the step of --to, which always runs")]
    [InlineData(null, new[] { "1", "2", "3", "4", "5", "6" }, "--skip: every step of the workflow is skipped, nothing would run")]
    [InlineData("2", new[] { "1", "2" }, "--skip 2: it is the step of --to, which always runs")]
    public void AStepThatIsNotThere_TheTarget_OrEveryStep_IsAnError(string? to, string[] skip, string expected) =>
        Assert.Equal(expected, Error(to, skip));

    [Fact]
    public void WithTo_TheStepsAfterTheTargetAreIgnored()
    {
        var workflow = Narrow("2", "1", "5");

        Assert.Equal([0], workflow.Skipped);
        Assert.Equal(2, workflow.Steps.Count);
        Assert.Equal(1, workflow.StoppedAfter);
    }

    [Fact]
    public async Task SkippedSteps_SendNothing_RaiseNoEvent_AndAreCountedApart()
    {
        var workflow = Narrow(null, "3", "4", "6");
        var server = Server();
        var engine = new WorkflowEngine(new HttpClient(server));
        var started = new List<int>();
        engine.StepStarted += step => started.Add(step.Index);

        var report = await engine.ExecuteAsync(workflow);
        var summary = WorkflowSummary.Create(workflow, report);

        Assert.Equal(["/login", "/users", "/users/7"], server.Requests.Select(r => r.Path));
        Assert.Equal([0, 1, 4], started);
        Assert.Equal([0, 1, 4], report.StepReports.Select(s => s.Index));
        Assert.True(summary.IsSuccess);
        Assert.Equal((3, 3, 0, 3), (summary.TotalSteps, summary.PassedSteps, summary.NotRunSteps, summary.SkippedSteps));

        var runStarted = JsonDocument.Parse(ProtocolJson.RunStarted("1.2.3", workflow, null)).RootElement;
        Assert.Equal([2, 3, 5], runStarted.GetProperty("skipped").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(6, runStarted.GetProperty("steps").GetArrayLength());
        var finished = JsonDocument.Parse(ProtocolJson.RunFinished(summary, TimeSpan.Zero)).RootElement.GetProperty("summary");
        Assert.Equal(3, finished.GetProperty("skipped_steps").GetInt32());
        Assert.Equal(3, finished.GetProperty("total_steps").GetInt32());

        var xml = XDocument.Parse(JunitReport.Write("1.2.3", [new JunitRun("test.yaml", workflow, report, TimeSpan.Zero, DateTime.UtcNow)]));
        var skipped = xml.Descendants("testcase").Where(c => c.Element("skipped") is not null).ToList();
        Assert.Equal(["Read some users", "Log in"], skipped.Select(c => (string)c.Attribute("name")!));
        Assert.All(skipped, c => Assert.Equal("Skipped with --skip", (string)c.Element("skipped")!.Attribute("message")!));
    }

    [Fact]
    public void AWholeRun_SkipsNothing()
    {
        var workflow = WorkflowLoader.Parse(Yaml, "test.yaml");

        Assert.Empty(workflow.Skipped);
        Assert.Equal(0, JsonDocument.Parse(ProtocolJson.RunStarted("1.2.3", workflow, null)).RootElement.GetProperty("skipped").GetArrayLength());
        Assert.Equal(0, WorkflowSummary.Create(workflow, new Models.WorkflowReport()).SkippedSteps);
    }

    [Fact]
    public async Task AValueOnlyASkippedStepSaves_FailsTheStepThatNeedsIt_NamingTheSkippedStep()
    {
        var workflow = Narrow(null, "Create a user");
        var server = Server();

        var report = await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(workflow);
        var summary = WorkflowSummary.Create(workflow, report);

        var failed = Assert.Single(report.StepReports, s => !s.IsSuccess);
        Assert.Equal("Delete the user", failed.StepName);
        Assert.Equal("Variable 'userId' is not defined: step 2 (Create a user), which saves it, was skipped.", failed.FailureReason);
        Assert.Equal(["/login", "/users/1", "/users/2"], server.Requests.Select(r => r.Path));
        Assert.False(summary.IsSuccess);
        Assert.Equal(1, summary.NotRunSteps); // the last Log in
    }

    [Fact]
    public async Task AValueTheEnvironmentGives_IsUsed_WhenTheStepThatSavesItIsSkipped()
    {
        var workflow = Narrow(null, "2");
        var server = Server();
        var environment = new Dictionary<string, JsonElement> { ["userId"] = JsonValues.FromString("42") };

        var report = await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(workflow, environment);

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Contains("/users/42", server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task AStepOfASequence_IsSkipped_ByItsPrefixedName()
    {
        File.WriteAllText(Path.Combine(_directory, "api.yaml"), """
            base_url: https://api.test
            endpoints:
              login: { method: POST, path: /login }
              ping: { path: /ping }
              me: { path: "/me/${token}" }
            """);
        File.WriteAllText(Path.Combine(_directory, "log-in.yaml"), """
            name: Log in
            import: [api.yaml]
            sequence:
              - name: Get a token
                call: login
                save: { token: $.token }
              - name: Ping
                call: ping
            """);
        var path = Path.Combine(_directory, "flow.yaml");
        File.WriteAllText(path, """
            name: Flow
            import: [api.yaml]
            steps:
              - use: log-in.yaml
              - name: Me
                call: me
            """);
        Assert.True(WorkflowLoader.TryNarrow(WorkflowLoader.Load(path), null, ["Log in › Get a token"], out var workflow, out var error), error);
        var server = new FakeServer(_ => new FakeResponse(Body: """{"token":"t"}"""));

        var report = await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(workflow);

        Assert.Equal([0], workflow.Skipped);
        Assert.Equal(["/ping"], server.Requests.Select(r => r.Path));
        Assert.Equal("Variable 'token' is not defined: step 1 (Log in › Get a token), which saves it, was skipped.", report.StepReports[^1].FailureReason);
    }
}

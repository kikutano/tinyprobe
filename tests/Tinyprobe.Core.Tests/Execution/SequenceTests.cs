using System.Text.Json;
using System.Xml.Linq;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Models;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Protocol;
using Tinyprobe.Core.Reporting;
using Tinyprobe.Core.Tests.Fakes;

namespace Tinyprobe.Core.Tests.Execution;

/// <summary>Sequence files and <c>use</c> (docs/specs/0012-reusable-sequences.md).</summary>
public sealed class SequenceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-sequences-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Api = """
        base_url: https://api.test
        auth:
          bearer: { Authorization: "Bearer ${token}" }
        endpoints:
          login:
            method: POST
            path: /login
            params: { email:, password: }
            body: { email: "${email}", password: "${password}" }
          me: { path: /me, auth: bearer }
          ping: { path: /ping }
        """;

    private const string LogIn = """
        name: Log in
        import: [../api/backend.yaml]
        params:
          email: admin@example.com
          password:
        sequence:
          - call: login
            with: { email: "${email}", password: "${password}" }
            save: { token: $.token }
        """;

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Workflow(string steps, string name = "workflows/test.yaml")
    {
        Write("api/backend.yaml", Api);
        Write("sequences/log-in.yaml", LogIn);
        return Write(name, $"name: Test\nimport: [../api/backend.yaml]\nsteps:\n{steps}");
    }

    private static FakeResponse Respond(CapturedRequest request) => request.Path == "/login"
        ? new FakeResponse(Body: $$"""{"token":"tok-{{JsonDocument.Parse(request.Body!).RootElement.GetProperty("password").GetString()}}"}""")
        : new FakeResponse();

    private static async Task<(WorkflowReport Report, FakeServer Server)> RunAsync(string path, Func<CapturedRequest, FakeResponse>? respond = null)
    {
        var server = new FakeServer(respond ?? Respond);
        var engine = new WorkflowEngine(new HttpClient(server)) { RecordExchanges = true };
        var environment = new Dictionary<string, JsonElement> { ["adminPassword"] = JsonValues.FromString("s3cret") };
        return (await engine.ExecuteAsync(WorkflowLoader.Load(path), environment), server);
    }

    private WorkflowLoadException LoadFails(string path) => Assert.Throws<WorkflowLoadException>(() => WorkflowLoader.Load(path));

    private string Relative(string error) => error.Replace(Path.GetRelativePath(Environment.CurrentDirectory, _directory) + Path.DirectorySeparatorChar, "")
        .Replace(_directory + Path.DirectorySeparatorChar, "");

    [Fact]
    public async Task UseRunsTheSequence_WithItsParams_AndItsSavesAreVisibleAfterIt()
    {
        var (report, server) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                with: { password: "${adminPassword}" }
              - name: Who am I
                call: me
            """));

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Equal("""{"email":"admin@example.com","password":"s3cret"}""", server.Requests[0].Body);
        Assert.Equal("Bearer tok-s3cret", server.Requests[1].Headers["Authorization"]);
        Assert.Equal(["Log in › login", "Who am I"], report.StepReports.Select(s => s.StepName));
    }

    [Fact]
    public async Task Params_AreLocalToTheSequence()
    {
        var (report, _) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                with: { password: x }
              - call: ping
                expect:
                  headers: { X-Unused: "${password}" }
            """));

        Assert.Equal("Variable 'password' is not defined.", report.StepReports[1].FailureReason);
    }

    [Fact]
    public async Task EachUse_HasItsOwnParams_AndANameMayReplaceTheSequences()
    {
        var (report, server) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                with: { password: first }
              - name: Second login
                use: ../sequences/log-in.yaml
                with: { password: second, email: ops@example.com }
              - call: me
            """));

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Equal(["Log in › login", "Second login › login", "me"], report.StepReports.Select(s => s.StepName));
        Assert.Contains("ops@example.com", server.Requests[1].Body);
        Assert.Equal("Bearer tok-second", server.Requests[2].Headers["Authorization"]);
    }

    [Fact]
    public async Task ANestedSequence_SeesTheParamsOfTheOuterOne()
    {
        Workflow("");
        Write("sequences/setup.yaml", """
            name: Setup
            params: { pass: }
            sequence:
              - use: log-in.yaml
                with: { password: "${pass}-inner" }
              - sleep: 1ms
              - call: ping
            """);
        var path = Write("workflows/nested.yaml", "import: [../api/backend.yaml]\nsteps:\n  - use: ../sequences/setup.yaml\n    with: { pass: p }\n");

        var (report, server) = await RunAsync(path);

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Contains("\"password\":\"p-inner\"", server.Requests[0].Body);
        Assert.Equal(["Setup › Log in › login", "Setup › ping"], report.StepReports.Select(s => s.StepName));
        var workflow = WorkflowLoader.Load(path);
        Assert.Equal("Setup › sleep 1ms", ((SleepStep)workflow.Steps[1]).Name);
    }

    [Fact]
    public async Task TheWorkflowStops_AtAFailingStepOfASequence()
    {
        var (report, server) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                with: { password: x }
              - call: me
            """), _ => new FakeResponse(System.Net.HttpStatusCode.Unauthorized));

        Assert.False(report.Single().IsSuccess);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void StepsOfASequence_AreLocatedAtTheUse_AndRememberWhereTheyAreWritten()
    {
        var path = Workflow("""
              - call: ping
              - use: ../sequences/log-in.yaml
                with: { password: x }
            """);
        var workflow = WorkflowLoader.Load(path);

        var step = workflow.Steps[1];
        Assert.Equal((5, 5), (step.Location.Line, step.Location.Column));
        Assert.EndsWith("test.yaml", step.Location.File);
        Assert.Equal((7, 5), (step.SourceLocation!.Value.Line, step.SourceLocation.Value.Column));
        Assert.EndsWith("log-in.yaml", step.SourceLocation.Value.File);

        var json = JsonDocument.Parse(ProtocolJson.Inspection("1.2.3", workflow, indented: false)).RootElement.GetProperty("steps");
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("sequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("source_location").ValueKind);
        Assert.Equal("Log in", json[1].GetProperty("sequence").GetProperty("name").GetString());
        Assert.Equal(7, json[1].GetProperty("source_location").GetProperty("line").GetInt32());
        Assert.Equal(5, json[1].GetProperty("location").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Junit_PlacesTheStepsOfASequence_AtTheUse()
    {
        var path = Workflow("  - call: ping\n  - use: ../sequences/log-in.yaml\n    with: { password: x }\n");
        var (report, _) = await RunAsync(path);

        var xml = XDocument.Parse(JunitReport.Write("1.2.3", [new JunitRun(path, WorkflowLoader.Load(path), report, TimeSpan.Zero, DateTime.UtcNow)]));
        Assert.Equal(["4", "5"], xml.Descendants("testcase").Select(c => (string)c.Attribute("line")!));
    }

    [Fact]
    public void ASequenceFile_IsInspected_WithItsParams()
    {
        Workflow("");
        var json = JsonDocument.Parse(ProtocolJson.Inspection("1.2.3", WorkflowLoader.LoadAny(Path.Combine(_directory, "sequences/log-in.yaml")), indented: false)).RootElement;

        Assert.Equal("sequence", json.GetProperty("kind").GetString());
        Assert.Equal("Log in", json.GetProperty("name").GetString());
        Assert.Equal("""[{"name":"email","required":false,"default":"admin@example.com"},{"name":"password","required":true,"default":null}]""",
            json.GetProperty("params").GetRawText());
        Assert.Equal("login", json.GetProperty("steps")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ASequenceFile_CannotRunAsAWorkflow()
    {
        Workflow("");
        var error = LoadFails(Path.Combine(_directory, "sequences/log-in.yaml")).Errors.Single();

        Assert.EndsWith("is a sequence, not a workflow: run it with `use:` from a workflow", error);
    }

    // --- validation ------------------------------------------------------------

    [Theory]
    [InlineData("  - use: ../sequences/log-in.yml", "workflows/test.yaml:4:10: sequence not found: ../sequences/log-in.yml")]
    [InlineData("  - use: ../sequences/log-in.yaml\n    with: { pasword: x }", "workflows/test.yaml:5:13: unknown param 'pasword' for sequence 'Log in' (did you mean 'password'?)")]
    [InlineData("  - use: ../sequences/log-in.yaml", "workflows/test.yaml:4:5: missing required param 'password' of sequence 'Log in' (defined at sequences/log-in.yaml:4:3)")]
    [InlineData("  - use: ../sequences/log-in.yaml\n    with: { password: x }\n    expect: { status: 200 }", "workflows/test.yaml:4:5: a use step cannot have 'expect'")]
    [InlineData("  - use: ../api/backend.yaml", "workflows/test.yaml:4:10: ../api/backend.yaml is not a sequence (it has no 'sequence:')")]
    public void InvalidUses_AreReportedWithTheirLocation(string steps, string expected)
    {
        var errors = LoadFails(Workflow(steps)).Errors.Select(Relative);

        Assert.Contains(expected, errors);
    }

    [Fact]
    public void ErrorsInsideASequence_PointToTheSequenceFile_Once()
    {
        Workflow("");
        Write("sequences/broken.yaml", "sequence:\n  - call: lgin\n  - call: login\n    with: { email: a }\n");
        var path = Write("workflows/broken.yaml",
            "import: [../api/backend.yaml]\nsteps:\n  - use: ../sequences/broken.yaml\n  - use: ../sequences/broken.yaml\n");

        var errors = LoadFails(path).Errors.Select(Relative).ToList();

        Assert.Equal(1, errors.Count(e => e.StartsWith("sequences/broken.yaml:2:5: unknown endpoint 'lgin'")));
        Assert.Contains(errors, e => e.StartsWith("sequences/broken.yaml:3:5: missing required param 'password' of endpoint 'login'"));
    }

    [Fact]
    public void ASequenceThatUsesItself_IsAnError()
    {
        Workflow("");
        Write("sequences/a.yaml", "sequence:\n  - use: b.yaml\n");
        Write("sequences/b.yaml", "sequence:\n  - use: a.yaml\n");
        var path = Write("workflows/loop.yaml", "steps:\n  - use: ../sequences/a.yaml\n");

        var errors = LoadFails(path).Errors.Select(Relative);

        Assert.Contains("sequences/b.yaml:2:10: sequence 'a' uses itself: a.yaml → b.yaml → a.yaml", errors);
    }

    [Fact]
    public void UnknownKeysOfASequenceFile_AreReported()
    {
        Workflow("");
        Write("sequences/typo.yaml", "params: {}\nsequence:\n  - call: ping\nsetings: {}\n");
        var path = Write("workflows/typo.yaml", "import: [../api/backend.yaml]\nsteps:\n  - use: ../sequences/typo.yaml\n");

        var errors = LoadFails(path).Errors.Select(Relative);

        Assert.Contains(errors, e => e.StartsWith("sequences/typo.yaml:4:1: unknown key 'setings' in a sequence"));
    }

    [Fact]
    public void ASequence_MayImportTheSameApiFileAsTheWorkflow()
    {
        var workflow = WorkflowLoader.Load(Workflow("  - use: ../sequences/log-in.yaml\n    with: { password: x }\n"));

        Assert.Equal(["login", "me", "ping"], workflow.Endpoints.Keys.Order());
    }

    // --- for_each over a sequence (docs/specs/0020-for-each-over-a-sequence.md) --------------------------------

    [Fact]
    public async Task ForEachOnAUse_RunsTheSequenceOncePerItem_EachWithItsOwnSaves()
    {
        var (report, server) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                with: { email: qa@example.com }
                for_each:
                  - { password: a }
                  - { password: b }
                  - { password: c }
              - call: me
            """));

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Equal(["Log in [1/3] › login", "Log in [2/3] › login", "Log in [3/3] › login", "me"], report.StepReports.Select(s => s.StepName));
        Assert.Equal(
            ["""{"email":"qa@example.com","password":"a"}""", """{"email":"qa@example.com","password":"b"}""", """{"email":"qa@example.com","password":"c"}"""],
            server.Requests.Take(3).Select(r => r.Body));
        // After the last item, its saved values stay visible.
        Assert.Equal("Bearer tok-c", server.Requests[3].Headers["Authorization"]);
    }

    [Fact]
    public async Task ForEachOnAUse_ReadsADataFile()
    {
        Write("data/passwords.csv", "password\nx\ny\n");
        var (report, server) = await RunAsync(Workflow("""
              - name: Admin
                use: ../sequences/log-in.yaml
                for_each: { file: ../data/passwords.csv }
            """));

        Assert.Equal(["Admin [1/2] › login", "Admin [2/2] › login"], report.StepReports.Select(s => s.StepName));
        Assert.Equal(["x", "y"], server.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("password").GetString()));
    }

    [Fact]
    public async Task ForEachOnAUse_StopsTheWorkflow_AtTheFirstFailingStep()
    {
        var (report, server) = await RunAsync(Workflow("""
              - use: ../sequences/log-in.yaml
                for_each: [{ password: a }, { password: wrong }, { password: c }]
            """), r => r.Body!.Contains("wrong") ? new FakeResponse(System.Net.HttpStatusCode.Unauthorized) : Respond(r));

        Assert.Equal([true, false], report.StepReports.Select(s => s.IsSuccess));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public void ForEachOnAUse_GivesEachStepItsItem_InTheInspection()
    {
        var workflow = WorkflowLoader.Load(Workflow("  - use: ../sequences/log-in.yaml\n    for_each: [{ password: a }, { password: b }]\n"));

        var steps = JsonDocument.Parse(ProtocolJson.Inspection("1.2.3", workflow)).RootElement.GetProperty("steps");
        Assert.Equal([1, 2], steps.EnumerateArray().Select(s => s.GetProperty("sequence").GetProperty("item").GetInt32()));
        Assert.All(steps.EnumerateArray(), s => Assert.Equal(2, s.GetProperty("sequence").GetProperty("items").GetInt32()));

        var plain = WorkflowLoader.Load(Workflow("  - use: ../sequences/log-in.yaml\n    with: { password: a }\n", "workflows/plain.yaml"));
        var sequence = JsonDocument.Parse(ProtocolJson.Inspection("1.2.3", plain)).RootElement.GetProperty("steps")[0].GetProperty("sequence");
        Assert.False(sequence.TryGetProperty("item", out _));
    }

    [Theory]
    [InlineData("  - use: ../sequences/log-in.yaml\n    for_each: [{ password: a }, { pasword: b }]",
        "workflows/test.yaml:5:44: item 2 of for_each: unknown param 'pasword' for sequence 'Log in' (did you mean 'password'?)")]
    [InlineData("  - use: ../sequences/log-in.yaml\n    for_each: [{ password: a }, { email: b@example.com }]",
        "workflows/test.yaml:4:5: missing required param 'password' of sequence 'Log in' in item 2 of for_each (defined at sequences/log-in.yaml:4:3)")]
    [InlineData("  - use: ../sequences/log-in.yaml\n    for_each: ${cases}",
        "workflows/test.yaml:5:15: for_each of a use step takes a list of items or { file: path }: the items of a variable are known only when the step runs")]
    [InlineData("  - use: ../sequences/log-in.yaml\n    with: { password: a }\n    for_each: [{ password: b }]",
        "workflows/test.yaml:6:18: item 1 of for_each and with both set 'password': keep one")]
    public void InvalidForEachOnAUse_IsReportedWithItsLocation(string steps, string expected)
    {
        var errors = LoadFails(Workflow(steps)).Errors.Select(Relative);

        Assert.Contains(expected, errors);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Protocol;
using Tinyprobe.Core.Reporting;
using Tinyprobe.Core.Tests.Fakes;

namespace Tinyprobe.Core.Tests.Execution;

/// <summary><c>run --resume</c> (docs/specs/0022-resume-a-run.md).</summary>
public sealed class ResumeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-resume-").FullName;
    private readonly string _path;

    public ResumeTests()
    {
        _path = Path.Combine(_directory, "users.yaml");
        File.WriteAllText(Path.Combine(_directory, "api.yaml"), """
            base_url: https://api.test
            endpoints:
              login: { method: POST, path: /login }
              create:
                method: POST
                path: /users
                body: { email: "${email}" }
              read: { path: "/users/${id}" }
              remove: { method: DELETE, path: "/users/${userId}" }
            """);
        Write(Yaml);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private const string Yaml = """
        name: Users
        import: [api.yaml]
        variables:
          email: "smoke-${$uuid}@example.com"
        steps:
          - name: Log in
            call: login
          - name: Create a user
            call: create
            save: { userId: $.id }
          - sleep: 1ms
          - name: Read the user
            call: read
            with: { id: "${userId}" }
          - name: Delete the user
            call: remove
        """;

    private void Write(string yaml) => File.WriteAllText(_path, yaml);

    private WorkflowDefinition Load(string? to, params string[] skip)
    {
        var workflow = WorkflowLoader.Load(_path);
        if (to is null && skip.Length == 0)
            return workflow;
        Assert.True(WorkflowLoader.TryNarrow(workflow, to, skip, out var narrowed, out var error), error);
        return narrowed;
    }

    private static FakeServer Server(CookieContainer? cookies = null) => new(r => r.Path switch
    {
        "/login" => new FakeResponse(Body: """{"token":"t"}""") { SetCookies = ["session=abc; Path=/; HttpOnly"] },
        "/users" => new FakeResponse(HttpStatusCode.Created, """{"id":56}"""),
        _ => new FakeResponse()
    }) { Cookies = cookies };

    /// <summary>Runs up to <paramref name="to"/>, as <c>run --to</c> does, and returns the state of its run_finished.</summary>
    private async Task<(RunState State, FakeServer Server)> RunTo(string to, string? env = null, params string[] skip)
    {
        var cookies = new CookieContainer();
        var server = Server(cookies);
        var engine = new WorkflowEngine(new HttpClient(server), cookies: cookies) { EnvironmentFile = env };
        var workflow = Load(to, skip);
        var report = await engine.ExecuteAsync(workflow);
        Assert.NotNull(report.State);

        // The state goes through run_finished and back, as the app passes it.
        var finished = JsonDocument.Parse(ProtocolJson.RunFinished(WorkflowSummary.Create(workflow, report), TimeSpan.Zero));
        return (RunState.Parse(finished.RootElement.GetProperty("state").GetRawText()), server);
    }

    private WorkflowDefinition Resume(RunState state, string? to = null, string? env = null, params string[] skip)
    {
        Assert.True(WorkflowLoader.TryResume(Load(to, skip), env, to, state, out var resumed, out var error), error);
        return resumed;
    }

    private string ResumeError(RunState state, string? to = null, string? env = null)
    {
        Assert.False(WorkflowLoader.TryResume(Load(to), env, to, state, out _, out var error));
        return error;
    }

    [Fact]
    public async Task ARunCutByTo_GivesItsState_AndTheNextRunGoesOnFromIt()
    {
        var (state, _) = await RunTo("2");

        Assert.Equal(1, state.After);
        Assert.Equal(_path, state.File);
        Assert.Null(state.EnvFile);
        Assert.Equal(["userId"], state.Saved.Select(v => v.Key));
        Assert.Equal(56, state.Saved[0].Value.GetInt32());
        Assert.Equal(["email"], state.Variables.Select(v => v.Key));
        var cookie = Assert.Single(state.Cookies);
        Assert.Equal(("session", "abc", true), (cookie.Name, cookie.Value, cookie.HttpOnly));

        var cookies = new CookieContainer();
        var server = Server(cookies);
        var workflow = Resume(state, to: "4");
        var started = new List<int>();
        var engine = new WorkflowEngine(new HttpClient(server), cookies: cookies);
        engine.StepStarted += s => started.Add(s.Index);
        var report = await engine.ExecuteAsync(workflow);
        var summary = WorkflowSummary.Create(workflow, report);

        Assert.Equal([2, 3], started); // the sleep, then Read the user
        Assert.Equal(["/users/56"], server.Requests.Select(r => r.Path));
        Assert.Equal("session=abc", server.Requests[0].Headers["Cookie"]);
        Assert.True(summary.IsSuccess);
        Assert.Equal((1, 1, 1, 3), (summary.TotalSteps, summary.PassedSteps, summary.ResumedAfter, summary.StoppedAfter));
        Assert.NotNull(report.State); // and it can be continued again
        Assert.Equal(3, report.State!.After);
    }

    [Fact]
    public async Task AGeneratedVariable_KeepsItsValue_AfterTheResume()
    {
        File.WriteAllText(_path, Yaml.Replace("call: remove", "call: create"));
        var (state, first) = await RunTo("2");

        var server = Server();
        await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(Resume(state));

        var email = JsonDocument.Parse(first.Requests[1].Body!).RootElement.GetProperty("email").GetString();
        Assert.StartsWith("smoke-", email);
        Assert.Equal(email, JsonDocument.Parse(server.Requests.Last().Body!).RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task OnlyARunCutByToThatPassed_HasAState()
    {
        var whole = await new WorkflowEngine(new HttpClient(Server())).ExecuteAsync(Load(null));
        Assert.Null(whole.State);

        var failing = new FakeServer(r => r.Path == "/users" ? new FakeResponse(HttpStatusCode.BadRequest) : new FakeResponse());
        Assert.Null((await new WorkflowEngine(new HttpClient(failing)).ExecuteAsync(Load("4"))).State);

        // --to on the last step: nothing is left to continue.
        Assert.Null((await new WorkflowEngine(new HttpClient(Server())).ExecuteAsync(Load("5"))).State);

        var finished = JsonDocument.Parse(ProtocolJson.RunFinished(WorkflowSummary.Create(Load(null), whole), TimeSpan.Zero)).RootElement;
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("summary").GetProperty("resumed_after").ValueKind);
    }

    [Fact]
    public async Task EditingALaterStep_KeepsTheState_EditingOneThatRan_OrItsEndpoint_DoesNot()
    {
        var (state, _) = await RunTo("2");

        Write(Yaml.Replace("name: Read the user", "name: Read the new user"));
        Resume(state);

        Write(Yaml.Replace("save: { userId: $.id }", "save: { userId: $.user.id }"));
        Assert.Equal("--resume: the steps up to step 2 changed since the state was made: run from the start", ResumeError(state));

        Write(Yaml);
        File.WriteAllText(Path.Combine(_directory, "api.yaml"),
            File.ReadAllText(Path.Combine(_directory, "api.yaml")).Replace("path: /users\n", "path: /v2/users\n"));
        Assert.Equal("--resume: the steps up to step 2 changed since the state was made: run from the start", ResumeError(state));
    }

    [Fact]
    public async Task AStateOfAnotherFile_OrEnvironment_OrBeyondTo_IsAnError()
    {
        var env = Path.Combine(_directory, "env.local.yaml");
        File.WriteAllText(env, "x: 1");
        File.WriteAllText(Path.Combine(_directory, "env.staging.yaml"), "x: 2");
        var (state, _) = await RunTo("2", env);
        var shown = (string name) => Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_directory, name));

        Assert.Equal($"--resume: the state was made with -e {shown("env.local.yaml")}: run from the start to use {shown("env.staging.yaml")}",
            ResumeError(state, env: Path.Combine(_directory, "env.staging.yaml")));
        Assert.Equal($"--resume: the state was made with -e {shown("env.local.yaml")}: run from the start to use no environment file",
            ResumeError(state));
        Assert.Equal("--to 2: the state already ran steps 1 to 2: --to must come after step 2", ResumeError(state, to: "2", env: env));
        Assert.Equal("--to \"Log in\": the state already ran steps 1 to 2: --to must come after step 2", ResumeError(state, to: "Log in", env: env));

        File.Copy(_path, Path.Combine(_directory, "orders.yaml"));
        Assert.False(WorkflowLoader.TryResume(WorkflowLoader.Load(Path.Combine(_directory, "orders.yaml")), env, null, state, out _, out var other));
        Assert.Equal($"--resume: the state is of {shown("users.yaml")}, not {shown("orders.yaml")}", other);

        Assert.Equal("--resume: the state is after the last step, nothing is left to run", ResumeError(state with { After = 4 }, env: env));
        Assert.False(WorkflowLoader.TryResume(Load(null, "3", "4", "5"), env, null, state, out _, out var skipped));
        Assert.Equal("--skip: every step after step 2 is skipped, nothing would run", skipped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"event":"run_finished"}""")]
    [InlineData("""{"state":1,"file":"x"}""")]
    [InlineData("[]")]
    public void SomethingElseThanAState_IsAnError(string json) =>
        Assert.Equal("--resume: not a tinyprobe state (expected the \"state\" of a run_finished event)",
            Assert.Throws<RunStateException>(() => RunState.Parse(json)).Message);

    [Fact]
    public void AStateOfANewerFormat_NeedsANewerTinyprobe() =>
        Assert.Equal("--resume: this state needs a newer tinyprobe",
            Assert.Throws<RunStateException>(() => RunState.Parse("""{"state":2}""")).Message);

    [Fact]
    public async Task ASkippedStepBeforeTheState_StillExplainsAMissingValue()
    {
        var (state, _) = await RunTo("3", null, "2");

        var workflow = Resume(state, skip: "2");
        var report = await new WorkflowEngine(new HttpClient(Server())).ExecuteAsync(workflow);

        Assert.Empty(workflow.SkippedInRun);
        Assert.Equal("Variable 'userId' is not defined: step 2 (Create a user), which saves it, was skipped.", report.StepReports[0].FailureReason);
        Assert.Equal(0, WorkflowSummary.Create(workflow, report).SkippedSteps);
    }

    [Fact]
    public async Task TheEventsAndTheJunitReport_CoverOnlyTheResumedRun()
    {
        var (state, _) = await RunTo("2");
        var workflow = Resume(state);
        var report = await new WorkflowEngine(new HttpClient(Server())).ExecuteAsync(workflow);

        var started = JsonDocument.Parse(ProtocolJson.RunStarted("1.2.3", workflow, null)).RootElement;
        Assert.Equal(1, started.GetProperty("resumed_after").GetInt32());
        Assert.Equal(5, started.GetProperty("steps").GetArrayLength());
        var summary = JsonDocument.Parse(ProtocolJson.RunFinished(WorkflowSummary.Create(workflow, report), TimeSpan.Zero)).RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("resumed_after").GetInt32());
        Assert.Equal(2, summary.GetProperty("total_steps").GetInt32());

        var xml = XDocument.Parse(JunitReport.Write("1.2.3", [new JunitRun(_path, workflow, report, TimeSpan.Zero, DateTime.UtcNow)]));
        Assert.Equal(["Read the user", "Delete the user"], xml.Descendants("testcase").Select(c => (string)c.Attribute("name")!));
    }

    [Fact]
    public async Task ARunCanBeResumed_BetweenTwoStepsOfASequence()
    {
        File.WriteAllText(Path.Combine(_directory, "log-in.yaml"), """
            name: Log in
            import: [api.yaml]
            params: { who: admin }
            sequence:
              - name: Get a token
                call: login
                save: { token: $.token }
              - name: Read me
                call: read
                with: { id: "${who}-${token}" }
            """);
        Write("""
            name: Flow
            import: [api.yaml]
            steps:
              - use: log-in.yaml
                with: { who: ada }
              - name: Done
                call: login
            """);
        var (state, _) = await RunTo("1");

        var server = Server();
        var report = await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(Resume(state));

        Assert.All(report.StepReports, s => Assert.True(s.IsSuccess, s.FailureReason));
        Assert.Equal(["/users/ada-t", "/login"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public void ACookieOfADomain_StaysADomainCookie_AndAHostOnlyOne_StaysHostOnly()
    {
        var jar = new CookieContainer();
        jar.SetCookies(new Uri("https://api.example.com/"), "host=1; Path=/");
        jar.SetCookies(new Uri("https://api.example.com/"), "wide=2; Domain=example.com; Path=/");
        var state = new RunState { File = "f", After = 0, Steps = "s", Cookies = RunState.CookiesOf(jar) };

        var restored = new CookieContainer();
        RunState.Parse(Json(state)).RestoreCookies(restored);

        Assert.Equal("host=1; wide=2", restored.GetCookieHeader(new Uri("https://api.example.com/")));
        Assert.Equal("wide=2", restored.GetCookieHeader(new Uri("https://www.example.com/")));
        Assert.Equal([".example.com", "api.example.com"], state.Cookies.Select(c => c.Domain).Order());
    }

    private static string Json(RunState state)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
            state.Write(w);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

using System.Net;
using System.Text.Json;
using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Execution;
using Tinyprobe.Core.Parsing;
using Tinyprobe.Core.Tests.Fakes;

namespace Tinyprobe.Core.Tests.Execution;

/// <summary>
/// Global variables (spec 0023): an endpoint's <c>globals</c> saved from its 2xx responses
/// for the next single calls, and the JSON file that holds them between calls.
/// </summary>
public sealed class GlobalVariablesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-globals-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Api = """
        base_url: https://api.test
        auth:
          bearer:
            Authorization: "Bearer ${token}"
        endpoints:
          login:
            method: POST
            path: /login
            globals:
              token: $.access_token
              user_id: $.user.id
          me:
            path: /me
            auth: bearer
        """;

    private static WorkflowDefinition Call(WorkflowDefinition source, string endpoint) =>
        WorkflowLoader.CreateCall(source, endpoint, []);

    private const string LoginBody = """{"access_token":"abc","user":{"id":7}}""";

    [Fact]
    public void Loader_ReadsTheGlobalsOfAnEndpoint()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));

        var globals = api.Endpoints["login"].Globals;

        Assert.Equal(["token", "user_id"], globals.Keys);
        Assert.Equal("$.access_token", globals["token"].Path);
        Assert.Empty(api.Endpoints["me"].Globals);
    }

    [Fact]
    public void Loader_ReportsAnInvalidJsonPathInGlobals()
    {
        var path = Write("api.yaml", "endpoints:\n  login:\n    path: /login\n    globals:\n      token: access_token\n");

        var error = Assert.Throws<WorkflowLoadException>(() => WorkflowLoader.LoadAny(path)).Problems.Single();

        Assert.Equal((5, 14), (error.Line, error.Column));
        Assert.Contains("invalid JSONPath 'access_token'", error.Message);
    }

    [Fact]
    public async Task Call_SavesTheGlobalsOfA2xxResponse_OnlyWhenAsked()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));
        var server = new FakeServer(_ => new FakeResponse(Body: LoginBody));

        var ignored = await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(Call(api, "login"));
        var kept = await new WorkflowEngine(new HttpClient(server)) { SaveGlobals = true }.ExecuteAsync(Call(api, "login"));

        Assert.Empty(ignored.Single().Globals);
        Assert.Equal(["token=\"abc\"", "user_id=7"],
            kept.Single().Globals.Select(g => $"{g.Key}={g.Value.GetRawText()}"));
    }

    [Fact]
    public async Task Call_SavesNothingFromAFailedResponse()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));
        var server = new FakeServer(_ => new FakeResponse(HttpStatusCode.Unauthorized, """{"error":"invalid"}"""));

        var report = await new WorkflowEngine(new HttpClient(server)) { SaveGlobals = true }.ExecuteAsync(Call(api, "login"));

        Assert.True(report.Single().IsSuccess);   // a single call accepts any status
        Assert.Empty(report.Single().Globals);
        Assert.Empty(report.Single().GlobalsMissing);
    }

    [Fact]
    public async Task Call_ReportsAGlobalMissingFromTheResponse_WithoutFailing()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));
        var server = new FakeServer(_ => new FakeResponse(Body: """{"access_token":"abc"}"""));

        var report = await new WorkflowEngine(new HttpClient(server)) { SaveGlobals = true }.ExecuteAsync(Call(api, "login"));

        Assert.True(report.Single().IsSuccess);
        Assert.Equal(["token"], report.Single().Globals.Select(g => g.Key));
        Assert.Equal(["user_id: $.user.id not found in the response"], report.Single().GlobalsMissing);
    }

    [Fact]
    public async Task Call_ToAnEndpointThatUsesAMissingGlobal_SaysWhichEndpointSavesIt()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));

        var report = await new WorkflowEngine(new HttpClient(new FakeServer())) { SaveGlobals = true }.ExecuteAsync(Call(api, "me"));

        Assert.Equal("Variable 'token' is not defined: call 'login' first, it saves it as a global variable.",
            report.Single().FailureReason);
    }

    [Fact]
    public async Task Call_UsesAGlobalGivenAsAVariable()
    {
        var api = WorkflowLoader.LoadAny(Write("api.yaml", Api));
        var server = new FakeServer();
        var environment = new Dictionary<string, JsonElement> { ["token"] = WorkflowLoader.ParseYamlValue("abc") };

        await new WorkflowEngine(new HttpClient(server)).ExecuteAsync(Call(api, "me"), environment);

        Assert.Equal("Bearer abc", server.Requests.Single().Headers["Authorization"]);
    }

    [Fact]
    public void File_UpdateReplacesInPlaceAndAddsNewOnesLast()
    {
        var path = Path.Combine(_directory, "globals.local.json");
        File.WriteAllText(path, """{ "token": "old", "lang": { "value": "it" } }""");

        GlobalsFile.Update(path, [
            new GlobalsFile.Entry("user_id", WorkflowLoader.ParseYamlValue("7"), "login", "2026-11-24T10:00:00Z"),
            new GlobalsFile.Entry("token", WorkflowLoader.ParseYamlValue("abc"), "login", "2026-11-24T10:00:00Z")]);

        var entries = GlobalsFile.Load(path);
        Assert.Equal(["token=\"abc\" login", "lang=\"it\" ", "user_id=7 login"],
            entries.Select(e => $"{e.Name}={e.Value.GetRawText()} {e.From}"));
    }

    [Fact]
    public void File_MissingIsEmpty_InvalidIsAnError()
    {
        Assert.Empty(GlobalsFile.Load(Path.Combine(_directory, "none.json")));

        var invalid = Write("bad.json", "[1, 2]");
        Assert.Contains("must be a JSON object", Assert.Throws<GlobalsFileException>(() => GlobalsFile.Load(invalid)).Message);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Tinyprobe.Cli.Mcp;

namespace Tinyprobe.Cli.Tests;

public sealed class McpServerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tinyprobe-mcp-").FullName;
    private readonly Server _server = new();
    private readonly McpServer _mcp;

    public McpServerTests() => _mcp = new McpServer("1.2.3", _server.CreateHandler);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Answers 200 with the request body echoed, except /broken (500); records every request.</summary>
    private sealed class Server
    {
        public readonly List<(string Path, string? Body)> Requests = [];

        public HttpMessageHandler CreateHandler(CookieContainer cookies) => new Handler(this);

        private sealed class Handler(Server server) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (server.Requests)
                    server.Requests.Add((request.RequestUri!.AbsolutePath, body));
                var status = request.RequestUri.AbsolutePath == "/broken" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body ?? """{"ok":true}""", Encoding.UTF8, "application/json")
                };
            }
        }
    }

    private const string Api = """
        base_url: https://api.test
        endpoints:
          ok:
            path: /ok
          broken:
            path: /broken
          echo:
            method: POST
            path: /echo
            params:
              id:
            body:
              id: ${id}
        """;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Workflow(string name, string steps) =>
        Write(name, $"name: {Path.GetFileNameWithoutExtension(name)}\nimport: [api.yaml]\nsteps:\n{steps}");

    private async Task<JsonElement> Send(string json)
    {
        var answer = await _mcp.HandleAsync(json);
        Assert.NotNull(answer);
        return JsonDocument.Parse(answer).RootElement.Clone();
    }

    private Task<JsonElement> CallTool(string name, object arguments) =>
        Send(JsonSerializerLite.Request(7, "tools/call", $$"""{"name":"{{name}}","arguments":{{JsonSerializerLite.Encode(arguments)}}}"""));

    /// <summary>The text of a tool result, and whether it is an error.</summary>
    private static (string Text, bool IsError) ToolResult(JsonElement answer)
    {
        var result = answer.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2099-01-01", "2025-06-18")]
    public async Task Initialize_AgreesOnAVersion_AndOffersTools(string requested, string answered)
    {
        var answer = await Send(JsonSerializerLite.Request(1, "initialize",
            "{\"protocolVersion\":" + JsonSerializerLite.Encode(requested) + ",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}"));

        Assert.Equal("2.0", answer.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, answer.GetProperty("id").GetInt32());
        var result = answer.GetProperty("result");
        Assert.Equal(answered, result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("tinyprobe", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("1.2.3", result.GetProperty("serverInfo").GetProperty("version").GetString());
        Assert.Contains("guide", result.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task NotificationsAndResponses_GetNoAnswer()
    {
        Assert.Null(await _mcp.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(await _mcp.HandleAsync("""{"jsonrpc":"2.0","id":5,"result":{}}"""));
    }

    [Fact]
    public async Task ProtocolErrors_AreJsonRpcErrors()
    {
        Assert.Equal(-32700, (await Send("{not json")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32601, (await Send("""{"jsonrpc":"2.0","id":2,"method":"resources/list"}""")).GetProperty("error").GetProperty("code").GetInt32());

        var unknownTool = await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"launch"}}""");
        Assert.Equal(3, unknownTool.GetProperty("id").GetInt32());
        Assert.Equal(-32602, unknownTool.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Unknown tool: launch", unknownTool.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ToolsList_HasTheFourTools_WithSchemasAndHints()
    {
        var tools = (await Send("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).GetProperty("result").GetProperty("tools");

        Assert.Equal(["guide", "inspect", "call", "run"], tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal([true, true, false, false],
            tools.EnumerateArray().Select(t => t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.Equal(["file", "endpoint"],
            tools[2].GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("array", tools[3].GetProperty("inputSchema").GetProperty("properties").GetProperty("paths").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Guide_IsTheGuideOfTheBinary()
    {
        var (text, isError) = ToolResult(await CallTool("guide", new { }));

        Assert.False(isError);
        Assert.Equal(GuideCommand.Text, text);
    }

    [Fact]
    public async Task Inspect_ReturnsTheInspection_OrTheProblems()
    {
        var api = Write("api.yaml", Api);
        var (text, isError) = ToolResult(await CallTool("inspect", new { file = api }));
        Assert.False(isError);
        Assert.Equal(["ok", "broken", "echo"], Json(text).GetProperty("endpoints").EnumerateArray().Select(e => e.GetProperty("name").GetString()));

        var broken = Workflow("w.yaml", "  - call: okk\n");
        (text, isError) = ToolResult(await CallTool("inspect", new { file = broken }));
        Assert.True(isError);
        var error = Assert.Single(Json(text).GetProperty("errors").EnumerateArray());
        Assert.Contains("unknown endpoint 'okk' (did you mean 'ok'?)", error.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("42", """{"id":42}""")]
    [InlineData("\"42\"", """{"id":"42"}""")]
    public async Task Call_KeepsTheJsonTypeOfArguments(string id, string sentBody)
    {
        var api = Write("api.yaml", Api);

        var answer = await Send(JsonSerializerLite.Request(1, "tools/call",
            "{\"name\":\"call\",\"arguments\":{\"file\":" + JsonSerializerLite.Encode(api) + ",\"endpoint\":\"echo\",\"with\":{\"id\":" + id + "}}}"));

        var (text, isError) = ToolResult(answer);
        Assert.False(isError);
        Assert.Equal(sentBody, _server.Requests.Single().Body);
        var result = Json(text);
        Assert.Equal("echo", result.GetProperty("endpoint").GetString());
        Assert.Equal(sentBody, result.GetProperty("request").GetProperty("body").GetString());
        Assert.Equal(200, result.GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Call_ShowsAnyStatus_AsAResult()
    {
        var api = Write("api.yaml", Api);

        var (text, isError) = ToolResult(await CallTool("call", new { file = api, endpoint = "broken" }));

        Assert.False(isError);
        Assert.Equal(500, Json(text).GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Run_RunsEveryWorkflow_AndShowsTheExchangeOfFailedStepsOnly()
    {
        Write("flows/api.yaml", Api); // in the folder, and skipped: it is not a workflow
        Workflow("flows/a.yaml", "  - call: ok\n  - call: broken\n  - call: ok\n");
        Workflow("flows/b.yaml", "  - call: ok\n");

        var (text, isError) = ToolResult(await CallTool("run", new { paths = new[] { Path.Combine(_directory, "flows") } }));

        Assert.False(isError);
        var result = Json(text);
        Assert.False(result.GetProperty("success").GetBoolean());
        var workflows = result.GetProperty("workflows").EnumerateArray().ToList();
        Assert.Equal([false, true], workflows.Select(w => w.GetProperty("success").GetBoolean()));
        Assert.Equal(1, workflows[0].GetProperty("summary").GetProperty("not_run_steps").GetInt32());

        var steps = workflows[0].GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.False(steps[0].TryGetProperty("response", out _));
        Assert.Equal("Expected a 2xx status, got 500 (set expect.status to expect another one)", steps[1].GetProperty("failure").GetString());
        Assert.Equal(500, steps[1].GetProperty("response").GetProperty("status").GetInt32());
        Assert.Equal(3, _server.Requests.Count); // a stops at broken; b still runs
    }

    [Fact]
    public async Task Run_SendsNothing_WhenAWorkflowIsInvalid()
    {
        Write("api.yaml", Api);
        var valid = Workflow("a.yaml", "  - call: ok\n");
        var invalid = Workflow("b.yaml", "  - call: brokn\n");

        var (text, isError) = ToolResult(await CallTool("run", new { paths = new[] { valid, invalid }, vars = new { token = "t" } }));

        Assert.True(isError);
        Assert.Empty(_server.Requests);
        Assert.Contains("did you mean 'broken'?", Json(text).GetProperty("errors")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Run_WithTo_StopsAfterThatStep_ByNumberOrName()
    {
        Write("api.yaml", Api);
        var workflow = Workflow("a.yaml", "  - call: ok\n  - name: Second\n    call: ok\n  - call: broken\n");

        var (byNumber, numberError) = ToolResult(await CallTool("run", new { paths = workflow, to = 2 }));
        var (byName, nameError) = ToolResult(await CallTool("run", new { paths = workflow, to = "Second" }));

        Assert.False(numberError);
        Assert.False(nameError);
        foreach (var text in new[] { byNumber, byName })
        {
            var run = Json(text).GetProperty("workflows")[0];
            Assert.True(run.GetProperty("success").GetBoolean());
            Assert.Equal(1, run.GetProperty("summary").GetProperty("stopped_after").GetInt32());
            Assert.Equal(2, run.GetProperty("summary").GetProperty("total_steps").GetInt32());
        }
        Assert.Equal(4, _server.Requests.Count); // the broken step never ran
    }

    [Fact]
    public async Task Run_WithSkip_LeavesTheStepsOut_AndListsThemInTheirPlace()
    {
        Write("api.yaml", Api);
        var workflow = Workflow("a.yaml", "  - call: ok\n  - name: Broken\n    call: broken\n  - call: ok\n  - call: broken\n");

        var (text, isError) = ToolResult(await CallTool("run", new { paths = workflow, skip = new object[] { "Broken", 4 } }));

        Assert.False(isError);
        var run = Json(text).GetProperty("workflows")[0];
        Assert.True(run.GetProperty("success").GetBoolean());
        Assert.Equal(2, run.GetProperty("summary").GetProperty("skipped_steps").GetInt32());
        Assert.Equal(2, run.GetProperty("summary").GetProperty("total_steps").GetInt32());
        var steps = run.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2, 3], steps.Select(s => s.GetProperty("index").GetInt32()));
        Assert.Equal([false, true, false, true], steps.Select(s => s.TryGetProperty("skipped", out var skipped) && skipped.GetBoolean()));
        Assert.Equal("Broken", steps[1].GetProperty("name").GetString());
        Assert.Equal(2, _server.Requests.Count);

        Assert.Equal(("'skip' applies to one workflow file: give one path, a workflow file.", true),
            ToolResult(await CallTool("run", new { paths = _directory, skip = new[] { 1 } })));
        Assert.Equal(("--skip 9: the workflow has 4 steps", true), ToolResult(await CallTool("run", new { paths = workflow, skip = new[] { 9 } })));
    }

    [Fact]
    public async Task Run_WithTo_NeedsOneWorkflowFile_AndAStepOfIt()
    {
        Write("flows/api.yaml", Api);
        var workflow = Workflow("flows/a.yaml", "  - call: ok\n");

        Assert.Equal(("'to' runs one workflow file: give one path, a workflow file.", true),
            ToolResult(await CallTool("run", new { paths = Path.Combine(_directory, "flows"), to = 1 })));
        Assert.Equal(("--to 5: the workflow has 1 step", true), ToolResult(await CallTool("run", new { paths = workflow, to = 5 })));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Run_AcceptsOnePathAsAString()
    {
        Write("api.yaml", Api);
        var workflow = Workflow("a.yaml", "  - call: ok\n");

        var (text, isError) = ToolResult(await CallTool("run", new { paths = workflow }));

        Assert.False(isError);
        Assert.True(Json(text).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WrongArguments_AreToolErrors_InOneSentence()
    {
        var api = Write("api.yaml", Api);

        Assert.Equal(("Missing argument 'file'.", true), ToolResult(await CallTool("inspect", new { })));
        Assert.Equal(($"File not found: {Path.Combine(_directory, "nope.yaml")}", true),
            ToolResult(await CallTool("inspect", new { file = Path.Combine(_directory, "nope.yaml") })));
        Assert.Equal(("Argument 'with' must be an object, for example { \"id\": 42 }.", true),
            ToolResult(await CallTool("call", new { file = api, endpoint = "echo", with = "id=1" })));
        Assert.StartsWith("Missing argument 'paths'", ToolResult(await CallTool("run", new { })).Text);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Session_AnswersEachRequestInOrder_OnOneLineEach()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}

            {"jsonrpc":"2.0","id":"two","method":"ping"}
            {"jsonrpc":"2.0","id":3,"method":"tools/list"}
            """);
        var output = new StringWriter();

        await _mcp.RunAsync(input, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(["1", "\"two\"", "3"], lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetRawText()));
    }
}

/// <summary>Builds test messages without reflection-based serialization, which the AOT test settings may not allow.</summary>
internal static class JsonSerializerLite
{
    public static string Request(int id, string method, string paramsJson) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";

    public static string Encode(object value) => JsonSerializer.Serialize(value);
}

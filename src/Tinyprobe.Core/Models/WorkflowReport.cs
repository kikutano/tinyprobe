using System.Net;
using System.Text.Json;

namespace Tinyprobe.Core.Models;

public class StepReport
{
    /// <summary>Position of the step in the workflow, sleeps included (0-based).</summary>
    public int Index { get; set; }
    public string StepName { get; set; } = string.Empty;
    /// <summary>For a <c>for_each</c> step: this iteration (1-based); null otherwise, and for a step with no items.</summary>
    public int? Iteration { get; set; }
    /// <summary>For a <c>for_each</c> step: how many items it has (0 when the list is empty); null for other steps.</summary>
    public int? Iterations { get; set; }
    public string? Method { get; set; }
    public Uri? Url { get; set; }
    /// <summary>Status code received from the server; null when no response was received.</summary>
    public HttpStatusCode? StatusCode { get; set; }
    /// <summary>The status codes accepted by <c>expect.status</c>; empty when the step accepts any 2xx.</summary>
    public List<HttpStatusCode> ExpectedStatusCodes { get; set; } = new();
    public bool IsSuccess { get; set; } = true;
    /// <summary>Why the step failed when the reason is not a failed assertion (status, error, ...).</summary>
    public string? FailureReason { get; set; }
    public List<AssertionResult> Assertions { get; set; } = new();
    /// <summary>Duration of the HTTP request (last attempt when retried).</summary>
    public TimeSpan Duration { get; set; }
    public long ExecutionTimeMilliseconds => (long)Duration.TotalMilliseconds;
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    /// <summary>How many times the call was sent: more than 1 for an <c>until</c> step that polled.</summary>
    public int Attempts { get; set; } = 1;
    /// <summary>The values stored by <c>save</c>, in order.</summary>
    public List<KeyValuePair<string, JsonElement>> Saved { get; set; } = new();
    /// <summary>The request sent (last attempt); recorded only when the engine captures exchanges.</summary>
    public RecordedRequest? Request { get; set; }
    /// <summary>The response received (last attempt); recorded only when the engine captures exchanges.</summary>
    public RecordedResponse? Response { get; set; }
}

/// <summary>The outcome of one condition of <c>expect</c>, e.g. <c>$.total &gt;= 3</c>.</summary>
public class AssertionResult
{
    /// <summary>What was checked: a JSONPath, <c>header Name</c> or <c>duration</c>.</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>The condition, e.g. <c>== "admin"</c>, <c>&gt;= 3</c>, <c>exists</c>.</summary>
    public string Condition { get; set; } = string.Empty;
    /// <summary>The actual value as JSON, or <c>(missing)</c>.</summary>
    public string ActualValue { get; set; } = string.Empty;
    public bool Passed { get; set; }
}

public sealed class RecordedRequest
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    /// <summary>Request and content headers, in the order they were set.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
    public string? Body { get; init; }
}

public sealed class RecordedResponse
{
    public required int Status { get; init; }
    public string? Reason { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
    /// <summary>The body as UTF-8 text, cut at <c>WorkflowEngine.MaxRecordedBodyBytes</c>.</summary>
    public required string Body { get; init; }
    public bool BodyTruncated { get; init; }
}

public class WorkflowReport
{
    /// <summary>A run cut by <c>--to</c> in which every step passed: what <c>run --resume</c> needs to continue it (spec 0022).</summary>
    public Execution.RunState? State { get; set; }

    public string WorkflowName { get; set; } = string.Empty;
    public List<StepReport> StepReports { get; set; } = new List<StepReport>();
}

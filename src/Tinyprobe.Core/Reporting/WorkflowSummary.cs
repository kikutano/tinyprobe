using Tinyprobe.Core.Definitions;
using Tinyprobe.Core.Models;

namespace Tinyprobe.Core.Reporting;

/// <summary>Aggregated metrics of a workflow run, in the spirit of the k6 end-of-test summary.</summary>
public sealed class WorkflowSummary
{
    /// <summary>Call steps of the workflow (sleeps excluded); each item of a <c>for_each</c> step that ran counts as a step.</summary>
    public int TotalSteps { get; init; }
    public int PassedSteps { get; init; }
    public int FailedSteps { get; init; }
    /// <summary>Steps skipped because an earlier step failed.</summary>
    public int NotRunSteps => TotalSteps - PassedSteps - FailedSteps;

    /// <summary>Status code expectations and asserts that were evaluated.</summary>
    public int ChecksPassed { get; init; }
    public int ChecksFailed { get; init; }

    /// <summary>Steps that received an HTTP response.</summary>
    public int HttpRequests { get; init; }
    /// <summary>Duration statistics of the HTTP requests; null when no request completed.</summary>
    public DurationStatistics? HttpRequestDuration { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }

    /// <summary><c>run --to</c>: the index of the last step run; the steps after it are not counted. Null for a whole run.</summary>
    public int? StoppedAfter { get; init; }
    /// <summary>The steps of the workflow file, those after <see cref="StoppedAfter"/> included (sleeps too).</summary>
    public int StepsInFile { get; init; }
    /// <summary><c>run --skip</c>: the steps left out of the run, sleeps included; they are not in <see cref="TotalSteps"/>.</summary>
    public int SkippedSteps { get; init; }
    /// <summary><c>run --resume</c>: the index of the last step the state's run ran; those steps are not counted.</summary>
    public int? ResumedAfter { get; init; }
    /// <summary>What <c>run --resume</c> needs to continue this run, or null (spec 0022).</summary>
    public Execution.RunState? State { get; init; }

    public bool IsSuccess => FailedSteps == 0 && NotRunSteps == 0;

    public static WorkflowSummary Create(WorkflowDefinition workflow, WorkflowReport report)
    {
        var responses = report.StepReports.Where(s => s.StatusCode is not null).ToList();

        int checksPassed = 0, checksFailed = 0;
        foreach (var step in report.StepReports)
        {
            if (step.ExpectedStatusCodes.Count > 0 && step.StatusCode is { } status)
            {
                if (step.ExpectedStatusCodes.Contains(status)) checksPassed++;
                else checksFailed++;
            }

            checksPassed += step.Assertions.Count(a => a.Passed);
            checksFailed += step.Assertions.Count(a => !a.Passed);
        }

        return new WorkflowSummary
        {
            // A for_each step is one step until it runs, then one per item.
            TotalSteps = workflow.Steps.Where((s, i) => s is CallStep && workflow.RunsStep(i) && !workflow.Skipped.Contains(i)).Count()
                + report.StepReports.Count(s => s.Iteration > 1),
            SkippedSteps = workflow.SkippedInRun.Count(),
            ResumedAfter = workflow.ResumedAfter,
            State = report.State,
            PassedSteps = report.StepReports.Count(s => s.IsSuccess),
            FailedSteps = report.StepReports.Count(s => !s.IsSuccess),
            StoppedAfter = workflow.StoppedAfter,
            StepsInFile = workflow.StoppedAfter is null ? workflow.Steps.Count : workflow.StepsInFile,
            ChecksPassed = checksPassed,
            ChecksFailed = checksFailed,
            HttpRequests = responses.Count,
            HttpRequestDuration = DurationStatistics.From(responses.Select(s => s.Duration)),
            BytesSent = report.StepReports.Sum(s => s.BytesSent),
            BytesReceived = report.StepReports.Sum(s => s.BytesReceived)
        };
    }
}

public sealed record DurationStatistics(
    TimeSpan Average, TimeSpan Min, TimeSpan Median, TimeSpan Max, TimeSpan P90, TimeSpan P95)
{
    public static DurationStatistics? From(IEnumerable<TimeSpan> durations)
    {
        var sorted = durations.Order().ToArray();
        if (sorted.Length == 0)
            return null;

        return new DurationStatistics(
            Average: TimeSpan.FromTicks((long)sorted.Average(d => d.Ticks)),
            Min: sorted[0],
            Median: Percentile(sorted, 50),
            Max: sorted[^1],
            P90: Percentile(sorted, 90),
            P95: Percentile(sorted, 95));
    }

    /// <summary>Percentile with linear interpolation between the closest ranks.</summary>
    private static TimeSpan Percentile(TimeSpan[] sorted, double percentile)
    {
        double rank = percentile / 100 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        double fraction = rank - lower;
        return TimeSpan.FromTicks((long)(sorted[lower].Ticks + (sorted[upper].Ticks - sorted[lower].Ticks) * fraction));
    }
}

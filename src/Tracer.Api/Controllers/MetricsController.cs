using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Delivery metrics derived from cycles, issues, states, and the activity spine:
/// velocity, burndown, throughput, and cycle-time percentiles.
///
/// <para>
/// The subtle part is <i>when</i> an issue started and finished. Nothing stores
/// that directly; it is reconstructed from the state-change entries in the audit
/// log — see <see cref="IssueLifecycles"/>. The reconstruction is deliberately
/// conservative: a transition into a state whose name no longer maps to a category
/// (a renamed or deleted state) is skipped rather than guessed at, so a metric is
/// never built on a state that is not there any more.
/// </para>
/// </summary>
[ApiController]
public class MetricsController(TracerDbContext db, TeamAccess access) : ControllerBase
{
    /// <summary>
    /// Completed story points per completed cycle, plus the trailing average — the
    /// number a plan for the next cycle should be sized against.
    ///
    /// Only completed cycles count: an in-flight cycle's "velocity" is a partial
    /// figure that would drag the average down every time it were read mid-sprint.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/metrics/velocity")]
    public async Task<ActionResult<VelocityDto>> Velocity(Guid teamId, [FromQuery] int? take)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var now = DateTimeOffset.UtcNow;

        var cycles = await db.Cycles
            .Where(c => c.TeamId == teamId && c.EndsAt <= now) // completed: half-open [start, end)
            .OrderBy(c => c.Number)
            .ToListAsync();

        var issues = await db.Issues
            .Where(i => i.CycleId != null && i.Cycle!.TeamId == teamId)
            .Select(i => new { CycleId = i.CycleId!.Value, i.Estimate, Type = i.State!.Type })
            .ToListAsync();

        var byCycle = issues.ToLookup(i => i.CycleId);

        var series = cycles.Select(c =>
        {
            var scope = byCycle[c.Id].Where(i => i.Type != WorkflowStateType.Canceled).ToList();
            var completed = scope.Where(i => i.Type == WorkflowStateType.Done).ToList();
            return new VelocityCycleDto(
                c.Id,
                c.Number,
                c.Name,
                c.StartsAt,
                c.EndsAt,
                CommittedPoints: scope.Sum(i => i.Estimate ?? 0),
                CompletedPoints: completed.Sum(i => i.Estimate ?? 0),
                CompletedIssues: completed.Count);
        }).ToList();

        // The trailing window: the most recent `take` completed cycles, if asked.
        if (take is { } n && n > 0 && n < series.Count)
        {
            series = series.Skip(series.Count - n).ToList();
        }

        var average = series.Count == 0 ? 0 : Math.Round(series.Average(c => c.CompletedPoints), 1);

        return Ok(new VelocityDto(teamId, series, average));
    }

    /// <summary>
    /// A cycle's burndown and scope-change series: remaining points per day against
    /// the ideal, with the scope line carried alongside so work added mid-cycle is
    /// visible rather than hidden.
    /// </summary>
    [HttpGet("api/cycles/{id:guid}/burndown")]
    public async Task<ActionResult<BurndownDto>> Burndown(Guid id)
    {
        var cycle = await db.Cycles.FindAsync(id);
        if (cycle is null || !await access.CanAccessTeamAsync(User, cycle.TeamId))
        {
            return this.NotFoundProblem("Cycle", id);
        }

        var now = DateTimeOffset.UtcNow;

        var issues = await db.Issues
            .Where(i => i.CycleId == id)
            .Select(i => new IssueFacts(i.Id, i.CreatedAt, i.State!.Type, i.Estimate))
            .ToListAsync();

        var lifecycles = await LifecyclesAsync(cycle.TeamId, issues);

        // Canceled work is not scope, exactly as the cycle summary treats it.
        var scope = issues.Where(i => i.CurrentType != WorkflowStateType.Canceled).ToList();

        var scopeItems = scope
            .Select(i => new BurndownScopeItem(i.Estimate ?? 0, i.CreatedAt, lifecycles[i.Id].CompletedAt))
            .ToList();

        var series = BurndownChart.Build(scopeItems, cycle.StartsAt, cycle.EndsAt, now)
            .Select(p => new BurndownPointDto(p.Date, p.ScopePoints, p.CompletedPoints, p.RemainingPoints, p.IdealRemaining))
            .ToList();

        return Ok(new BurndownDto(
            cycle.Id,
            cycle.Number,
            cycle.Name,
            cycle.StartsAt,
            cycle.EndsAt,
            CycleSchedule.StatusAt(cycle.StartsAt, cycle.EndsAt, now),
            ScopePoints: scopeItems.Sum(i => i.Points),
            ScopeAddedPoints: scopeItems.Where(i => i.EnteredAt > cycle.StartsAt).Sum(i => i.Points),
            CompletedPoints: scopeItems.Where(i => i.CompletedAt is not null).Sum(i => i.Points),
            Series: series));
    }

    /// <summary>
    /// Issues completed per day or week over a window, optionally narrowed to a
    /// project or an assignee. The buckets tile the whole window — a week nothing
    /// shipped is a zero, not a gap.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/metrics/throughput")]
    public async Task<ActionResult<ThroughputDto>> Throughput(
        Guid teamId,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] MetricInterval interval = MetricInterval.Day,
        [FromQuery] Guid? projectId = null,
        [FromQuery] string? assignee = null)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var (start, end) = ResolveWindow(from, to);

        var completions = (await CompletedInWindowAsync(teamId, start, end, projectId, assignee))
            .Select(c => c.CompletedAt!.Value)
            .ToList();

        var counts = completions
            .GroupBy(at => MetricMath.BucketStart(at, interval))
            .ToDictionary(g => g.Key, g => g.Count());

        var buckets = new List<ThroughputBucketDto>();
        for (var bucket = MetricMath.BucketStart(start, interval); bucket < end; bucket = MetricMath.NextBucket(bucket, interval))
        {
            buckets.Add(new ThroughputBucketDto(bucket, counts.GetValueOrDefault(bucket, 0)));
        }

        return Ok(new ThroughputDto(teamId, interval, start, end, completions.Count, buckets));
    }

    /// <summary>
    /// Cycle-time percentiles (p50/p75/p90, in hours) over issues completed in the
    /// window. Null percentiles when nothing completed — an honest "no data" rather
    /// than a percentile of an empty set.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/metrics/cycle-time")]
    public async Task<ActionResult<CycleTimeDto>> CycleTime(
        Guid teamId,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] Guid? projectId = null,
        [FromQuery] string? assignee = null)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var (start, end) = ResolveWindow(from, to);

        var completed = await CompletedInWindowAsync(teamId, start, end, projectId, assignee);

        var hours = completed
            .Select(c => c.Lifecycle.CycleTime)
            .Where(t => t is not null)
            .Select(t => t!.Value.TotalHours)
            .OrderBy(h => h)
            .ToList();

        double? Pct(int p) => hours.Count == 0 ? null : Math.Round(MetricMath.Percentile(hours, p), 2);

        return Ok(new CycleTimeDto(teamId, start, end, hours.Count, Pct(50), Pct(75), Pct(90)));
    }

    /// <summary>An issue reduced to what the lifecycle reconstruction needs.</summary>
    private sealed record IssueFacts(Guid Id, DateTimeOffset CreatedAt, WorkflowStateType CurrentType, int? Estimate);

    /// <summary>A completed issue with its reconstructed lifecycle.</summary>
    private sealed record CompletedIssue(Guid Id, int? Estimate, IssueLifecycle Lifecycle)
    {
        public DateTimeOffset? CompletedAt => Lifecycle.CompletedAt;
    }

    private (DateTimeOffset From, DateTimeOffset To) ResolveWindow(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);
        return (start, end);
    }

    /// <summary>
    /// The team's issues that reached Done with a completion instant inside
    /// <c>[from, to)</c>, after any project/assignee narrowing. Half-open on the
    /// upper bound so consecutive windows tile without double-counting, matching
    /// the activity feed and the cycle intervals.
    /// </summary>
    private async Task<List<CompletedIssue>> CompletedInWindowAsync(
        Guid teamId,
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? projectId,
        string? assignee)
    {
        var query = db.Issues.Where(i => i.TeamId == teamId);

        if (projectId is { } pid)
        {
            query = query.Where(i => i.ProjectId == pid);
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            var handle = assignee.Trim().ToLower();
            query = query.Where(i => i.Assignee != null && i.Assignee.ToLower() == handle);
        }

        var issues = await query
            .Select(i => new IssueFacts(i.Id, i.CreatedAt, i.State!.Type, i.Estimate))
            .ToListAsync();

        var lifecycles = await LifecyclesAsync(teamId, issues);

        return issues
            .Select(i => new CompletedIssue(i.Id, i.Estimate, lifecycles[i.Id]))
            .Where(c => c.CompletedAt is { } at && at >= from && at < to)
            .ToList();
    }

    /// <summary>
    /// Reconstructs each issue's lifecycle from the team's state-change history.
    ///
    /// <para>
    /// The team's <see cref="ActivityType.IssueStateChanged"/> entries are loaded
    /// once and grouped, rather than queried per issue: the feed is team-scoped and
    /// indexed that way, so one read beats N. A transition's target state is its
    /// recorded name mapped back to a category through the team's current states;
    /// names that no longer map are dropped.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, IssueLifecycle>> LifecyclesAsync(Guid teamId, IReadOnlyList<IssueFacts> issues)
    {
        var typeByStateName = await db.WorkflowStates
            .Where(s => s.TeamId == teamId)
            .ToDictionaryAsync(s => s.Name, s => s.Type);

        var transitions = await db.Activities
            .Where(a => a.TeamId == teamId && a.Type == ActivityType.IssueStateChanged)
            .Select(a => new { a.IssueId, a.NewValue, a.CreatedAt })
            .ToListAsync();

        var transitionsByIssue = transitions
            .Where(t => t.NewValue != null && typeByStateName.ContainsKey(t.NewValue))
            .GroupBy(t => t.IssueId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => new StateTransition(typeByStateName[t.NewValue!], t.CreatedAt)).ToList());

        return issues.ToDictionary(
            i => i.Id,
            i => IssueLifecycles.Reconstruct(
                i.CreatedAt,
                i.CurrentType,
                transitionsByIssue.GetValueOrDefault(i.Id, [])));
    }
}

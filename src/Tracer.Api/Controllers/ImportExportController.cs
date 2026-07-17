using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Api.Export;
using Tracer.Api.Search;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Bulk import and export of a team's issues.
///
/// <para>
/// Export reuses the search filters rather than growing its own: "the issues I
/// am looking at" and "the issues I want out" are the same question, and a
/// second filter chain would answer it differently the first time either side
/// changed.
/// </para>
/// </summary>
[ApiController]
public class ImportExportController(
    TracerDbContext db,
    TeamAccess access,
    ActivityRecorder activity) : ControllerBase
{
    /// <summary>
    /// Exports a team's issues as JSON or CSV, narrowed by any of the filters
    /// <c>GET /api/issues</c> accepts.
    ///
    /// Deliberately not paged: an export is the whole of what the filters
    /// select, and a caller who has to reassemble one from 40 pages will get it
    /// wrong. The filters — and the team scope — are the bound on the size.
    /// </summary>
    [HttpGet("api/teams/{teamId:guid}/issues/export")]
    public async Task<IActionResult> Export(Guid teamId, [FromQuery] ExportQuery query)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var team = await db.Teams.FindAsync(teamId);

        var filtered = IssueSearch.ApplyFilters(db.Issues.Where(i => i.TeamId == teamId), query);
        var issues = await IssueSearch.ApplySort(filtered, query)
            .Include(i => i.State)
            .Include(i => i.Labels)
            .Include(i => i.Project)
            .Include(i => i.Cycle)
            .ToListAsync();

        var rows = issues.Select(i => i.ToExportDto(team!.Key)).ToList();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd");

        if (query.Format == ExportFormat.Csv)
        {
            var csv = Csv.Render(ExportMappings.CsvHeaders, rows.Select(r => r.ToCsvRow()));
            // A BOM: without it Excel reads the file as the local ANSI code page
            // and mangles every non-ASCII title.
            return File(
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv",
                $"{team!.Key}-issues-{stamp}.csv");
        }

        return File(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rows, JsonExportFormat),
            "application/json",
            $"{team!.Key}-issues-{stamp}.json");
    }

    /// <summary>
    /// Imports issues into a team, matching each row to an existing issue by its
    /// external id so that sending the same payload twice updates rather than
    /// duplicates.
    ///
    /// <para>
    /// <b>All or nothing.</b> Every row is resolved and validated before any is
    /// written, and one bad row rejects the payload. A half-applied import is the
    /// worst outcome available: the caller cannot tell what landed, and their
    /// obvious next move — fix the file and send it again — is only safe because
    /// the import is idempotent, which is exactly the property a partial write
    /// would have been relied on to provide.
    /// </para>
    /// </summary>
    [HttpPost("api/teams/{teamId:guid}/issues/import")]
    public async Task<ActionResult<ImportReportDto>> Import(Guid teamId, ImportRequest request)
    {
        if (!await access.CanAccessTeamAsync(User, teamId))
        {
            return this.NotFoundProblem("Team", teamId);
        }

        var team = await db.Teams.FindAsync(teamId);
        var plan = await PlanAsync(team!, request.Issues);

        if (!ModelState.IsValid)
        {
            // The same shape model binding already produces for a malformed
            // request, keyed per row — so a client parses one error format
            // whether the payload was bad JSON or a bad reference.
            return ValidationProblem(ModelState);
        }

        var report = new ImportReportDto(
            request.DryRun,
            plan.Count,
            Created: plan.Count(p => p.Existing is null),
            Updated: plan.Count(p => p.Existing is not null));

        if (request.DryRun)
        {
            return Ok(report);
        }

        await ApplyAsync(team!, plan);
        return Ok(report);
    }

    /// <summary>One import row, resolved against what is already in the team.</summary>
    private sealed record PlannedRow(
        ImportIssueRow Row,
        Issue? Existing,
        WorkflowState State,
        Project? Project,
        List<Label> Labels);

    /// <summary>
    /// Resolves and validates every row, writing nothing.
    ///
    /// This is the whole of the difference between a dry run and a real import:
    /// both build this plan, and only one of them goes on to apply it. That is
    /// what makes a green dry run a promise rather than a guess — there is no
    /// second validation path to disagree with the first.
    /// </summary>
    private async Task<List<PlannedRow>> PlanAsync(Team team, IReadOnlyList<ImportIssueRow> rows)
    {
        var states = await db.WorkflowStates.Where(s => s.TeamId == team.Id).OrderBy(s => s.Position).ToListAsync();
        var projects = await db.Projects.Where(p => p.TeamId == team.Id).ToListAsync();
        var labels = await db.Labels.Where(l => l.TeamId == team.Id).ToListAsync();
        var issues = await db.Issues.Where(i => i.TeamId == team.Id).Include(i => i.State).Include(i => i.Labels).ToListAsync();

        var byExternalId = issues
            .Where(i => i.ExternalId is not null)
            .ToDictionary(i => i.ExternalId!, StringComparer.OrdinalIgnoreCase);
        var byNumber = issues.ToDictionary(i => i.Number);

        var plan = new List<PlannedRow>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var externalId = row.ExternalId?.Trim() ?? string.Empty;

            if (externalId.Length > 0 && !seen.TryAdd(externalId, index))
            {
                // Two rows claiming the same identity. Applied in order, one would
                // silently overwrite the other and the report would call it two
                // successes.
                ModelState.AddModelError(
                    $"issues[{index}].externalId",
                    $"Duplicate externalId '{externalId}'; row {seen[externalId]} already claims it.");
                continue;
            }

            var existing = Resolve(team, externalId, byExternalId, byNumber);

            var state = ResolveState(states, row.State, existing, index);
            if (state is null)
            {
                continue;
            }

            // A state change is a state change however it arrives. Import is not
            // a back door around the workflow, for the same reason dragging a
            // card between columns is not one.
            if (existing is not null
                && existing.StateId != state.Id
                && !IssueStateMachine.CanTransition(existing.State!.Type, state.Type))
            {
                ModelState.AddModelError(
                    $"issues[{index}].state",
                    $"Cannot move {team.Key}-{existing.Number} from '{existing.State!.Name}' to '{state.Name}'.");
                continue;
            }

            Project? project = null;
            if (!string.IsNullOrWhiteSpace(row.Project))
            {
                project = projects.FirstOrDefault(p => Same(p.Name, row.Project));
                if (project is null)
                {
                    ModelState.AddModelError(
                        $"issues[{index}].project",
                        $"Unknown project '{row.Project}' for this team.");
                    continue;
                }
            }

            var rowLabels = new List<Label>();
            var unknownLabel = false;
            foreach (var name in row.Labels ?? [])
            {
                var label = labels.FirstOrDefault(l => Same(l.Name, name));
                if (label is null)
                {
                    // Import creates issues, not the team's vocabulary. Inventing a
                    // label here would let a typo in one row silently add a label
                    // nobody chose, to a list everyone shares.
                    ModelState.AddModelError(
                        $"issues[{index}].labels",
                        $"Unknown label '{name}' for this team.");
                    unknownLabel = true;
                    break;
                }

                rowLabels.Add(label);
            }

            if (unknownLabel)
            {
                continue;
            }

            plan.Add(new PlannedRow(row with { ExternalId = externalId }, existing, state, project, rowLabels));
        }

        return plan;
    }

    /// <summary>
    /// Finds the issue a row refers to, in a fixed order:
    /// <list type="number">
    /// <item>an issue already imported under this external id, then</item>
    /// <item>an issue whose own identifier is this string ("ENG-42").</item>
    /// </list>
    ///
    /// <para>
    /// The second rule is what makes an export re-importable into the team it
    /// came from: an issue created here has no external id, so it exports under
    /// its identifier, and without this it would come back as a copy of itself.
    /// The order matters — an explicit mapping is a deliberate statement about
    /// identity and beats a string that merely looks like one of our own names.
    /// </para>
    /// </summary>
    private static Issue? Resolve(
        Team team,
        string externalId,
        Dictionary<string, Issue> byExternalId,
        Dictionary<int, Issue> byNumber)
    {
        if (byExternalId.TryGetValue(externalId, out var mapped))
        {
            return mapped;
        }

        var prefix = team.Key + "-";
        if (externalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(externalId[prefix.Length..], out var number)
            && byNumber.TryGetValue(number, out var byIdentifier))
        {
            return byIdentifier;
        }

        return null;
    }

    private WorkflowState? ResolveState(List<WorkflowState> states, string? name, Issue? existing, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // Unstated means "leave it where it is", and for a new issue the same
            // default a plain create would pick.
            return existing?.State ?? states.FirstOrDefault();
        }

        var state = states.FirstOrDefault(s => Same(s.Name, name));
        if (state is null)
        {
            ModelState.AddModelError($"issues[{index}].state", $"Unknown workflow state '{name}' for this team.");
        }

        return state;
    }

    /// <summary>
    /// Writes the plan. Everything lands in one <c>SaveChanges</c>, so the import
    /// and its audit entries commit together or not at all.
    /// </summary>
    private async Task ApplyAsync(Team team, List<PlannedRow> plan)
    {
        var nextNumber = await db.Issues.Where(i => i.TeamId == team.Id).MaxAsync(i => (int?)i.Number) ?? 0;
        var nextPosition = await db.Issues.Where(i => i.TeamId == team.Id).MaxAsync(i => (double?)i.Position) ?? 0;

        foreach (var (row, existing, state, project, labels) in plan)
        {
            if (existing is null)
            {
                var issue = new Issue
                {
                    TeamId = team.Id,
                    Number = ++nextNumber,
                    ExternalId = row.ExternalId,
                    Title = row.Title,
                    Description = row.Description,
                    Priority = row.Priority,
                    Estimate = row.Estimate,
                    Assignee = row.Assignee,
                    StateId = state.Id,
                    State = state,
                    ProjectId = project?.Id,
                    Position = ++nextPosition,
                    Labels = labels,
                };
                db.Issues.Add(issue);
                await activity.RecordAsync(User, issue, ActivityType.IssueCreated, newValue: issue.Title);
                continue;
            }

            // Captured before the entity is touched: "changed from X to Y" is the
            // whole point of the entries recorded below.
            var before = new
            {
                existing.Title,
                existing.Description,
                existing.Priority,
                existing.Estimate,
                existing.Assignee,
                StateName = existing.State!.Name,
                StateId = existing.StateId,
            };

            existing.Title = row.Title;
            existing.Description = row.Description;
            existing.Priority = row.Priority;
            existing.Estimate = row.Estimate;
            existing.Assignee = row.Assignee;
            existing.ProjectId = project?.Id;
            existing.Labels = labels;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            if (before.StateId != state.Id)
            {
                existing.StateId = state.Id;
                existing.State = state;
                existing.Position = ++nextPosition;
            }

            await activity.RecordFieldEditsAsync(User, existing, before.Title, before.Description, before.Priority, before.Estimate);

            if (!string.Equals(before.Assignee, existing.Assignee, StringComparison.Ordinal))
            {
                await activity.RecordAsync(User, existing, ActivityType.IssueAssigned,
                    oldValue: before.Assignee, newValue: existing.Assignee);
            }

            if (before.StateId != state.Id)
            {
                await activity.RecordAsync(User, existing, ActivityType.IssueStateChanged,
                    oldValue: before.StateName, newValue: state.Name);
            }
        }

        await db.SaveChangesAsync();
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static readonly System.Text.Json.JsonSerializerOptions JsonExportFormat = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

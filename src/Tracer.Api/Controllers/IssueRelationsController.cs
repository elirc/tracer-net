using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Links between issues: relates-to, blocks/blocked-by, duplicates/duplicated-by.
///
/// <para>
/// <b>One fact, one row.</b> A client may say "TRA-9 is blocked by TRA-4"
/// because that is how the situation is actually described, but what gets stored
/// is the single row "TRA-4 blocks TRA-9". The inverse is derived on read, never
/// written. The alternative — a row per direction — means every link is two
/// writes that can disagree: delete one, or fail between them, and the graph
/// claims A blocks B while B is blocked by nothing.
/// </para>
/// <para>
/// <b>Links stay inside a team.</b> The team is the boundary authorization is
/// drawn on, so a cross-team link would either leak the far issue's title into a
/// response the caller may not see, or need per-row redaction on every read of
/// every relation. Refused as a 400 — the same answer this API already gives for
/// another team's label or state.
/// </para>
/// </summary>
[ApiController]
public class IssueRelationsController(TracerDbContext db, TeamAccess access, ActivityRecorder activity) : ControllerBase
{
    [HttpGet("api/issues/{issueId:guid}/relations")]
    public async Task<ActionResult<List<IssueRelationDto>>> ListForIssue(Guid issueId)
    {
        var issue = await FindVisibleAsync(issueId);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        // Both directions: rows this issue points at, and rows pointing at it.
        var rows = await db.IssueRelations
            .Where(r => r.SourceIssueId == issueId || r.TargetIssueId == issueId)
            .Include(r => r.SourceIssue).ThenInclude(i => i!.State)
            .Include(r => r.SourceIssue).ThenInclude(i => i!.Team)
            .Include(r => r.TargetIssue).ThenInclude(i => i!.State)
            .Include(r => r.TargetIssue).ThenInclude(i => i!.Team)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        return Ok(rows.Select(r => ToDto(r, issueId)).ToList());
    }

    [HttpPost("api/issues/{issueId:guid}/relations")]
    public async Task<ActionResult<IssueRelationDto>> Create(Guid issueId, CreateIssueRelationRequest request)
    {
        var issue = await FindVisibleAsync(issueId);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        // Non-null: [Required] on the nullable properties already rejected omissions.
        var (kind, otherId) = (request.Kind!.Value, request.IssueId!.Value);

        if (otherId == issueId)
        {
            return this.DomainRuleProblem(
                "Self-relation.",
                "An issue cannot be related to itself.");
        }

        var other = await db.Issues.Include(i => i.Team).SingleOrDefaultAsync(i => i.Id == otherId);
        if (other is null || other.TeamId != issue.TeamId)
        {
            // Deliberately one answer for "no such issue" and "another team's
            // issue", so this route cannot be used to probe for issues the
            // caller would otherwise get a 404 for.
            return ValidationProblem(title: "Unknown related issue for this team.");
        }

        // Canonicalize first: every spelling of one fact — including "B relates
        // to A" for an existing "A relates to B" — becomes the same tuple, so a
        // plain equality check finds it and, more importantly, so does the
        // unique index below.
        var (sourceId, targetId, type) = IssueRelations.Canonicalize(kind, issueId, otherId);

        if (await db.IssueRelations.AnyAsync(r =>
                r.SourceIssueId == sourceId && r.TargetIssueId == targetId && r.Type == type))
        {
            return AlreadyRelated();
        }

        if (type is IssueRelationType.Blocks or IssueRelationType.Duplicates
            && await WouldCycleAsync(issue.TeamId, type, sourceId, targetId))
        {
            return this.DomainRuleProblem(
                CycleTitle(type),
                CycleDetail(type));
        }

        var relation = new IssueRelation { SourceIssueId = sourceId, TargetIssueId = targetId, Type = type };
        db.IssueRelations.Add(relation);

        // Logged against the issue the caller addressed, phrased the way they
        // phrased it. The row is canonical; the history is what happened.
        activity.Record(User, issue, ActivityType.IssueRelationAdded,
            field: kind.ToString(),
            newValue: $"{other.Team!.Key}-{other.Number}");

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The check above and this catch are not redundant. The check is what
            // makes the common case a clean 409 without an exception; the unique
            // index is what is actually true, because two concurrent requests can
            // both pass the check before either writes. Without this translation
            // the loser of that race gets a 500 for what is plainly a conflict.
            return AlreadyRelated();
        }

        var created = await db.IssueRelations
            .Include(r => r.SourceIssue).ThenInclude(i => i!.State)
            .Include(r => r.SourceIssue).ThenInclude(i => i!.Team)
            .Include(r => r.TargetIssue).ThenInclude(i => i!.State)
            .Include(r => r.TargetIssue).ThenInclude(i => i!.Team)
            .SingleAsync(r => r.Id == relation.Id);

        return CreatedAtAction(
            nameof(ListForIssue),
            new { issueId },
            ToDto(created, issueId));
    }

    /// <summary>
    /// Removes a link. Reachable from either end: the row is stored in one
    /// direction, but both issues can see it, so both can cut it.
    /// </summary>
    [HttpDelete("api/issues/{issueId:guid}/relations/{relationId:guid}")]
    public async Task<IActionResult> Delete(Guid issueId, Guid relationId)
    {
        var issue = await FindVisibleAsync(issueId);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var relation = await db.IssueRelations.FindAsync(relationId);
        if (relation is null || (relation.SourceIssueId != issueId && relation.TargetIssueId != issueId))
        {
            return this.NotFoundProblem("Relation on this issue", relationId);
        }

        var otherId = relation.SourceIssueId == issueId ? relation.TargetIssueId : relation.SourceIssueId;
        activity.Record(User, issue, ActivityType.IssueRelationRemoved,
            field: IssueRelations.AsSeenFrom(relation.Type, relation.SourceIssueId == issueId).ToString(),
            oldValue: await IdentifierOfAsync(otherId));

        db.IssueRelations.Remove(relation);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> IdentifierOfAsync(Guid issueId)
    {
        var found = await db.Issues
            .Where(i => i.Id == issueId)
            .Select(i => new { i.Number, TeamKey = i.Team!.Key })
            .SingleOrDefaultAsync();

        return found is null ? null : $"{found.TeamKey}-{found.Number}";
    }

    private async Task<Issue?> FindVisibleAsync(Guid issueId)
    {
        var issue = await db.Issues.FindAsync(issueId);
        return issue is not null && await access.CanAccessTeamAsync(User, issue.TeamId) ? issue : null;
    }

    /// <summary>
    /// True when storing <paramref name="sourceId"/> → <paramref name="targetId"/>
    /// would close a loop — i.e. when a path already leads back from the target to
    /// the source.
    ///
    /// The team's edges of that type are loaded once and walked in memory. A
    /// recursive CTE would push the walk into SQL, but a team's relation graph is
    /// small and the traversal is a domain rule; keeping it in
    /// <see cref="IssueGraph"/> means it can be tested without a database.
    /// </summary>
    private async Task<bool> WouldCycleAsync(Guid teamId, IssueRelationType type, Guid sourceId, Guid targetId)
    {
        var rows = await db.IssueRelations
            .Where(r => r.Type == type && r.SourceIssue!.TeamId == teamId)
            .Select(r => new { r.SourceIssueId, r.TargetIssueId })
            .ToListAsync();

        var edges = rows
            .GroupBy(r => r.SourceIssueId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.TargetIssueId).ToList());

        return IssueGraph.Reaches(edges, targetId, sourceId);
    }

    private ObjectResult AlreadyRelated() =>
        this.ConflictProblem(
            "Relation already exists.",
            "These two issues are already linked that way.");

    /// <summary>
    /// True when a failed save was the relations table's uniqueness rejecting a
    /// duplicate. Matched narrowly on the SQLite constraint code: catching every
    /// <see cref="DbUpdateException"/> would report unrelated write failures as
    /// conflicts, which is how a 500 gets dressed up as a 409 and stops being
    /// investigated.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode };

    /// <summary>SQLITE_CONSTRAINT — a constraint (here, the unique index) refused the row.</summary>
    private const int SqliteConstraintErrorCode = 19;

    private static string CycleTitle(IssueRelationType type) =>
        type == IssueRelationType.Blocks ? "Circular blocking." : "Circular duplication.";

    private static string CycleDetail(IssueRelationType type) => type == IssueRelationType.Blocks
        ? "That issue already blocks this one, directly or through a chain. A blocking loop is a deadlock: neither issue could ever start."
        : "That issue is already a duplicate of this one, directly or through a chain.";

    private static IssueRelationDto ToDto(IssueRelation relation, Guid viewedFrom)
    {
        var fromSource = relation.SourceIssueId == viewedFrom;
        var other = fromSource ? relation.TargetIssue! : relation.SourceIssue!;

        return new IssueRelationDto(
            relation.Id,
            IssueRelations.AsSeenFrom(relation.Type, fromSource),
            other.Id,
            $"{other.Team!.Key}-{other.Number}",
            other.Title,
            other.State!.Name,
            relation.CreatedAt);
    }
}

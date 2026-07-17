using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// Comments.
///
/// <para>
/// <b>Authorship is taken from the credential, never from the body.</b> Before
/// there was a caller to ask, <c>author</c> was a field on the request, which
/// meant anyone could post as anyone — the API had no way to know better. Now
/// the authenticated handle is the author, and the field is gone from the
/// contract rather than ignored: a field that is silently overwritten is worse
/// than one that was never offered, because the client believes it worked.
/// </para>
/// <para>
/// Editing and deleting are the author's own (or an admin's). Team membership
/// gets you into the conversation; it does not let you rewrite what a teammate
/// said.
/// </para>
/// </summary>
[ApiController]
public class CommentsController(TracerDbContext db, TeamAccess access, ActivityRecorder activity) : ControllerBase
{
    [HttpGet("api/issues/{issueId:guid}/comments")]
    public async Task<ActionResult<PagedResult<CommentDto>>> ListForIssue(Guid issueId, [FromQuery] PageQuery paging)
    {
        if (await FindVisibleIssueAsync(issueId) is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var comments = await db.Comments
            .Where(c => c.IssueId == issueId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => new CommentDto(c.Id, c.IssueId, c.Author, c.Body, c.CreatedAt))
            .ToPagedResultAsync(paging);
        return Ok(comments);
    }

    [HttpPost("api/issues/{issueId:guid}/comments")]
    public async Task<ActionResult<CommentDto>> Create(Guid issueId, CreateCommentRequest request)
    {
        var issue = await FindVisibleIssueAsync(issueId);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var comment = new Comment { IssueId = issueId, Author = User.Handle(), Body = request.Body };
        db.Comments.Add(comment);
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await activity.RecordAsync(User, issue, ActivityType.CommentCreated, newValue: Excerpt(comment.Body));
        await db.SaveChangesAsync();

        var dto = new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt);
        return CreatedAtAction(nameof(GetById), new { id = comment.Id }, dto);
    }

    [HttpGet("api/comments/{id:guid}")]
    public async Task<ActionResult<CommentDto>> GetById(Guid id)
    {
        var comment = await FindVisibleCommentAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        return Ok(new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt));
    }

    [HttpPut("api/comments/{id:guid}")]
    public async Task<ActionResult<CommentDto>> Update(Guid id, UpdateCommentRequest request)
    {
        var comment = await FindVisibleCommentAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        if (!MayEdit(comment))
        {
            return NotTheAuthor("edit");
        }

        var issue = (await FindVisibleIssueAsync(comment.IssueId))!;
        comment.Body = request.Body;
        await activity.RecordAsync(User, issue, ActivityType.CommentUpdated, newValue: Excerpt(comment.Body));
        await db.SaveChangesAsync();

        return Ok(new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt));
    }

    [HttpDelete("api/comments/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var comment = await FindVisibleCommentAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        if (!MayEdit(comment))
        {
            return NotTheAuthor("delete");
        }

        var issue = (await FindVisibleIssueAsync(comment.IssueId))!;
        await activity.RecordAsync(User, issue, ActivityType.CommentDeleted, oldValue: Excerpt(comment.Body));
        db.Comments.Remove(comment);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// A short, bounded excerpt for the feed. The log stores what was said, not
    /// the whole of it: comment bodies run to ten thousand characters, and an
    /// append-only table that keeps a full copy of every edit of every one of
    /// them stops being an audit log and becomes a second, worse copy of the
    /// comments table.
    /// </summary>
    private static string Excerpt(string body) =>
        body.Length <= ExcerptLength ? body : body[..ExcerptLength] + "…";

    private const int ExcerptLength = 120;

    /// <summary>The issue, if the caller's teams reach it; otherwise null, which callers report as 404.</summary>
    private async Task<Issue?> FindVisibleIssueAsync(Guid issueId)
    {
        var issue = await db.Issues.FindAsync(issueId);
        return issue is not null && await access.CanAccessTeamAsync(User, issue.TeamId) ? issue : null;
    }

    private async Task<Comment?> FindVisibleCommentAsync(Guid id)
    {
        var comment = await db.Comments.FindAsync(id);
        if (comment is null)
        {
            return null;
        }

        return await FindVisibleIssueAsync(comment.IssueId) is null ? null : comment;
    }

    /// <summary>
    /// Compared case-insensitively: handles are stored lowercase, but the author
    /// string is free-form history and an old row may not be.
    /// </summary>
    private bool MayEdit(Comment comment) =>
        User.IsAdmin() || string.Equals(comment.Author, User.Handle(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A 403 rather than a 404: the caller can see this comment — it is on an
    /// issue in one of their teams, and they can read it through the very same
    /// route — so pretending it does not exist would only confuse them. Nothing
    /// is disclosed by admitting that somebody else wrote it.
    /// </summary>
    private ObjectResult NotTheAuthor(string verb) =>
        Problem(
            title: $"Not allowed to {verb} another user's comment.",
            detail: $"Only the author of a comment, or an admin, can {verb} it.",
            statusCode: StatusCodes.Status403Forbidden);
}

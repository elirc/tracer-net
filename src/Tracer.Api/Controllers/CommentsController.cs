using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Contracts;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

[ApiController]
public class CommentsController(TracerDbContext db) : ControllerBase
{
    [HttpGet("api/issues/{issueId:guid}/comments")]
    public async Task<ActionResult<List<CommentDto>>> ListForIssue(Guid issueId)
    {
        if (!await db.Issues.AnyAsync(i => i.Id == issueId))
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var comments = await db.Comments
            .Where(c => c.IssueId == issueId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CommentDto(c.Id, c.IssueId, c.Author, c.Body, c.CreatedAt))
            .ToListAsync();
        return Ok(comments);
    }

    [HttpPost("api/issues/{issueId:guid}/comments")]
    public async Task<ActionResult<CommentDto>> Create(Guid issueId, CreateCommentRequest request)
    {
        var issue = await db.Issues.FindAsync(issueId);
        if (issue is null)
        {
            return this.NotFoundProblem("Issue", issueId);
        }

        var comment = new Comment { IssueId = issueId, Author = request.Author, Body = request.Body };
        db.Comments.Add(comment);
        issue.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var dto = new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt);
        return CreatedAtAction(nameof(GetById), new { id = comment.Id }, dto);
    }

    [HttpGet("api/comments/{id:guid}")]
    public async Task<ActionResult<CommentDto>> GetById(Guid id)
    {
        var comment = await db.Comments.FindAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        return Ok(new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt));
    }

    [HttpPut("api/comments/{id:guid}")]
    public async Task<ActionResult<CommentDto>> Update(Guid id, UpdateCommentRequest request)
    {
        var comment = await db.Comments.FindAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        comment.Body = request.Body;
        await db.SaveChangesAsync();

        return Ok(new CommentDto(comment.Id, comment.IssueId, comment.Author, comment.Body, comment.CreatedAt));
    }

    [HttpDelete("api/comments/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var comment = await db.Comments.FindAsync(id);
        if (comment is null)
        {
            return this.NotFoundProblem("Comment", id);
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync();
        return NoContent();
    }
}

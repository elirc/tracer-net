using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record CommentDto(Guid Id, Guid IssueId, string Author, string Body, DateTimeOffset CreatedAt);

// No Author field: the author is the authenticated caller. Accepting one would
// let any member post as anyone, and accepting-then-ignoring it would be worse
// still — the client would be told its impersonation succeeded.
public record CreateCommentRequest(
    [Required, MaxLength(10000)] string Body);

public record UpdateCommentRequest(
    [Required, MaxLength(10000)] string Body);

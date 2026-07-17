using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record CommentDto(Guid Id, Guid IssueId, string Author, string Body, DateTimeOffset CreatedAt);

public record CreateCommentRequest(
    [Required, MaxLength(100)] string Author,
    [Required, MaxLength(10000)] string Body);

public record UpdateCommentRequest(
    [Required, MaxLength(10000)] string Body);

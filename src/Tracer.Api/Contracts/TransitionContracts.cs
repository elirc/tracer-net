using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

// Nullable so that [Required] rejects an omitted StateId with 400. On a
// non-nullable Guid it is a no-op, and the field would bind to Guid.Empty and be
// reported as an unknown workflow state instead of the missing field that it is.
public record TransitionIssueRequest([Required] Guid? StateId);

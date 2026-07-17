using System.ComponentModel.DataAnnotations;

namespace Tracer.Api.Contracts;

public record TransitionIssueRequest([Required] Guid StateId);

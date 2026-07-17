using Microsoft.AspNetCore.Mvc;

namespace Tracer.Api.Controllers;

/// <summary>
/// One way to report an error, so clients only ever parse one shape.
///
/// Everything goes through <see cref="ControllerBase.Problem(string, string, int?, string, string)"/>
/// rather than newing up a <see cref="ProblemDetails"/>: that routes the response
/// through the registered <c>ProblemDetailsFactory</c>, which fills in the RFC 7807
/// <c>type</c> link and a <c>traceId</c> for correlation. Hand-built ProblemDetails
/// silently skip both, which is how an API ends up with several different error
/// shapes that are all "RFC 7807".
///
/// The status codes carry meaning:
/// <list type="bullet">
/// <item>400 — the request itself is malformed, or points at something that is not
/// the caller's to point at (another team's state, a label from another team).</item>
/// <item>404 — the addressed resource does not exist.</item>
/// <item>409 — the request is well-formed but collides with existing data
/// (a duplicate key, an overlapping cycle).</item>
/// <item>422 — the request is well-formed and consistent, but a domain rule
/// forbids the outcome (an illegal workflow transition, a backwards date range).</item>
/// </list>
/// </summary>
public static class ApiProblems
{
    public static ObjectResult NotFoundProblem(this ControllerBase controller, string resource, Guid id) =>
        controller.Problem(
            title: $"{resource} not found.",
            detail: $"No {resource.ToLowerInvariant()} exists with id {id}.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>The request collides with data that already exists.</summary>
    public static ObjectResult ConflictProblem(this ControllerBase controller, string title, string detail) =>
        controller.Problem(title: title, detail: detail, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// A write lost an optimistic-concurrency race: the row changed between the
    /// caller reading it and saving. A 409, like every other collision, and
    /// deliberately not a 500 — the request was well-formed, it was simply built
    /// on a copy that has since moved.
    /// </summary>
    public static ObjectResult ConcurrencyConflictProblem(this ControllerBase controller, string resource) =>
        controller.Problem(
            title: $"{resource} was modified by someone else.",
            detail: $"This {resource.ToLowerInvariant()} changed after you loaded it. Re-read it and reapply your change.",
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>The request is understood and coherent, but a domain rule forbids it.</summary>
    public static ObjectResult DomainRuleProblem(this ControllerBase controller, string title, string detail) =>
        controller.Problem(title: title, detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity);
}

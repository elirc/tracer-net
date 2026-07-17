using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

/// <param name="Owner">Handle of the owner of a personal view; null for a team view.</param>
/// <param name="Rules">The view's filters, as an object — clients should not have to parse a string out of the response.</param>
public record SavedViewDto(
    Guid Id,
    Guid TeamId,
    string Name,
    SavedViewScope Scope,
    string? Owner,
    bool IsDefault,
    IssueFilter Rules,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CreateSavedViewRequest(
    [Required, MaxLength(100)] string Name,
    SavedViewScope Scope = SavedViewScope.Team,
    IssueFilter? Rules = null,
    bool IsDefault = false);

public record UpdateSavedViewRequest(
    [Required, MaxLength(100)] string Name,
    SavedViewScope Scope = SavedViewScope.Team,
    IssueFilter? Rules = null,
    bool IsDefault = false);

/// <summary>
/// Paging for <c>GET /api/views/{id}/issues</c>. The view supplies the filters
/// and the sort; the request supplies only where the caller is in the results.
/// </summary>
public record ExecuteSavedViewQuery
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 25;
}

public static class SavedViewMappings
{
    /// <summary>
    /// How rules are stored. Enums are written as names and nulls are dropped, so
    /// a stored rule set reads as what it is when someone opens the database, and
    /// renumbering an enum cannot silently repoint every view at a different
    /// priority. Property names are matched case-insensitively on read, matching
    /// how the same JSON arrives over HTTP.
    /// </summary>
    private static readonly JsonSerializerOptions RuleFormat = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(IssueFilter rules) => JsonSerializer.Serialize(rules, RuleFormat);

    /// <summary>
    /// Throws rather than falling back to an empty filter: an unreadable rule set
    /// means the row is corrupt, and "show every issue in the team" is the most
    /// dangerous possible guess at what a filter that failed to parse meant.
    /// </summary>
    public static IssueFilter Deserialize(string filterJson) =>
        JsonSerializer.Deserialize<IssueFilter>(filterJson, RuleFormat)
        ?? throw new InvalidOperationException("Saved view has unreadable rules.");

    public static SavedViewDto ToDto(this SavedView view) => new(
        view.Id,
        view.TeamId,
        view.Name,
        view.Scope,
        view.Owner?.Handle,
        view.IsDefault,
        Deserialize(view.FilterJson),
        view.CreatedAt,
        view.UpdatedAt);
}

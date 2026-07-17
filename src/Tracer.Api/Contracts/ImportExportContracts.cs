using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

public enum ExportFormat
{
    Json = 0,
    Csv = 1,
}

/// <summary>
/// An issue as it leaves the system.
///
/// <para>
/// References are names, not ids. A guid means nothing to the spreadsheet, the
/// script, or the other tracker on the receiving end, and it would mean
/// something wrong if the payload were ever imported somewhere else.
/// </para>
/// </summary>
public record ExportIssueDto(
    string Identifier,
    string ExternalId,
    string Title,
    string? Description,
    IssuePriority Priority,
    int? Estimate,
    string? Assignee,
    string State,
    string? Project,
    string? Cycle,
    IReadOnlyList<string> Labels,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Export options: a format, plus the same filters search accepts.</summary>
public record ExportQuery : IssueFilter
{
    public ExportFormat Format { get; init; } = ExportFormat.Json;
}

/// <summary>
/// One issue in an import payload.
///
/// <see cref="ExternalId"/> is required, and it is what makes an import
/// repeatable: it is the caller's name for this issue, so sending the same
/// payload twice means the same issues twice, not twice as many issues. A
/// payload without one could only ever append.
/// </summary>
public record ImportIssueRow(
    [Required, MaxLength(200)] string ExternalId,
    [Required, MaxLength(500)] string Title,
    string? Description = null,
    IssuePriority Priority = IssuePriority.None,
    [Range(0, 100)] int? Estimate = null,
    [MaxLength(100)] string? Assignee = null,
    string? State = null,
    string? Project = null,
    IReadOnlyList<string>? Labels = null);

public record ImportRequest(
    [Required] IReadOnlyList<ImportIssueRow> Issues,
    bool DryRun = false);

/// <summary>
/// What an import did, or — for a dry run — what it would have done.
///
/// The two are the same numbers from the same code: a dry run is a real import
/// that stops before saving, so a dry run reporting 3 created and 2 updated is a
/// promise about the run that follows, not a separate estimate of it.
/// </summary>
public record ImportReportDto(bool DryRun, int Total, int Created, int Updated);

public static class ExportMappings
{
    /// <summary>
    /// Column order for CSV. It matches the JSON field order so the two formats
    /// are recognisably the same export.
    /// </summary>
    public static readonly string[] CsvHeaders =
    [
        "identifier", "externalId", "title", "description", "priority", "estimate",
        "assignee", "state", "project", "cycle", "labels", "createdAt", "updatedAt",
    ];

    /// <summary>Separator for the multi-valued labels column.</summary>
    public const string LabelSeparator = "|";

    public static ExportIssueDto ToExportDto(this Issue issue, string teamKey) => new(
        $"{teamKey}-{issue.Number}",
        // An issue created here has no external identity, so it exports under the
        // only stable name it has — its own identifier. That is also the name it
        // would come back under, which is what makes an export re-importable
        // without duplicating everything in it.
        issue.ExternalId ?? $"{teamKey}-{issue.Number}",
        issue.Title,
        issue.Description,
        issue.Priority,
        issue.Estimate,
        issue.Assignee,
        issue.State!.Name,
        issue.Project?.Name,
        issue.Cycle?.Name,
        issue.Labels.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
        issue.CreatedAt,
        issue.UpdatedAt);

    public static IReadOnlyList<string?> ToCsvRow(this ExportIssueDto issue) =>
    [
        issue.Identifier,
        issue.ExternalId,
        issue.Title,
        issue.Description,
        issue.Priority.ToString(),
        issue.Estimate?.ToString(),
        issue.Assignee,
        issue.State,
        issue.Project,
        issue.Cycle,
        string.Join(LabelSeparator, issue.Labels),
        issue.CreatedAt.ToString("O"),
        issue.UpdatedAt.ToString("O"),
    ];
}

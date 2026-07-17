using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

/// <summary>
/// A team's velocity: completed story points per completed cycle, plus the
/// trailing average that a plan for the next cycle should be built on.
/// </summary>
public record VelocityDto(
    Guid TeamId,
    IReadOnlyList<VelocityCycleDto> Cycles,
    double AverageVelocity);

/// <summary>One completed cycle's contribution to velocity.</summary>
public record VelocityCycleDto(
    Guid CycleId,
    int Number,
    string? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int CommittedPoints,
    int CompletedPoints,
    int CompletedIssues);

/// <summary>A cycle's burndown and scope-change series.</summary>
public record BurndownDto(
    Guid CycleId,
    int Number,
    string? Name,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    CycleStatus Status,
    int ScopePoints,
    int ScopeAddedPoints,
    int CompletedPoints,
    IReadOnlyList<BurndownPointDto> Series);

public record BurndownPointDto(
    DateTimeOffset Date,
    int ScopePoints,
    int CompletedPoints,
    int RemainingPoints,
    double IdealRemaining);

/// <summary>Completed-issue counts bucketed over time.</summary>
public record ThroughputDto(
    Guid TeamId,
    MetricInterval Interval,
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalCompleted,
    IReadOnlyList<ThroughputBucketDto> Buckets);

public record ThroughputBucketDto(DateTimeOffset BucketStart, int Completed);

/// <summary>
/// Cycle-time percentiles over completed issues in a window, in hours. The
/// percentiles are null when nothing completed in the window — a percentile of an
/// empty sample is a number that means "we made it up", and the count says so
/// plainly instead.
/// </summary>
public record CycleTimeDto(
    Guid TeamId,
    DateTimeOffset From,
    DateTimeOffset To,
    int CompletedIssues,
    double? P50Hours,
    double? P75Hours,
    double? P90Hours);

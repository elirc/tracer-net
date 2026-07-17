using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

/// <summary>
/// An inbox entry. It carries the <see cref="Activity"/> whole rather than a
/// copy, so the client renders a notification exactly as it renders a feed row —
/// one shape, not two that can drift.
/// </summary>
public record NotificationDto(
    Guid Id,
    bool IsRead,
    DateTimeOffset? ReadAt,
    DateTimeOffset CreatedAt,
    ActivityDto Activity);

public record UnreadCountDto(int Unread);

public record SubscriptionDto(bool Subscribed, SubscriptionReason? Reason);

public record IssueSubscriberDto(Guid UserId, string Handle, string Name, SubscriptionReason Reason);

public record NotificationInboxQuery
{
    /// <summary>When true, only unread. Absent means the whole inbox.</summary>
    public bool? Unread { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public static class NotificationMappings
{
    /// <summary>Requires <see cref="Notification.Activity"/> and its team to be loaded.</summary>
    public static NotificationDto ToDto(this Notification notification) => new(
        notification.Id,
        notification.ReadAt is not null,
        notification.ReadAt,
        notification.CreatedAt,
        notification.Activity!.ToDto());
}

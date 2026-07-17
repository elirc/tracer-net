using System.ComponentModel.DataAnnotations;
using Tracer.Domain.Entities;

namespace Tracer.Api.Contracts;

/// <summary>A webhook as it can safely be read back: everything except the secret.</summary>
public record WebhookDto(
    Guid Id,
    Guid TeamId,
    string Name,
    string Url,
    IReadOnlyList<WebhookEvent> Events,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// The only response carrying the signing secret — returned when a webhook is
/// created or its secret rotated, and never by a read.
///
/// Unlike an API key this cannot be stored hashed: signing a payload requires the
/// secret itself, and a hash cannot sign. Since storage can't protect it,
/// exposure is what's limited instead.
/// </summary>
public record CreatedWebhookDto(
    Guid Id,
    Guid TeamId,
    string Name,
    string Url,
    IReadOnlyList<WebhookEvent> Events,
    bool IsActive,
    string Secret,
    DateTimeOffset CreatedAt);

public record WebhookSecretDto(Guid Id, string Secret);

public record CreateWebhookRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(2000)] string Url,
    [Required, MinLength(1)] List<WebhookEvent>? Events);

public record UpdateWebhookRequest(
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(2000)] string Url,
    [Required, MinLength(1)] List<WebhookEvent>? Events,
    bool IsActive = true);

public record WebhookDeliveryDto(
    Guid Id,
    Guid WebhookId,
    WebhookEvent Event,
    WebhookDeliveryStatus Status,
    int AttemptCount,
    int? ResponseStatusCode,
    WebhookFailureClass FailureClass,
    string? Error,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// A delivery plus the exact bytes that were signed and sent — the thing a team
/// actually needs when their endpoint rejected something and they want to know
/// what it rejected.
/// </summary>
public record WebhookDeliveryDetailDto(WebhookDeliveryDto Delivery, string Payload);

public record WebhookDeliveryQuery
{
    public WebhookDeliveryStatus? Status { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public static class WebhookMappings
{
    public static WebhookDto ToDto(this Webhook webhook) =>
        new(webhook.Id, webhook.TeamId, webhook.Name, webhook.Url, webhook.Events, webhook.IsActive, webhook.CreatedAt);

    public static WebhookDeliveryDto ToDto(this WebhookDelivery delivery) => new(
        delivery.Id,
        delivery.WebhookId,
        delivery.Event,
        delivery.Status,
        delivery.AttemptCount,
        delivery.ResponseStatusCode,
        delivery.FailureClass,
        delivery.Error,
        delivery.LastAttemptAt,
        delivery.DeliveredAt,
        delivery.NextAttemptAt,
        delivery.CreatedAt);
}

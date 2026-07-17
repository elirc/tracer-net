using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tracer.Api.Auth;
using Tracer.Api.Contracts;
using Tracer.Infrastructure;

namespace Tracer.Api.Controllers;

/// <summary>
/// A user's own inbox.
///
/// Every route here is scoped to the caller by construction: a notification
/// belongs to exactly one person, so "is this mine?" is the whole of the
/// authorization, and there is no team to check. Another user's notification is
/// a 404 — not because it is secret, but because addressing it at all is a
/// mistake, and the honest answer to "give me a thing that is not yours" is that
/// there is no such thing here.
/// </summary>
[ApiController]
[Route("api/notifications")]
public class NotificationsController(TracerDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> Inbox([FromQuery] NotificationInboxQuery query)
    {
        var mine = db.Notifications.Where(n => n.UserId == User.UserId());

        if (query.Unread == true)
        {
            mine = mine.Where(n => n.ReadAt == null);
        }

        var total = await mine.CountAsync();

        var results = await mine
            .OrderByDescending(n => n.CreatedAt)
            .ThenBy(n => n.Id) // notifications fanned out from one save share a timestamp exactly
            .Include(n => n.Activity).ThenInclude(a => a!.Team)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return Ok(new PagedResult<NotificationDto>(
            results.Select(n => n.ToDto()).ToList(),
            query.Page,
            query.PageSize,
            total,
            (int)Math.Ceiling(total / (double)query.PageSize)));
    }

    /// <summary>The unread badge. Its own endpoint so a client can poll it without dragging the whole inbox.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountDto>> UnreadCount()
    {
        var unread = await db.Notifications.CountAsync(n => n.UserId == User.UserId() && n.ReadAt == null);
        return Ok(new UnreadCountDto(unread));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<NotificationDto>> MarkRead(Guid id) => await SetReadAsync(id, read: true);

    /// <summary>Marks one unread again — the undo for a misclick, so "read" is not a one-way door.</summary>
    [HttpPost("{id:guid}/unread")]
    public async Task<ActionResult<NotificationDto>> MarkUnread(Guid id) => await SetReadAsync(id, read: false);

    /// <summary>
    /// Clears the badge in one call. Scoped to the caller's own unread rows, and
    /// idempotent: an already-empty inbox reports zero rather than erroring.
    /// </summary>
    [HttpPost("read-all")]
    public async Task<ActionResult<UnreadCountDto>> MarkAllRead()
    {
        // One set-based UPDATE rather than loading every unread row to touch it:
        // an inbox that has been ignored for a month should not have to be
        // materialised in full just to be dismissed.
        await db.Notifications
            .Where(n => n.UserId == User.UserId() && n.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow));

        return Ok(new UnreadCountDto(0));
    }

    private async Task<ActionResult<NotificationDto>> SetReadAsync(Guid id, bool read)
    {
        var notification = await db.Notifications
            .Include(n => n.Activity).ThenInclude(a => a!.Team)
            .SingleOrDefaultAsync(n => n.Id == id && n.UserId == User.UserId());
        if (notification is null)
        {
            return this.NotFoundProblem("Notification", id);
        }

        // A timestamp, not a bool: recording when it was read is free here and
        // answers "how long did this sit unseen" later. Re-marking read keeps the
        // original instant rather than sliding it forward on every glance.
        if (read && notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        else if (!read && notification.ReadAt is not null)
        {
            notification.ReadAt = null;
            await db.SaveChangesAsync();
        }

        return Ok(notification.ToDto());
    }
}

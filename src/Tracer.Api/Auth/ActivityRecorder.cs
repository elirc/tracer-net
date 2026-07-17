using System.Security.Claims;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Api.Auth;

/// <summary>
/// Writes the audit trail. Every issue mutation goes through here, so the feed,
/// and everything later built on it, has exactly one source.
///
/// <para>
/// <b>It does not call SaveChanges, and that is the whole point.</b> The activity
/// is added to the same <see cref="TracerDbContext"/> the mutation is sitting in,
/// so the caller's existing <c>SaveChangesAsync</c> commits both in one
/// transaction. Recording separately — a second save, a background queue, an
/// outbox drain — buys a window where the change lands and the record does not,
/// or the reverse. An audit log that disagrees with the data is worse than none,
/// because it is trusted.
/// </para>
/// <para>
/// <b>Why controllers call this rather than a SaveChanges interceptor.</b> An
/// interceptor over the change tracker would be tamper-proof and impossible to
/// forget, which is tempting. But it sees columns, not intent: it cannot tell a
/// re-parent from an assignment without re-deriving the meaning from the diff,
/// it cannot name the actor without reaching for ambient request state, and it
/// would report a rank rewrite during a rebalance — a move of *other* people's
/// cards — as a dozen edits nobody made. The trade is real: this can be
/// forgotten. The tests are where that is caught.
/// </para>
/// </summary>
public sealed class ActivityRecorder(TracerDbContext db)
{
    /// <summary>
    /// One instant for the whole request — this is registered scoped, so it is
    /// stamped once and shared by everything recorded here.
    ///
    /// <para>
    /// Everything this recorder writes commits in a single transaction, so it all
    /// became true at the same moment; reading the clock per entry would instead
    /// record the order the C# happened to run in, spreading one atomic change
    /// across a few microseconds of invented chronology. Editing an issue's title
    /// and priority in one save did not happen twice, one after the other.
    /// </para>
    /// <para>
    /// This is why the feeds break ties on id. Entries from one save now collide
    /// on <see cref="Activity.CreatedAt"/> exactly, and paging a result set whose
    /// order is only "whatever the planner returns" silently repeats and skips
    /// rows.
    /// </para>
    /// </summary>
    private readonly DateTimeOffset _at = DateTimeOffset.UtcNow;

    public Activity Record(
        ClaimsPrincipal user,
        Issue issue,
        ActivityType type,
        string? field = null,
        string? oldValue = null,
        string? newValue = null)
    {
        var activity = new Activity
        {
            TeamId = issue.TeamId,
            IssueId = issue.Id,
            IssueNumber = issue.Number,
            IssueTitle = issue.Title,
            Type = type,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            ActorId = user.UserId(),
            ActorHandle = user.Handle(),
            CreatedAt = _at,
        };

        db.Activities.Add(activity);
        return activity;
    }
}
